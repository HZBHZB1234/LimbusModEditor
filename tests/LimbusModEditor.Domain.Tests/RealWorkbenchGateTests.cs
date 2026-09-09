using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Rebank;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-06 / plan-07 的真实数据审查门：
/// bank 目录 → 音频 bank 样本表 → FSB 切片 → FMOD 解码试听/波形；
/// .rebank 包结构与真实加载器（LCTA webutils/bank/rebank.py）解析语义一致；
/// lang 目录 → 修改真实表 → 导出补丁 → 回放到 vanilla 一致。
/// 缺少游戏目录 / 缓存 / DLL 的机器自动跳过对应断言。</summary>
public class RealWorkbenchGateTests
{
    private static string GameDirectory => Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
        @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";

    private static string? FmodDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(GameDirectory, "LimbusCompany_Data", "Plugins", "x86_64"),
            Path.Combine(AppContext.BaseDirectory, "fmod"),
        };
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            if (File.Exists(Path.Combine(candidate, "fmod64.dll")) ||
                File.Exists(Path.Combine(candidate, "fmodstudio.dll")) ||
                File.Exists(Path.Combine(candidate, "fmod.dll"))) return candidate;
        }
        return null;
    }

    // ── plan-06：bank 样本表 → FSB 切片 → 解码试听/波形 ─────────────────

    [Fact]
    public async Task Real_audio_bank_sample_can_be_sliced_decoded_and_waveformed()
    {
        var bankDirectory = new BankDirectoryService().ResolveBankDirectory(GameDirectory);
        if (bankDirectory is null) return; // 无游戏目录：跳过

        var banks = new BankDirectoryService().ScanDirectory(bankDirectory);
        var audioBank = banks.FirstOrDefault(x => x.Kind == BankKind.Audio);
        Assert.NotNull(audioBank);

        var table = new BankDirectoryService().ReadSampleTable(audioBank!.FullPath);
        var fsbTable = table.FsbTables.FirstOrDefault(x => x.Fsb is not null);
        Assert.NotNull(fsbTable);
        var sample = fsbTable!.Fsb!.Samples.FirstOrDefault();
        Assert.NotNull(sample);

        // 1) 按 SNDH 表切出该 FSB：必须是 FSB5（BankAudioService 同款路径）。
        var data = File.ReadAllBytes(audioBank.FullPath);
        var info = BankParser.TryParse(data);
        Assert.NotNull(info);
        var fsbBytes = data.AsSpan((int)info!.FsbOffsets[fsbTable.FsbIndex],
            (int)info.FsbSizes[fsbTable.FsbIndex]).ToArray();
        Assert.True(fsbBytes.Length >= 4 && fsbBytes.AsSpan(0, 4).SequenceEqual("FSB5"u8));
        Assert.Equal(sample!.SampleRate > 0, sample.SampleCount > 0);

        // 2) 解码（需要 FMOD DLL；本机没有则只验证结构部分）。
        var fmodDirectory = FmodDirectory();
        if (fmodDirectory is null) return;
        using var codec = new NativeFmodAudioCodec(fmodDirectory);
        var wav = await codec.DecodeFsbToWaveAsync(fsbBytes, 0);
        Assert.True(wav.Length > 44, "解码出的 WAV 太小");
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav.AsSpan(8, 4).ToArray());

        // 3) 波形包络（预览管线同款）：可绘制且有时长。
        var waveform = AudioWaveform.BuildEnvelope(wav, buckets: 128);
        Assert.Null(waveform.UnavailableReason);
        Assert.True(waveform.HasEnvelope);
        Assert.True(waveform.DurationSeconds > 0);
        Assert.True(waveform.Envelope.Any(x => x > 0f), "波形全为 0");

        // 4) 越界子样本索引必须明确报错（不猜）。
        var count = Fsb5Parser.TryParse(fsbBytes)?.SampleCount ?? 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.DecodeFsbToWaveAsync(fsbBytes, count + 10));
    }

    // ── plan-06：.rebank 包结构与加载器解析语义一致 ──────────────────────

    [Fact]
    public void Rebank_package_with_wav_entries_matches_loader_layout()
    {
        // 真实加载器（webutils/bank/rebank.py iter_rebank_wavs）：rebank.json + {整数索引}/{*.wav}。
        var wav = BuildMinimalWav();
        var package = RebankDiffService.Create(
            baseBank: "sound.bank", name: "LME 音频测试", version: "1.0", author: "tester", description: "gate",
            files: [(3, "sample_one.wav", wav, "modified"), (7, "sample_two.wav", wav, "added")]);

        using var output = new MemoryStream();
        RebankArchive.Write(package, output);
        output.Position = 0;

        using var zip = new ZipArchive(output, ZipArchiveMode.Read, true);
        var config = zip.GetEntry("rebank.json");
        Assert.NotNull(config);
        using (var reader = new StreamReader(config!.Open()))
        {
            var json = JsonNode.Parse(reader.ReadToEnd())!;
            Assert.Equal("sound.bank", json["base_bank"]!.GetValue<string>());
            // 加载器只接受纯文件名（防路径穿越）。
            var baseBank = json["base_bank"]!.GetValue<string>();
            Assert.Equal(Path.GetFileName(baseBank), baseBank);
            Assert.DoesNotContain('/', baseBank);
            Assert.DoesNotContain('\\', baseBank);
        }

        var entries = zip.Entries.Where(x => !string.IsNullOrEmpty(x.Name) && x.FullName != "rebank.json").ToList();
        Assert.Equal(2, entries.Count);
        foreach (var entry in entries)
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            Assert.True(int.TryParse(parts[0], out var index) && index >= 0, $"条目索引非法: {entry.FullName}");
            Assert.EndsWith(".wav", parts[^1], StringComparison.OrdinalIgnoreCase);
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            Assert.True(bytes.Length > 44);
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        }

        // 回读往返一致（编辑器自己的读取路径）。
        output.Position = 0;
        var roundTripped = RebankArchive.Read(output);
        Assert.Equal(2, roundTripped.Files.Count);
        Assert.Equal("sound.bank", roundTripped.Metadata["base_bank"].GetString());
    }

    // ── plan-07：真实 lang 表修改 → 补丁 → 回放一致 ──────────────────────

    [Fact]
    public void Real_lang_table_edit_round_trips_through_patch_replay()
    {
        var langRoot = new LangTextWorkbenchService().ResolveLangRoot(GameDirectory);
        if (langRoot is null) return; // 无游戏目录：跳过

        var service = new LangTextWorkbenchService();
        var files = service.EnumerateFiles(langRoot);
        Assert.NotEmpty(files);
        Assert.Equal("LLC_zh-CN", service.ReadActiveLanguage(langRoot));

        // 挑一个根级 AbDlg_*.json（存在则用，不存在退回第一个可解析文件）。
        var target = files.FirstOrDefault(x => x.RelativePath.EndsWith("AbDlg_Faust.json", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(x => x.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && x.KeyCount > 0);
        Assert.NotNull(target);

        var original = service.BeginEdit(target!.RelativePath);
        var document = JsonNode.Parse(original);
        Assert.NotNull(document);
        var firstKey = (document as JsonObject)?.FirstOrDefault();
        Assert.NotNull(firstKey);

        // 修改第一个值（字符串键）或追加一个测试键。
        var modifiedNode = JsonNode.Parse(original)!;
        var marker = "LME-回放验证-" + Guid.NewGuid().ToString("N")[..8];
        if (modifiedNode is JsonObject obj && firstKey!.Value.Value is JsonValue value && value.TryGetValue<string>(out _))
            obj[firstKey.Value.Key] = marker;
        else if (modifiedNode is JsonObject appendTarget)
            appendTarget["LME_gate_marker"] = marker;
        var modifiedText = modifiedNode.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        service.SetModified(target.RelativePath, modifiedText);

        var patchPath = Path.Combine(Path.GetTempPath(), $"lme-lang-gate-{Guid.NewGuid():N}.json");
        try
        {
            var report = service.ExportPatch(patchPath);
            Assert.Equal(1, report.EditedFileCount);
            Assert.Equal(1, report.PatchedFileCount);

            // 补丁文档键必须是相对 lang 根的 '/' 路径。
            var documentPatch = new LangTextPatchService().Read(patchPath);
            var patchEntry = Assert.Single(documentPatch.Patches);
            Assert.Equal(target.RelativePath, patchEntry.Key);
            Assert.DoesNotContain('\\', patchEntry.Key);

            // 用 TextDiffService.Apply 把 ops 回放到 vanilla 快照 → 必须等于修改后的文档。
            var replayed = new TextDiffService().Apply(JsonNode.Parse(original)!, patchEntry.Value);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(modifiedText), replayed),
                "补丁回放结果与修改后的 JSON 不一致");

            // 还原：lang 目录从未被改动。
            Assert.True(service.Revert(target.RelativePath));
            Assert.Empty(service.EditedFiles);
        }
        finally
        {
            try { File.Delete(patchPath); } catch (Exception) { /* 临时文件 */ }
        }
    }

    /// <summary>最小 16-bit 单声道 WAV（结构校验用）。</summary>
    private static byte[] BuildMinimalWav()
    {
        var pcm = new byte[160];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }
}
