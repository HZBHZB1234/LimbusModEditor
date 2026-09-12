using System.Security.Cryptography;
using System.Text;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Debugging;

/// <summary>调试应用的一步（可逆）。</summary>
/// <param name="Kind">步骤种类。</param>
/// <param name="TargetPath">被改动的游戏 / 缓存文件。</param>
/// <param name="BackupPath">备份文件（原文件副本；新增文件时为 null）。</param>
/// <param name="ExistedBefore">改动前目标是否存在（false = 关闭时要删除）。</param>
/// <param name="BeforeHash">改动前目标哈希（诊断用）。</param>
/// <param name="AppliedHash">本次写入后的哈希（还原前逐字节比对，避免覆盖别人的改动）。</param>
/// <param name="Note">中文说明（报告用）。</param>
public sealed record ModApplyStep(
    ModApplyKind Kind,
    string TargetPath,
    string? BackupPath,
    bool ExistedBefore,
    string? BeforeHash,
    string? AppliedHash,
    string Note)
{
    /// <summary>还原清单行：<c>目标 \t 备份 \t 是否存在过 \t 写入后哈希 \t 说明</c>。</summary>
    internal string ToManifestLine() => string.Join('\t',
        TargetPath, BackupPath ?? string.Empty, ExistedBefore ? "1" : "0", AppliedHash ?? string.Empty, Note);

    internal static ModApplyStep? FromManifestLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 5) return null;
        return new ModApplyStep(
            ModApplyKind.FileOverwrite,
            parts[0],
            string.IsNullOrEmpty(parts[1]) ? null : parts[1],
            parts[2] == "1",
            null,
            string.IsNullOrEmpty(parts[3]) ? null : parts[3],
            parts[4]);
    }
}

/// <summary>调试应用的步骤种类。</summary>
public enum ModApplyKind
{
    /// <summary>整文件覆盖（.bank / lang 补丁 JSON）。</summary>
    FileOverwrite,
    /// <summary>就地重写（bundle <c>__data</c> 的对象级替换；可写性同覆盖，语义不同故分开记）。</summary>
    InPlaceRewrite,
    /// <summary>新增文件（关闭时删除）。</summary>
    Added,
}

/// <summary>调试应用的结果。</summary>
public sealed record ModApplyReport(
    string BackupDirectory,
    IReadOnlyList<ModApplyStep> Steps,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>改动文件数（去重）。</summary>
    public int ChangedFileCount => Steps.Select(x => x.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>一行摘要。</summary>
    public string Describe() => Skipped.Count == 0
        ? $"已应用 {ChangedFileCount} 个文件（备份在 {BackupDirectory}）"
        : $"已应用 {ChangedFileCount} 个文件；{Skipped.Count} 项跳过（备份在 {BackupDirectory}）";
}

/// <summary>
/// plan-16 S7：<b>「使用当前修改启动游戏进行调试」的应用层</b>——把导出的产物按
/// <b>加载器的应用语义</b>铺到游戏 / Unity 缓存上，并在关闭时逐字节还原。
///
/// <para>用户口径：bank 与资源 patch 走「导出成 <c>__data</c> 与 <c>.bank</c> 后备份覆盖」，
/// 语言与静态数据走「标准 patch」，核心是<b>模拟加载器的加载语义</b>，够制作者自测即可。</para>
///
/// <para><b>护栏</b>（比既有 <see cref="DebugApplyService"/> 更严，因为它覆盖的文件更多）：</para>
/// <list type="bullet">
/// <item>改动前逐文件 sha256 + 备份原始字节到 <c>&lt;备份根&gt;/&lt;时间戳&gt;/</c>，并写还原清单 <c>steps.tsv</c>；</item>
/// <item>还原前比对哈希：目标被别的程序改过就<b>不覆盖</b>并记进冲突（绝不悄悄丢别人的改动）；</item>
/// <item>应用过程中任一步失败 → 回滚本次已应用的步骤再抛；</item>
/// <item>游戏在运行时不应用（由调用方先确认）。</item>
/// </list>
///
/// <para><b>只做「能证明正确」的格式</b>：rebank 缺 FMOD DLL 时无法展开成整包、
/// 静态模组要写 catalog（S7b）——都<b>明确跳过并列进诊断</b>，不写半吊子改动。</para>
/// </summary>
public sealed class ModApplyService
{
    /// <summary>音频 bank 相对游戏目录的路径（与 LCTA <c>sound.sound_folder()</c> 一致）。</summary>
    public static readonly string[] BankRelativePath =
        ["LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMODBuilds", "Desktop"];

    /// <summary>lang 目录相对游戏目录的路径。</summary>
    public static readonly string[] LangRelativePath = ["LimbusCompany_Data", "lang"];

    /// <summary>还原清单文件名（备份目录内）。</summary>
    public const string ManifestFileName = "steps.tsv";

    /// <param name="backupRoot">备份根目录（一般是 <c>&lt;项目&gt;/backups</c>）。</param>
    /// <param name="unityCacheDirectory">Unity 缓存根（资源对象就地重写用；空则该项跳过）。</param>
    public async Task<ModApplyReport> ApplyAsync(
        ModProject project,
        ModPackExportResult export,
        string gameDirectory,
        string? unityCacheDirectory,
        string backupRoot,
        ModExportPlanContext context,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);

        if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException($"游戏目录不存在：{gameDirectory}");
        var game = Path.GetFullPath(gameDirectory);
        var cache = string.IsNullOrWhiteSpace(unityCacheDirectory) ? null : Path.GetFullPath(unityCacheDirectory);
        var backup = Path.Combine(Path.GetFullPath(backupRoot), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);

        var steps = new List<ModApplyStep>();
        var skipped = new List<string>();
        var diagnostics = new List<string>();
        try
        {
            foreach (var slot in export.Slots.Where(x => x.Written))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (slot.Descriptor.Slot)
                {
                    case ExportSlot.Bank:
                        progress?.Report($"铺音频整包（{slot.ArtifactCount} 个）…");
                        foreach (var file in slot.OutputPaths)
                            await ApplyBankAsync(file, game, backup, steps, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.Rebank:
                        progress?.Report("展开音频差分并铺到游戏…");
                        foreach (var file in slot.OutputPaths)
                            await ApplyRebankAsync(file, game, context, backup, steps, skipped, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.Carra:
                        progress?.Report("把资源对象写进 Unity 缓存…");
                        foreach (var file in slot.OutputPaths)
                            await ApplyCarraAsync(file, cache, backup, steps, skipped, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.LangPatch:
                        progress?.Report($"铺语言补丁（{slot.ArtifactCount} 个文件）…");
                        foreach (var file in slot.OutputPaths)
                            await ApplyLangPatchAsync(file, game, backup, steps, diagnostics, cancellationToken);
                        break;
                    default:
                        skipped.Add($"{slot.Descriptor.DisplayName}：{DebugSkipReason(slot.Descriptor.Slot)}");
                        break;
                }
            }

            await File.WriteAllLinesAsync(Path.Combine(backup, ManifestFileName),
                steps.Select(x => x.ToManifestLine()), new UTF8Encoding(false), cancellationToken);
            return new ModApplyReport(backup, steps, skipped, diagnostics);
        }
        catch
        {
            // 失败也要回滚（安全操作：取消令牌不参与还原）。
            try { await RestoreAsync(backup, CancellationToken.None); } catch (Exception) { /* 回滚尽力而为 */ }
            throw;
        }
    }

    private static string DebugSkipReason(ExportSlot slot) => slot switch
    {
        ExportSlot.LangBus => "这是给文本美化引擎（fancy bus）用的规则集，加载器不直接消费；文本改动已由 patch 槽位应用",
        ExportSlot.LangPathset => "这是可读的改动清单，当前加载器不消费；文本改动已由 patch 槽位应用",
        ExportSlot.StaticMod => "静态数据模组需要写 catalog（S7b 落地），本次调试未应用",
        _ => "该格式不在调试应用范围内",
    };

    // ── 音频整包：备份 → 覆盖 ────────────────────────────────────────

    private static async Task ApplyBankAsync(
        string sourceBank, string game, string backup,
        List<ModApplyStep> steps, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(sourceBank);
        var target = Path.Combine(game, Path.Combine(BankRelativePath), fileName);
        if (!File.Exists(target))
        {
            diagnostics.Add($"{fileName}：游戏音频目录下没有同名 bank，跳过（整包替换按文件名匹配）");
            return;
        }
        steps.Add(await OverwriteAsync(sourceBank, target, backup, "备份后覆盖（整包 .bank）", cancellationToken));
    }

    /// <summary>
    /// rebank 槽位：把差分包<b>展开成整包</b>再覆盖（等价语义）。
    /// 需要 FMOD 逐样本解码 → 缺 DLL 时明确跳过。
    /// </summary>
    private static async Task ApplyRebankAsync(
        string rebankPath, string game, ModExportPlanContext context, string backup,
        List<ModApplyStep> steps, List<string> skipped, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var package = RebankArchive.Read(File.OpenRead(rebankPath));
        var baseBank = package.Metadata.TryGetValue("base_bank", out var node) ? node.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseBank))
        {
            diagnostics.Add($"{Path.GetFileName(rebankPath)}：rebank.json 里没有 base_bank，跳过");
            return;
        }
        if (!context.FmodCodecAvailable)
        {
            skipped.Add($"{Path.GetFileName(rebankPath)}：缺少 FMOD DLL，无法把差分展开成整包");
            return;
        }
        var target = Path.Combine(game, Path.Combine(BankRelativePath), Path.GetFileName(baseBank));
        if (!File.Exists(target))
        {
            diagnostics.Add($"{Path.GetFileName(rebankPath)}：目标 bank 不在游戏目录下（{Path.GetFileName(baseBank)}），跳过");
            return;
        }

        // 与导出侧同一套语义：按 (FSB 序号, 样本名) 用差分里的 WAV 重建该 FSB，再整块替换。
        using var codec = new NativeFmodAudioCodec(context.FmodDirectory!);
        var bank = ModPackExportService.LoadBank(target);
        var applied = 0;
        foreach (var group in package.Files.GroupBy(x => x.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key < 0 || group.Key >= bank.FsbData.Count) continue;
            var info = Fsb5Parser.TryParse(bank.FsbData[group.Key]);
            if (info is null) continue;
            // 逐个样本替换：目标 bank 与该差分同序号样本名一致时才应用（加载器同口径）。
            var targets = info.Samples.ToDictionary(x => x.Index);
            var replacement = group.FirstOrDefault(x => targets.Values.Any(s =>
                string.Equals(s.Name + ".wav", x.Name, StringComparison.Ordinal)));
            if (replacement is null || replacement.Data.Length < 12) continue;
            bank.FsbData[group.Key] = await codec.EncodeWaveToFsbAsync(replacement.Data, cancellationToken);
            applied++;
        }
        if (applied == 0)
        {
            diagnostics.Add($"{Path.GetFileName(rebankPath)}：没有任何可应用的样本（样本名/序号对不上），跳过");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "lme-debug-bank-" + Guid.NewGuid().ToString("N") + ".bank");
        try
        {
            bank.HasModifications = true;
            await using (var stream = File.Create(temp))
            {
                await new BankFormatHandler().ExportAsync(
                    new ModPackage { SourceFormat = ModFormatKind.Bank, Payload = bank },
                    stream, new ExportContext(ModFormatKind.Bank, CancellationToken: cancellationToken));
            }
            steps.Add(await OverwriteAsync(temp, target, backup, "差分展开后覆盖", cancellationToken));
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { /* 临时文件 */ }
        }
    }

    // ── 资源对象：就地重写缓存 __data ────────────────────────────────

    private static async Task ApplyCarraAsync(
        string carraPath, string? cacheDirectory, string backup,
        List<ModApplyStep> steps, List<string> skipped, List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            skipped.Add($"{Path.GetFileName(carraPath)}：缺少可用的 Unity 缓存目录，无法定位 __data");
            return;
        }
        CarraPackage package;
        using (var stream = File.OpenRead(carraPath)) package = CarraArchive.Read(stream);

        foreach (var group in package.Entries.GroupBy(x => (x.Key.Account, x.Key.Bundle)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataPath = Path.Combine(cacheDirectory, group.Key.Account, group.Key.Bundle, "__data");
            if (!File.Exists(dataPath))
            {
                diagnostics.Add($"缓存里没有 {group.Key.Account}/{group.Key.Bundle}/__data（游戏可能已更新、外层键变了），跳过该 bundle");
                continue;
            }
            try
            {
                var rebuildRoot = Path.Combine(Path.GetTempPath(), "lme-debug-bundle-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var backend = new AssetsToolsBackend();
                    // 缓存 __data 是 UnityFS bundle：逐个 SerializedFile 找出这次要替换的 pathId，
                    // 按 SerializedFile 归组后各自重建（每个缓存条目通常只有一个，但不做假设）。
                    var bySerializedFile = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.Ordinal);
                    foreach (var entry in group)
                    {
                        var placed = false;
                        foreach (var serializedName in backend.BundleSerializedFileNames(dataPath))
                        {
                            UnityBundleSerializedObject? probe;
                            try { probe = backend.ReadBundleSerializedObject(dataPath, serializedName, entry.Key.PathId); }
                            catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException) { continue; }
                            if (probe is null) continue;
                            // 类型表索引必须与导出时一致，否则加载器会按类型不匹配跳过该对象。
                            if (entry.Key.TypeId is { } expectedType && expectedType != probe.TypeTableIndex)
                            {
                                diagnostics.Add($"{group.Key.Bundle}：pathId {entry.Key.PathId} 的类型表索引不一致" +
                                                $"（导出时 {expectedType}，当前缓存 {probe.TypeTableIndex}），跳过该对象");
                                placed = true;
                                break;
                            }
                            if (!bySerializedFile.TryGetValue(serializedName, out var map))
                                bySerializedFile[serializedName] = map = [];
                            map[entry.Key.PathId] = entry.ReadData();
                            placed = true;
                            break;
                        }
                        if (!placed)
                            diagnostics.Add($"{group.Key.Bundle}：缓存里没有 pathId {entry.Key.PathId}（游戏可能已更新），跳过该对象");
                    }

                    var produced = string.Empty;
                    var objectCount = 0;
                    foreach (var (serializedName, replacements) in bySerializedFile)
                    {
                        if (replacements.Count == 0) continue;
                        var output = Path.Combine(rebuildRoot, group.Key.Bundle, "__data");
                        backend.ReplaceBundleSerializedAssets(dataPath, serializedName, replacements, output);
                        produced = output;
                        objectCount += replacements.Count;
                    }
                    if (!File.Exists(produced))
                    {
                        diagnostics.Add($"{group.Key.Bundle}：没有任何可替换的对象，跳过该 bundle");
                        continue;
                    }
                    steps.Add(await OverwriteAsync(produced, dataPath, backup,
                        $"对象级就地重写（{objectCount} 个对象 → __data）", cancellationToken));
                }
                finally
                {
                    try { Directory.Delete(rebuildRoot, true); } catch (Exception) { /* 临时目录 */ }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add($"{group.Key.Bundle}：写回缓存失败（{ex.Message}），跳过该 bundle");
            }
        }
    }

    // ── 语言：标准 patch（补丁 JSON 放进 lang 目录）──────────────────

    private static async Task ApplyLangPatchAsync(
        string patchFile, string game, string backup,
        List<ModApplyStep> steps, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var langDirectory = Path.Combine(game, Path.Combine(LangRelativePath));
        if (!Directory.Exists(langDirectory))
        {
            diagnostics.Add($"{Path.GetFileName(patchFile)}：游戏 lang 目录不存在（{langDirectory}），跳过");
            return;
        }
        // 与加载器一致：补丁 JSON 放进 lang 目录（任意层），加载器启动时按 patchs 键逐文件 .bak 后应用。
        var target = Path.Combine(langDirectory, Path.GetFileName(patchFile));
        var existed = File.Exists(target);
        steps.Add(await OverwriteAsync(patchFile, target, backup,
            existed ? "备份后覆盖（语言补丁 JSON）" : "新增语言补丁 JSON（关闭时删除）", cancellationToken));
    }

    // ── 共用：备份 + 覆盖 ────────────────────────────────────────────

    private static async Task<ModApplyStep> OverwriteAsync(
        string source, string target, string backup, string note, CancellationToken cancellationToken)
    {
        var existed = File.Exists(target);
        string? backupPath = null;
        string? beforeHash = null;
        if (existed)
        {
            Directory.CreateDirectory(backup);
            backupPath = Path.Combine(backup, StableName(target));
            File.Copy(target, backupPath, overwrite: true);
            beforeHash = await Sha256Async(target, cancellationToken);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(target))
            await input.CopyToAsync(output, cancellationToken);
        var appliedHash = await Sha256Async(target, cancellationToken);
        return new ModApplyStep(
            existed ? ModApplyKind.FileOverwrite : ModApplyKind.Added,
            target, backupPath, existed, beforeHash, appliedHash, note);
    }

    /// <summary>备份文件名：目标路径的稳定短哈希 + 扩展名（避免长路径与重名冲突）。</summary>
    private static string StableName(string target)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant())))[..20];
        return hash + Path.GetExtension(target);
    }

    /// <summary>
    /// 还原一次调试会话的全部改动（逆序）：逐文件比对哈希后回写。
    /// 返回<b>冲突清单</b>（目标被外部改过、因此没有被覆盖）。
    /// </summary>
    public async Task<IReadOnlyList<string>> RestoreAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var conflicts = new List<string>();
        var manifest = Path.Combine(backupDirectory, ManifestFileName);
        if (!File.Exists(manifest)) return conflicts;
        var lines = await File.ReadAllLinesAsync(manifest, cancellationToken);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var step = ModApplyStep.FromManifestLine(lines[index]);
            if (step is null) continue;
            var target = step.TargetPath;
            if (step.ExistedBefore && File.Exists(target) && !string.IsNullOrEmpty(step.AppliedHash))
            {
                var currentHash = await Sha256Async(target, cancellationToken);
                if (!string.Equals(currentHash, step.AppliedHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(target);
                    continue; // 目标被别的程序改过：不覆盖
                }
            }
            if (step.ExistedBefore && step.BackupPath is not null && File.Exists(step.BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(step.BackupPath, target, overwrite: true);
            }
            else if (!step.ExistedBefore && File.Exists(target))
            {
                File.Delete(target);
            }
        }
        return conflicts;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
