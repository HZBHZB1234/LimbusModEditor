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
        var fsb = SyntheticBank.MinimalFsb5();
        var encrypted = SyntheticBank.Create(fsb);
        encrypted[0x48] = (byte)'X'; // 破坏首个 FSB 的 FSB5 魔数 → 加密
        File.WriteAllBytes(Path.Combine(_workDir, "01-event.bank"), SyntheticBank.Create(null));
        File.WriteAllBytes(Path.Combine(_workDir, "02-audio.bank"), SyntheticBank.Create(fsb));
        File.WriteAllBytes(Path.Combine(_workDir, "03-encrypted.bank"), encrypted);
        File.WriteAllBytes(Path.Combine(_workDir, "04-corrupt.bank"), [0xDE, 0xAD, 0xBE, 0xEF, .. new byte[96]]);
        File.WriteAllBytes(Path.Combine(_workDir, "05-truncated.bank"), SyntheticBank.Create(null)[..20]);
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
        File.WriteAllBytes(path, SyntheticBank.Create(null));

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
        File.WriteAllBytes(path, SyntheticBank.Create(SyntheticBank.MinimalFsb5(codec: 16)));

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
        var fsb = SyntheticBank.MinimalFsb5();
        var encrypted = SyntheticBank.Create(fsb);
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

    // 合成 bank 构造已抽到共享夹具 SyntheticBank（plan-11 的索引测试复用同一份布局）。

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
