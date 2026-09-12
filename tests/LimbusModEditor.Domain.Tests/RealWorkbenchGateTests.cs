using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
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
        // 活动语言跟随 config.json（玩家可能启用汉化组目录，不能写死 LLC_zh-CN）。
        var expectedLanguage = System.Text.Json.Nodes.JsonNode
            .Parse(File.ReadAllText(Path.Combine(langRoot, "config.json")))!["lang"]!.GetValue<string>();
        Assert.Equal(expectedLanguage, service.ReadActiveLanguage(langRoot));

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

            // 补丁文档键必须是「相对 lang 根」的 '/' 路径 = 加载器口径（plan-16 §5：
            // 条目是语言目录内部口径，导出时由 ToPatchKey 补回语言目录那一层）。
            var documentPatch = new LangTextPatchService().Read(patchPath);
            var patchEntry = Assert.Single(documentPatch.Patches);
            Assert.Equal(service.ToPatchKey(target.RelativePath), patchEntry.Key);
            Assert.StartsWith(service.LanguageDirectoryPrefix, patchEntry.Key, StringComparison.Ordinal);
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

    // ── plan-16 S4：真实 bank 上的 rebank 槽位必须按「FSB 序号 + 真实样本名」写出 ──

    /// <summary>
    /// 真实数据门（无游戏目录 / 无 FMOD DLL 自动跳过）：挑一个音频 bank 的单个样本做
    /// <b>FSB 级替换</b>（把同一个 FSB 再写回去，内容不变），跑完整的「计划 → 导出」，
    /// 断言写出的 <c>.rebank</c>：
    /// ① 文件名 = 目标 bank 基名（加载器按 <c>base_bank</c> 定位）；
    /// ② 每个条目是 <c>&lt;FSB序号&gt;/&lt;真实样本名&gt;.wav</c> —— 这正是 plan-16 §2.3.1
    /// 指出的旧实现缺陷（旧实现写 <c>{i}.fsb</c>，加载器一条都匹配不上）；
    /// ③ 条目 WAV 能被 RIFF 头部校验（加载器 <c>read_wav_info</c> 会拒收非 WAV）。
    /// </summary>
    [Fact]
    public async Task Real_bank_exports_a_rebank_with_real_sample_names()
    {
        var bankDirectory = new BankDirectoryService().ResolveBankDirectory(GameDirectory);
        if (bankDirectory is null) return;
        var fmodDirectory = FmodDirectory();
        if (fmodDirectory is null) return; // 差分必须有 FMOD 解码逐样本 WAV

        var audioBank = new BankDirectoryService().ScanDirectory(bankDirectory)
            .Where(x => x.Kind == BankKind.Audio)
            .OrderBy(x => x.FileSizeBytes)
            .FirstOrDefault();
        if (audioBank is null) return;

        var work = Path.Combine(Path.GetTempPath(), "lme-rebank-gate-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(work, "out");
        Directory.CreateDirectory(work);
        try
        {
            // 项目里登记一条「样本 0 被替换」的音频修改：替换内容 = 该 FSB 本身（内容相同也算改动，
            // 这里验证的是**包结构与命名**，不是音频差异）。
            var bankCopy = Path.Combine(work, "sources", "banks", audioBank.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(bankCopy)!);
            File.Copy(audioBank.FullPath, bankCopy, overwrite: true);
            var data = File.ReadAllBytes(bankCopy);
            var info = BankParser.TryParse(data)!;
            var fsbIndex = 0;
            var fsb = data.AsSpan((int)info.FsbOffsets[fsbIndex], (int)info.FsbSizes[fsbIndex]).ToArray();
            var parsed = Fsb5Parser.TryParse(fsb);
            if (parsed is null || parsed.Samples.Count == 0) return;              // 无法解析：跳过
            if (parsed.Samples.Any(x => string.IsNullOrWhiteSpace(x.Name))) return; // 没有真实样本名：跳过（本测试要验的就是它）
            var replacement = Path.Combine(work, "replacement.fsb5");
            File.WriteAllBytes(replacement, fsb);

            var project = new ModProject { Name = "RebankGate" };
            project.Assets.Add(new AssetRecord
            {
                LogicalPath = $"fsb/{fsbIndex}",
                SourcePath = bankCopy,
                Type = AssetType.Audio,
                Bundle = audioBank.FileName,
                Metadata = { ["bankSource"] = audioBank.FileName, ["replacementPath"] = replacement },
            });

            var context = new ModExportPlanContext(FmodDirectory: fmodDirectory);
            var plan = new ModExportPlanService().Plan(project, output, new LangEditSession(), new StaticEditSession(), context);
            var result = await new ModPackExportService().ExportAsync(project, work, plan, context);

            var slot = result.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
            if (!slot.Written)
            {
                // 真实数据上不满足条件时必须给出中文原因，不许静默。
                Assert.NotEmpty(slot.Diagnostics);
                return;
            }

            var file = Assert.Single(slot.OutputPaths);
            Assert.Equal(Path.GetFileNameWithoutExtension(audioBank.FileName) + ".rebank", Path.GetFileName(file));
            using var zip = ZipFile.OpenRead(file);
            var config = zip.GetEntry("rebank.json");
            Assert.NotNull(config);
            using (var reader = new StreamReader(config!.Open()))
            {
                var json = JsonNode.Parse(reader.ReadToEnd())!;
                var baseBank = json["base_bank"]!.GetValue<string>();
                Assert.Equal(Path.GetFileName(baseBank), baseBank); // 加载器只接受纯文件名（防穿越）
                Assert.Contains(Path.GetFileNameWithoutExtension(audioBank.FileName), baseBank, StringComparison.OrdinalIgnoreCase);
            }

            var entries = zip.Entries.Where(x => x.FullName != "rebank.json").ToList();
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                var parts = entry.FullName.Replace('\\', '/').Split('/');
                Assert.Equal(2, parts.Length);
                Assert.True(int.TryParse(parts[0], out var index) && index == fsbIndex, $"条目必须挂在被改的 FSB 序号下: {entry.FullName}");
                Assert.EndsWith(".wav", parts[1], StringComparison.OrdinalIgnoreCase);
                // 条目名必须是真实样本名（与 FSB5 里解析出的一致）
                var sampleName = parts[1][..^4];
                Assert.Contains(parsed.Samples, x => string.Equals(x.Name, sampleName, StringComparison.Ordinal));
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                Assert.True(bytes.Length > 44, $"条目 WAV 太小: {entry.FullName}");
                Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
                Assert.Equal("WAVE"u8.ToArray(), bytes.AsSpan(8, 4).ToArray());
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception) { /* 临时目录 */ }
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
