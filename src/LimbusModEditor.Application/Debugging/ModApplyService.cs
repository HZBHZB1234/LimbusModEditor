using System.Security.Cryptography;
using System.Text;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Unity;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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

        using var scope = Log.Scope("铺盘模组到游戏目录（调试应用）");
        var plannedEntries = export.Slots.Count(x => x.Written);
        Log.Info("调试应用开始：模组 {0}，游戏目录 {1}，Unity 缓存 {2}，备份根 {3}，计划条目 {4} 个槽位",
            project.Name ?? "-", gameDirectory, unityCacheDirectory ?? "-", backupRoot, plannedEntries);

        if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException($"游戏目录不存在：{gameDirectory}");
        var game = Path.GetFullPath(gameDirectory);
        var cache = string.IsNullOrWhiteSpace(unityCacheDirectory) ? null : Path.GetFullPath(unityCacheDirectory);
        if (cache is null)
            Log.Warn("调试应用缺少 Unity 缓存目录：资源对象就地重写（carra）与静态模组（static）将逐项跳过（游戏目录 {0}）", game);
        var backup = Path.Combine(Path.GetFullPath(backupRoot), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        Log.Debug("调试应用备份目录已创建：{0}", backup);

        Log.Debug("调试应用环境：备份目录 {0}，Unity 缓存 {1}，FMOD 目录 {2}，FMOD 可用 {3}",
            backup, cache ?? "-", context.FmodDirectory ?? "-", context.FmodCodecAvailable);
        var steps = new List<ModApplyStep>();
        var skipped = new List<string>();
        var diagnostics = new List<string>();
        var progressReports = 0;
        try
        {
            foreach (var slot in export.Slots.Where(x => x.Written))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Debug("调试应用槽位开始：{0}（{1}），产物 {2} 个，目录 {3}",
                    slot.Descriptor.Slot, slot.Descriptor.DisplayName, slot.ArtifactCount, slot.Directory);
                switch (slot.Descriptor.Slot)
                {
                    case ExportSlot.Bank:
                        progress?.Report($"铺音频整包（{slot.ArtifactCount} 个）…");
                        progressReports++;
                        foreach (var file in slot.OutputPaths)
                            await ApplyBankAsync(file, game, backup, steps, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.Rebank:
                        progress?.Report("展开音频差分并铺到游戏…");
                        progressReports++;
                        foreach (var file in slot.OutputPaths)
                            await ApplyRebankAsync(file, game, context, backup, steps, skipped, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.Carra:
                        progress?.Report("把资源对象写进 Unity 缓存…");
                        progressReports++;
                        foreach (var file in slot.OutputPaths)
                            await ApplyCarraAsync(file, cache, backup, steps, skipped, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.LangPatch:
                        progress?.Report($"铺语言补丁（{slot.ArtifactCount} 个文件）…");
                        progressReports++;
                        foreach (var file in slot.OutputPaths)
                            await ApplyLangPatchAsync(file, game, backup, steps, diagnostics, cancellationToken);
                        break;
                    case ExportSlot.StaticMod:
                        progress?.Report("应用静态数据模组（重打包 bundle + 双写 catalog）…");
                        progressReports++;
                        foreach (var file in slot.OutputPaths)
                            await ApplyStaticModAsync(file, game, cache, backup, steps, skipped, diagnostics, cancellationToken);
                        break;
                    default:
                        skipped.Add($"{slot.Descriptor.DisplayName}：{DebugSkipReason(slot.Descriptor.Slot)}");
                        Log.Warn("调试应用跳过槽位：{0}（{1}）：{2}",
                            slot.Descriptor.Slot, slot.Descriptor.DisplayName, DebugSkipReason(slot.Descriptor.Slot));
                        break;
                }
                if (progressReports > 0 && progressReports % 3 == 0)
                {
                    Log.Debug("调试应用进度：已报告 {0} 次、已登记 {1} 个步骤、跳过 {2} 项、诊断 {3} 条",
                        progressReports, steps.Count, skipped.Count, diagnostics.Count);
                    progressReports = 0;
                }
            }

            foreach (var reason in skipped)
                Log.Warn("调试应用跳过项：{0}", reason);
            foreach (var note in diagnostics)
                Log.Debug("调试应用诊断：{0}", note);

            await File.WriteAllLinesAsync(Path.Combine(backup, ManifestFileName),
                steps.Select(x => x.ToManifestLine()), new UTF8Encoding(false), cancellationToken);
            Log.Info("调试应用完成：改动 {0} 个文件、步骤 {1} 步、跳过 {2} 项、诊断 {3} 条，还原清单 {4}",
                steps.Select(x => x.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                steps.Count, skipped.Count, diagnostics.Count, Path.Combine(backup, ManifestFileName));
            return new ModApplyReport(backup, steps, skipped, diagnostics);
        }
        catch (OperationCanceledException)
        {
            Log.Info("调试应用已取消：备份目录 {0}，已登记 {1} 个步骤，准备回滚", backup, steps.Count);
            // 失败也要回滚（安全操作：取消令牌不参与还原）。
            try { await RestoreAsync(backup, CancellationToken.None); } catch (Exception ex) { Log.Error(ex, "调试应用取消后的回滚失败：备份目录 {0}", backup); /* 回滚尽力而为 */ }
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "调试应用失败：游戏目录 {0}，备份目录 {1}，已登记 {2} 个步骤，准备回滚", game, backup, steps.Count);
            // 失败也要回滚（安全操作：取消令牌不参与还原）。
            try { await RestoreAsync(backup, CancellationToken.None); } catch (Exception rollbackEx) { Log.Error(rollbackEx, "调试应用失败后的回滚失败：备份目录 {0}", backup); /* 回滚尽力而为 */ }
            throw;
        }
    }

    private static string DebugSkipReason(ExportSlot slot) => slot switch
    {
        ExportSlot.LangBus => "这是给文本美化引擎（fancy bus）用的规则集，加载器不直接消费；文本改动已由 patch 槽位应用",
        ExportSlot.LangPathset => "这是可读的改动清单，当前加载器不消费；文本改动已由 patch 槽位应用",
        _ => "该格式不在调试应用范围内",
    };

    // ── 静态数据：catalog 双写（S7b）────────────────────────────────

    /// <summary>
    /// 应用 <c>.staticmod</c>：定位当前 catalog 里的 static 条目 → 从缓存取官方 bundle →
    /// 改 TextAsset 并重打包 → 算解压块 CRC32 → 双写缓存 <c>__data</c>/<c>__info</c> 与 catalog。
    /// 关闭时由还原清单把 catalog 与缓存条目一起还原（两者都在备份里）。
    /// </summary>
    private static async Task ApplyStaticModAsync(
        string staticModPath, string game, string? cacheDirectory, string backup,
        List<ModApplyStep> steps, List<string> skipped, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(staticModPath);
        using var scope = Log.Scope("应用静态数据模组");
        Log.Debug("静态模组应用开始：{0}，游戏目录 {1}，Unity 缓存 {2}，备份目录 {3}",
            staticModPath, game, cacheDirectory ?? "-", backup);
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            Log.Warn("静态模组 {0} 跳过：缺少可用的 Unity 缓存根（{1}），无法定位静态 bundle 与 catalog", name, cacheDirectory ?? "-");
            skipped.Add($"{name}：缺少可用的 Unity 缓存根，无法定位静态 bundle 与 catalog");
            return;
        }

        // catalog 候选：运行时 catalog（缓存根推导）优先，其次游戏安装目录里那份。
        var candidates = StaticBundleLocator.FindCatalogCandidates(game, [cacheDirectory]);
        Log.Debug("静态模组 {0}：找到 {1} 个 catalog 候选", name, candidates.Count);
        if (candidates.Count == 0)
        {
            Log.Warn("静态模组 {0} 跳过：在游戏目录 {1} 与缓存根 {2} 下都找不到 catalog（com.unity.addressables/catalog_S1.bin）",
                name, game, cacheDirectory);
            skipped.Add($"{name}：找不到 catalog（com.unity.addressables/catalog_S1.bin），静态模组未应用");
            return;
        }

        var service = new StaticModApplyService();
        var candidateIndex = 0;
        foreach (var catalogPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateIndex++;
            Log.Debug("静态模组 {0}：尝试第 {1}/{2} 个 catalog {3}", name, candidateIndex, candidates.Count, catalogPath);
            StaticModApplyService.StaticCatalogSlot slot;
            try
            {
                slot = service.Locate(catalogPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(ex, "静态模组 {0}：catalog {1} 定位失败（{2}），尝试下一个候选", name, catalogPath, ex.Message);
                diagnostics.Add($"{name}：catalog {Path.GetFileName(catalogPath)} 定位失败（{ex.Message}）");
                continue;
            }

            // 缓存命中：外层键优先用 catalog 记录，滞后时按「任意外层键下存在该内层键」兜底。
            var location = StaticBundleLocator.LocateInCache(
                new StaticBundleLocation(slot.BundleName, slot.InnerHash, slot.OuterKey, null, CatalogPath: catalogPath),
                [cacheDirectory]);
            if (location.DataPath is null || !File.Exists(location.DataPath))
            {
                Log.Warn("静态模组 {0}：catalog {1} 的官方 static bundle（{2}）不在缓存里（DataPath {3}），尝试下一个候选",
                    name, catalogPath, slot.BundleName, location.DataPath ?? "-");
                diagnostics.Add($"{name}：catalog {Path.GetFileName(catalogPath)} 对应的官方 static bundle 不在缓存里" +
                                "（启动一次游戏让它下载缓存后重试），跳过");
                continue;
            }

            try
            {
                var cacheEntryDirectory = Path.GetDirectoryName(location.DataPath)!;
                var infoPath = Path.Combine(cacheEntryDirectory, "__info");
                var infoContent = File.Exists(infoPath) ? await File.ReadAllTextAsync(infoPath, cancellationToken) : string.Empty;
                Log.Debug("静态模组 {0}：缓存条目 {1}（__info 存在 {2}），catalog {3}",
                    name, cacheEntryDirectory, File.Exists(infoPath), catalogPath);

                // ① 改写前先备份：catalog 与缓存条目 __data/__info（三者任一没还原都是脏状态）。
                var catalogBackup = await BackupOriginalAsync(catalogPath, backup, "catalog 原字节（还原时写回，crc/size 随之复位）", cancellationToken);
                var dataBackup = await BackupOriginalAsync(location.DataPath, backup, "静态 bundle 缓存 __data 原字节", cancellationToken);
                var infoBackup = await BackupOriginalAsync(infoPath, backup, "静态 bundle 缓存 __info 原字节", cancellationToken);

                // ② 应用：改 TextAsset → 重打包 → 算 CRC → 双写缓存条目与 catalog 字段。
                var (crc, size, applied) = await service.ApplyAsync(
                    staticModPath, slot, location.DataPath, cacheEntryDirectory, infoContent, cancellationToken);

                // ③ 写入成功后才登记（还原清单里的记录必须代表「真的改过」，否则会把没改的文件也还原一遍）。
                steps.Add(catalogBackup with { AppliedHash = await Sha256Async(catalogPath, cancellationToken), Note = "catalog 双写（crc/size）" });
                steps.Add(dataBackup with { AppliedHash = await Sha256Async(location.DataPath, cancellationToken), Note = "静态 bundle 重打包写回" });
                if (infoBackup.ExistedBefore)
                    steps.Add(infoBackup with { AppliedHash = await Sha256Async(infoPath, cancellationToken), Note = "静态 bundle 缓存 __info 刷新" });
                Log.Debug("静态模组 {0} 已写入：catalog {1}、缓存 __data {2}、__info {3}",
                    name, catalogPath, location.DataPath, infoBackup.ExistedBefore ? infoPath : "-");
                diagnostics.Add($"{name}：已应用 {applied.Count} 个静态表（{slot.BundleName}，crc 0x{crc:X8} / size {size}）");
                Log.Info("静态模组应用完成：{0}，bundle {1}，共 {2} 个静态表，crc 0x{3:X8}，size {4} 字节",
                    name, slot.BundleName, applied.Count, crc, size);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "静态模组 {0} 应用失败：catalog {1}，静态 bundle {2}；改动将整体回滚",
                    name, catalogPath, location.DataPath);
                diagnostics.Add($"{name}：应用失败（{ex.Message}）；改动会被整体回滚，静态模组本次未生效");
                return;
            }
        }
        Log.Warn("静态模组 {0} 跳过：{1} 个候选 catalog 都无法应用（{2}）", name, candidates.Count, string.Join("; ", diagnostics));
        skipped.Add($"{name}：所有候选 catalog 都无法应用（详见诊断）");
    }

    /// <summary>
    /// 改写前备份一个文件的原始字节（catalog / 缓存条目这类「就地改写」的对象）。
    /// 返回的记录 <c>AppliedHash</c> 为空，调用方在<b>确认写成功</b>后再补上写入后的哈希
    /// ——只有真改过的文件才该进还原清单。
    /// </summary>
    private static async Task<ModApplyStep> BackupOriginalAsync(
        string target, string backup, string note, CancellationToken cancellationToken)
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
            if (Log.IsDebugEnabled)
                Log.Debug("已备份原文件（就地改写前）：{0} → {1}，{2} 字节，sha256 {3}（{4}）",
                    target, backupPath, FileLengthOrMinusOne(backupPath), beforeHash, note);
        }
        else
        {
            Log.Debug("就地改写的目标当前不存在（将按新增处理）：{0}（{1}）", target, note);
        }
        return new ModApplyStep(ModApplyKind.InPlaceRewrite, target, backupPath, existed, beforeHash,
            AppliedHash: null, note);
    }

    // ── 音频整包：备份 → 覆盖 ────────────────────────────────────────

    private static async Task ApplyBankAsync(
        string sourceBank, string game, string backup,
        List<ModApplyStep> steps, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(sourceBank);
        var target = Path.Combine(game, Path.Combine(BankRelativePath), fileName);
        if (!File.Exists(target))
        {
            Log.Warn("音频整包 {0} 跳过：游戏音频目录下没有同名 bank（期望 {1}，按文件名匹配）", fileName, target);
            diagnostics.Add($"{fileName}：游戏音频目录下没有同名 bank，跳过（整包替换按文件名匹配）");
            return;
        }
        Log.Debug("音频整包覆盖开始：{0} → {1}", sourceBank, target);
        var step = await OverwriteAsync(sourceBank, target, backup, "备份后覆盖（整包 .bank）", cancellationToken);
        Log.Debug("音频整包覆盖完成：{0} → {1}，{2} 字节，备份 {3}",
            sourceBank, target, FileLengthOrMinusOne(target), step.BackupPath ?? "（原本不存在）");
        steps.Add(step);
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
        Log.Debug("音频差分 {0}：base_bank {1}，差分条目 {2} 个", Path.GetFileName(rebankPath), baseBank ?? "-", package.Files.Count);
        if (string.IsNullOrWhiteSpace(baseBank))
        {
            Log.Warn("音频差分 {0} 跳过：rebank.json 里没有 base_bank（{1} 个条目）", Path.GetFileName(rebankPath), package.Files.Count);
            diagnostics.Add($"{Path.GetFileName(rebankPath)}：rebank.json 里没有 base_bank，跳过");
            return;
        }
        if (!context.FmodCodecAvailable)
        {
            Log.Warn("音频差分 {0} 跳过：缺少 FMOD DLL（FmodDirectory {1}），无法把差分展开成整包",
                Path.GetFileName(rebankPath), context.FmodDirectory ?? "-");
            skipped.Add($"{Path.GetFileName(rebankPath)}：缺少 FMOD DLL，无法把差分展开成整包");
            return;
        }
        var target = Path.Combine(game, Path.Combine(BankRelativePath), Path.GetFileName(baseBank));
        if (!File.Exists(target))
        {
            Log.Warn("音频差分 {0} 跳过：目标 bank 不在游戏目录下（{1}）", Path.GetFileName(rebankPath), target);
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
            if (group.Key < 0 || group.Key >= bank.FsbData.Count)
            {
                Log.Warn("音频差分 {0}：FSB 序号 {1} 超出目标 bank 范围（共 {2} 个 FSB），跳过该组 {3} 个样本",
                    Path.GetFileName(rebankPath), group.Key, bank.FsbData.Count, group.Count());
                continue;
            }
            var info = Fsb5Parser.TryParse(bank.FsbData[group.Key]);
            if (info is null)
            {
                Log.Warn("音频差分 {0}：目标 bank 的 FSB #{1} 解析失败（TryParse 返回 null），跳过该组 {2} 个样本",
                    Path.GetFileName(rebankPath), group.Key, group.Count());
                continue;
            }
            // 逐个样本替换：目标 bank 与该差分同序号样本名一致时才应用（加载器同口径）。
            var targets = info.Samples.ToDictionary(x => x.Index);
            var replacement = group.FirstOrDefault(x => targets.Values.Any(s =>
                string.Equals(s.Name + ".wav", x.Name, StringComparison.Ordinal)));
            if (replacement is null || replacement.Data.Length < 12)
            {
                Log.Debug("音频差分 {0}：FSB #{1} 没有匹配到样本（候选 {2} 个，命中 {3}，数据长度 {4}），跳过该组",
                    Path.GetFileName(rebankPath), group.Key, group.Count(), replacement is not null,
                    replacement?.Data.Length ?? 0);
                continue;
            }
            bank.FsbData[group.Key] = await codec.EncodeWaveToFsbAsync(replacement.Data, cancellationToken);
            applied++;
            Log.Debug("音频差分 {0}：FSB #{1} 已用 {2}（{3} 字节）重编码替换，累计 {4} 个样本",
                Path.GetFileName(rebankPath), group.Key, replacement.Name, replacement.Data.Length, applied);
        }
        if (applied == 0)
        {
            Log.Warn("音频差分 {0} 跳过：没有任何可应用的样本（样本名/序号对不上），目标 bank {1}",
                Path.GetFileName(rebankPath), target);
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
            Log.Debug("音频差分 {0}：已重建整包临时文件 {1}（{2} 字节，{3} 个样本已替换）",
                Path.GetFileName(rebankPath), temp, FileLengthOrMinusOne(temp), applied);
            steps.Add(await OverwriteAsync(temp, target, backup, "差分展开后覆盖", cancellationToken));
            Log.Debug("音频差分 {0}：整包已铺到 {1}（{2} 字节）",
                Path.GetFileName(rebankPath), target, FileLengthOrMinusOne(target));
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) { Log.Debug(ex, "删除音频重建临时文件失败（保留不影响结果）：{0}", temp); /* 临时文件 */ }
        }
    }

    // ── 资源对象：就地重写缓存 __data ────────────────────────────────

    private static async Task ApplyCarraAsync(
        string carraPath, string? cacheDirectory, string backup,
        List<ModApplyStep> steps, List<string> skipped, List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            Log.Warn("资源对象 {0} 跳过：缺少可用的 Unity 缓存目录（{1}），无法定位 __data",
                Path.GetFileName(carraPath), cacheDirectory ?? "-");
            skipped.Add($"{Path.GetFileName(carraPath)}：缺少可用的 Unity 缓存目录，无法定位 __data");
            return;
        }
        CarraPackage package;
        using (var stream = File.OpenRead(carraPath)) package = CarraArchive.Read(stream);
        Log.Debug("资源对象应用开始：{0}，条目 {1} 个，缓存目录 {2}，备份目录 {3}",
            carraPath, package.Entries.Count, cacheDirectory, backup);

        foreach (var group in package.Entries.GroupBy(x => (x.Key.Account, x.Key.Bundle)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataPath = Path.Combine(cacheDirectory, group.Key.Account, group.Key.Bundle, "__data");
            if (!File.Exists(dataPath))
            {
                Log.Warn("资源对象跳过 bundle {0}/{1}：缓存里没有 __data（{2}，游戏可能已更新、外层键变了），该组 {3} 个对象未应用",
                    group.Key.Account, group.Key.Bundle, dataPath, group.Count());
                diagnostics.Add($"缓存里没有 {group.Key.Account}/{group.Key.Bundle}/__data（游戏可能已更新、外层键变了），跳过该 bundle");
                continue;
            }
            Log.Debug("资源对象 bundle {0}/{1}：{2} 个待替换对象，缓存条目 {3}",
                group.Key.Account, group.Key.Bundle, group.Count(), dataPath);
            try
            {
                var rebuildRoot = Path.Combine(Path.GetTempPath(), "lme-debug-bundle-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var backend = new AssetsToolsBackend();
                    // 缓存 __data 是 UnityFS bundle：逐个 SerializedFile 找出这次要替换的 pathId，
                    // 按 SerializedFile 归组后各自重建（每个缓存条目通常只有一个，但不做假设）。
                    var bySerializedFile = new Dictionary<string, Dictionary<long, byte[]>>(StringComparer.Ordinal);
                    var probeFailures = 0;
                    foreach (var entry in group)
                    {
                        var placed = false;
                        foreach (var serializedName in backend.BundleSerializedFileNames(dataPath))
                        {
                            UnityBundleSerializedObject? probe;
                            try { probe = backend.ReadBundleSerializedObject(dataPath, serializedName, entry.Key.PathId); }
                            catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException)
                            {
                                probeFailures++;
                                Log.Debug(ex, "资源对象 {0}/{1}：读取 serialize 文件 {2} 的 pathId {3} 失败（{4}），尝试下一个",
                                    group.Key.Account, group.Key.Bundle, serializedName, entry.Key.PathId, ex.Message);
                                continue;
                            }
                            if (probe is null) continue;
                            // 类型表索引必须与导出时一致，否则加载器会按类型不匹配跳过该对象。
                            if (entry.Key.TypeId is { } expectedType && expectedType != probe.TypeTableIndex)
                            {
                                Log.Warn("资源对象 {0}/{1}：pathId {2} 的类型表索引不一致（导出时 {3}，当前缓存 {4}），跳过该对象",
                                    group.Key.Account, group.Key.Bundle, entry.Key.PathId, expectedType, probe.TypeTableIndex);
                                diagnostics.Add($"{group.Key.Bundle}：pathId {entry.Key.PathId} 的类型表索引不一致" +
                                                $"（导出时 {expectedType}，当前缓存 {probe.TypeTableIndex}），跳过该对象");
                                placed = true;
                                break;
                            }
                            if (!bySerializedFile.TryGetValue(serializedName, out var map))
                                bySerializedFile[serializedName] = map = [];
                            map[entry.Key.PathId] = entry.ReadData();
                            if (Log.IsTraceEnabled)
                                Log.Trace("资源对象 {0}/{1}：pathId {2} 已放入重建计划（serialize 文件 {3}，{4} 字节）",
                                    group.Key.Account, group.Key.Bundle, entry.Key.PathId, serializedName, map[entry.Key.PathId].Length);
                            placed = true;
                            break;
                        }
                        if (!placed)
                        {
                            Log.Warn("资源对象 {0}/{1}：缓存里没有 pathId {2}（游戏可能已更新），跳过该对象",
                                group.Key.Account, group.Key.Bundle, entry.Key.PathId);
                            diagnostics.Add($"{group.Key.Bundle}：缓存里没有 pathId {entry.Key.PathId}（游戏可能已更新），跳过该对象");
                        }
                    }
                    Log.Debug("资源对象 {0}/{1}：探测结束，{2} 次读取失败，{3} 个 serialize 文件待重建",
                        group.Key.Account, group.Key.Bundle, probeFailures, bySerializedFile.Count);

                    var produced = string.Empty;
                    var objectCount = 0;
                    foreach (var (serializedName, replacements) in bySerializedFile)
                    {
                        if (replacements.Count == 0) continue;
                        var output = Path.Combine(rebuildRoot, group.Key.Bundle, "__data");
                        backend.ReplaceBundleSerializedAssets(dataPath, serializedName, replacements, output);
                        Log.Debug("资源对象 {0}/{1}：已重建 serialize 文件 {2}（{3} 个对象）→ {4}",
                            group.Key.Account, group.Key.Bundle, serializedName, replacements.Count, output);
                        produced = output;
                        objectCount += replacements.Count;
                    }
                    if (!File.Exists(produced))
                    {
                        Log.Warn("资源对象跳过 bundle {0}/{1}：没有任何可替换的对象（重建产物 {2} 不存在）",
                            group.Key.Account, group.Key.Bundle, produced.Length == 0 ? "未生成" : produced);
                        diagnostics.Add($"{group.Key.Bundle}：没有任何可替换的对象，跳过该 bundle");
                        continue;
                    }
                    steps.Add(await OverwriteAsync(produced, dataPath, backup,
                        $"对象级就地重写（{objectCount} 个对象 → __data）", cancellationToken));
                    Log.Debug("资源对象 bundle {0}/{1} 已完成：{2} 个对象写回 {3}",
                        group.Key.Account, group.Key.Bundle, objectCount, dataPath);
                }
                finally
                {
                    try { Directory.Delete(rebuildRoot, true); } catch (Exception ex) { Log.Debug(ex, "清理资源重建临时目录失败（不影响结果）：{0}", rebuildRoot); /* 临时目录 */ }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "资源对象 bundle {0}/{1} 写回缓存失败：缓存条目 {2}，跳过该 bundle",
                    group.Key.Account, group.Key.Bundle, dataPath);
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

    /// <summary>只给日志用的文件大小探测：失败返回 -1，绝不影响铺盘流程。</summary>
    private static long FileLengthOrMinusOne(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
