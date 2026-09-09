using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using LimbusModEditor.Application.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// BankDirectoryService 单测：合成 bank（全离线）+ 真实游戏目录门控。
/// 真实目录候选方式与 RealBankTests 一致（含 LME_GAME_DIR 覆盖）；
/// 无真实游戏目录时门控测试静默通过。全程只读，从不写游戏目录。
/// </summary>
public class BankDirectoryServiceTests : IDisposable
{
    private static readonly string? BankRoot = FindBankRoot();

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "lme-bankdir-" + Guid.NewGuid().ToString("N"));
    private readonly BankDirectoryService _service = new();

    public BankDirectoryServiceTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch (IOException) { }
    }

    private static string? FindBankRoot()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
        };
        var overrideDir = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            candidates = [Path.Combine(overrideDir, "LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMODBuilds", "Desktop"), .. candidates];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    // ---------- 合成 / 异常测试（全离线） ----------

    [Fact]
    public void ResolveBankDirectory_returns_null_for_missing_or_blank()
    {
        Assert.Null(_service.ResolveBankDirectory(null));
        Assert.Null(_service.ResolveBankDirectory(""));
        Assert.Null(_service.ResolveBankDirectory("   "));
        Assert.Null(_service.ResolveBankDirectory(Path.Combine(_workDir, "不存在的游戏目录")));
    }

    [Fact]
    public void ResolveBankDirectory_resolves_existing_structure()
    {
        var desktop = Path.Combine(_workDir, "LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMODBuilds", "Desktop");
        Directory.CreateDirectory(desktop);
        Assert.Equal(desktop, _service.ResolveBankDirectory(_workDir));
    }

    [Fact]
    public void ScanDirectory_missing_dir_throws_clear_error()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _service.ScanDirectory(Path.Combine(_workDir, "nope")));
    }

    [Fact]
    public void ScanDirectory_classifies_synthetic_and_corrupted_banks()
    {
        var fsb = BuildMinimalFsb5();
        var encrypted = BuildSyntheticBank(fsb);
        encrypted[0x48] = (byte)'X'; // 破坏首个 FSB 的 FSB5 魔数 → 加密
        File.WriteAllBytes(Path.Combine(_workDir, "01-event.bank"), BuildSyntheticBank(null));
        File.WriteAllBytes(Path.Combine(_workDir, "02-audio.bank"), BuildSyntheticBank(fsb));
        File.WriteAllBytes(Path.Combine(_workDir, "03-encrypted.bank"), encrypted);
        File.WriteAllBytes(Path.Combine(_workDir, "04-corrupt.bank"), [0xDE, 0xAD, 0xBE, 0xEF, .. new byte[96]]);
        File.WriteAllBytes(Path.Combine(_workDir, "05-truncated.bank"), BuildSyntheticBank(null)[..20]);
        File.WriteAllText(Path.Combine(_workDir, "readme.txt"), "not a bank");

        var entries = _service.ScanDirectory(_workDir);

        Assert.Equal(5, entries.Count); // 只统计 *.bank，readme.txt 被忽略
        Assert.Equal(
            [BankKind.Event, BankKind.Audio, BankKind.Encrypted, BankKind.Unknown, BankKind.Unknown],
            entries.Select(e => e.Kind).ToArray());
        Assert.Equal(0, entries[0].FsbCount);
        Assert.Equal(1, entries[1].FsbCount);
        Assert.Equal(1, entries[2].FsbCount);
        Assert.All(entries, e =>
        {
            Assert.True(File.Exists(e.FullPath));
            Assert.True(e.FileSizeBytes > 0);
        });
        Assert.Contains("RIFF", entries[3].Detail, StringComparison.Ordinal);
        Assert.NotNull(entries[4].Detail);
    }

    [Fact]
    public void ReadSampleTable_event_bank_lists_riff_chunks()
    {
        var path = Path.Combine(_workDir, "event.bank");
        File.WriteAllBytes(path, BuildSyntheticBank(null));

        var table = _service.ReadSampleTable(path);

        Assert.Equal(BankKind.Event, table.Kind);
        Assert.Empty(table.FsbTables);
        Assert.Equal(["FAKE", "LIST", "SNDH", "DEL "], table.RiffChunks.Select(c => c.FourCc).ToArray());
        Assert.Contains("事件 bank", table.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSampleTable_audio_bank_parses_fsb5_samples()
    {
        var path = Path.Combine(_workDir, "audio.bank");
        File.WriteAllBytes(path, BuildSyntheticBank(BuildMinimalFsb5(codec: 16)));

        var table = _service.ReadSampleTable(path);

        Assert.Equal(BankKind.Audio, table.Kind);
        var fsb = Assert.Single(table.FsbTables);
        Assert.Null(fsb.UnresolvedReason);
        Assert.NotNull(fsb.Fsb);
        Assert.Equal(16, fsb.Fsb!.Codec);
        Assert.Equal(0, fsb.Fsb.SampleCount);
        Assert.Equal(0x3C, fsb.Fsb.DataStartOffset);
        Assert.Equal(0x3C, fsb.Size);
        Assert.Empty(table.Samples);
    }

    [Fact]
    public void ReadSampleTable_encrypted_bank_throws_chinese_error()
    {
        var fsb = BuildMinimalFsb5();
        var encrypted = BuildSyntheticBank(fsb);
        encrypted[0x48] = (byte)'X';
        var path = Path.Combine(_workDir, "enc.bank");
        File.WriteAllBytes(path, encrypted);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.ReadSampleTable(path));
        Assert.Contains("加密", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSampleTable_unknown_bank_throws_instead_of_guessing()
    {
        var path = Path.Combine(_workDir, "bad.bank");
        File.WriteAllBytes(path, new byte[200]);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.ReadSampleTable(path));
        Assert.Contains("无法识别", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSampleTable_missing_file_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => _service.ReadSampleTable(Path.Combine(_workDir, "ghost.bank")));
    }

    // ---------- 合成 bank 构造 ----------

    /// <summary>最小合法 FSB5（版本 1、0 样本、可指定 codec）：
    /// 0x3C 头 = 魔数/版本/样本数/条目区/名称区/数据区/codec + flags + hash + 8B 尾。</summary>
    private static byte[] BuildMinimalFsb5(uint codec = 16)
    {
        var fsb = new byte[0x3C];
        Encoding.ASCII.GetBytes("FSB5").CopyTo(fsb, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fsb.AsSpan(0x04), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fsb.AsSpan(0x18), codec);
        return fsb;
    }

    /// <summary>合成 bank：RIFF(FEV ){ FAKE[8B] LIST(PROJ/BNKI[4B]) SNDH{...} [DEL ] [FSB] }。
    /// fsb 为 null → 事件 bank（空 SNDH + DEL 块）；否则音频 bank，FSB 附在 0x48。</summary>
    private static byte[] BuildSyntheticBank(byte[]? fsb)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(0u);                              // 0x00 RIFF 尺寸稍后回填
        bw.Write("FEV "u8);                                            // 0x08
        bw.Write("FAKE"u8); bw.Write(8u); bw.Write(1u); bw.Write(0u);  // 0x0C chunk0（0x14 处非 0，过 BankParser 门）
        bw.Write("LIST"u8); bw.Write(16u);                             // 0x1C
        bw.Write("PROJ"u8); bw.Write("BNKI"u8); bw.Write(4u); bw.Write(0x6B6E6942u); // 0x24 起
        var offsetFieldPos = 0L;
        if (fsb is null)
        {
            bw.Write("SNDH"u8); bw.Write(0u);                          // 0x34 空 SNDH → 事件 bank
            bw.Write("DEL "u8); bw.Write(0u);                          // 0x3C
        }
        else
        {
            bw.Write("SNDH"u8); bw.Write(12u);                         // 0x34
            bw.Write(1u);                                              // 0x3C 被解析器跳过的 u32
            offsetFieldPos = bw.Seek(0, SeekOrigin.Current);           // 0x40 FSB 偏移字段
            bw.Write(0u); bw.Write((uint)fsb.Length);                  // (offset, size)
            bw.Write(fsb);                                             // 0x48
        }
        var bytes = ms.ToArray();
        if (fsb is not null)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)offsetFieldPos), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x04), (uint)(bytes.Length - 8)); // RIFF 尺寸
        return bytes;
    }

    // ---------- 真实游戏目录门控测试 ----------

    [Fact]
    public void Real_bank_directory_scans_all_1531_banks_within_budget()
    {
        if (BankRoot is null) return;

        var watch = Stopwatch.StartNew();
        var entries = _service.ScanDirectory(BankRoot);
        watch.Stop();

        Assert.Equal(1531, entries.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"扫描耗时 {watch.Elapsed.TotalSeconds:F1}s，超出 60s 预算");
        Console.WriteLine($"真实 bank 扫描: {entries.Count} 个文件，耗时 {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Real_bank_type_distribution_is_sane()
    {
        if (BankRoot is null) return;

        var entries = _service.ScanDirectory(BankRoot);
        var unknowns = entries.Where(e => e.Kind == BankKind.Unknown).ToList();
        Assert.True(unknowns.Count == 0,
            $"有 {unknowns.Count} 个文件无法判定类型: " +
            string.Join(", ", unknowns.Take(5).Select(e => $"{e.FileName}({e.Detail})")));

        Assert.Contains(entries, e => e.Kind == BankKind.Audio);
        Assert.Contains(entries, e => e.Kind == BankKind.Event);
        Assert.All(entries, e => Assert.True(e.FsbCount >= 0 && e.FileSizeBytes > 0));
        Assert.All(entries.Where(e => e.Kind == BankKind.Event), e => Assert.Equal(0, e.FsbCount));
        Assert.All(entries.Where(e => e.Kind == BankKind.Audio), e => Assert.True(e.FsbCount > 0));

        var groups = entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine("真实 bank 类型分布: " +
            string.Join(", ", groups.OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Value}")));
    }

    [Fact]
    public void Real_audio_bank_sample_tables_are_readable_with_fadpcm()
    {
        if (BankRoot is null) return;

        var audioBanks = _service.ScanDirectory(BankRoot)
            .Where(e => e.Kind == BankKind.Audio)
            .Take(3)
            .ToList();
        Assert.NotEmpty(audioBanks);

        foreach (var bank in audioBanks)
        {
            var table = _service.ReadSampleTable(bank.FullPath);
            Assert.Equal(BankKind.Audio, table.Kind);
            Assert.NotEmpty(table.FsbTables);
            foreach (var fsbTable in table.FsbTables)
            {
                Assert.Null(fsbTable.UnresolvedReason);
                Assert.NotNull(fsbTable.Fsb);
                Assert.Equal(16, fsbTable.Fsb!.Codec); // FADPCM
                foreach (var sample in fsbTable.Fsb.Samples.Take(3))
                {
                    Assert.InRange(sample.SampleRate, 8000, 192000);
                    Assert.InRange(sample.Channels, 1, 8);
                }
            }
            var samples = table.Samples.ToList();
            Assert.True(samples.Count > 0, $"{bank.FileName} 没有任何样本");
            Console.WriteLine($"{bank.FileName}: {table.FsbTables.Count} 个 FSB / {samples.Count} 个样本");
        }
    }
}
