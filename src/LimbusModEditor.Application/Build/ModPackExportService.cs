using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Build;

/// <summary>一个槽位的导出结果（报告的最小单元）。</summary>
/// <param name="Descriptor">槽位描述。</param>
/// <param name="Written">是否真的写出了（false 时 <paramref name="Diagnostics"/> 必须给出原因）。</param>
/// <param name="Directory">产物目录（未写出时该目录不会被创建）。</param>
/// <param name="ArtifactCount">写出的产物个数。</param>
/// <param name="OutputPaths">产物绝对路径（报告可点开）。</param>
/// <param name="Diagnostics">诊断（中文；跳过原因写在这里）。</param>
/// <param name="Statuses">逐资源结果（Carra / Bank 用）。</param>
public sealed record ModPackSlotResult(
    ExportSlotDescriptor Descriptor,
    bool Written,
    string Directory,
    int ArtifactCount,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ExportAssetStatus> Statuses)
{
    /// <summary>报告摘述。</summary>
    public string Describe() => Written
        ? $"{Descriptor.DisplayName}：{ArtifactCount} 个产物 → {Directory}"
        : $"{Descriptor.DisplayName}：未写出（{(Diagnostics.Count > 0 ? Diagnostics[0] : "无内容")}）";
}

/// <summary>一次导出的汇总结果。</summary>
/// <param name="RootDirectory">用户选定的目标目录。</param>
/// <param name="ModName">项目名（产物目录名的前缀）。</param>
/// <param name="Slots">逐槽位结果（按槽位顺序）。</param>
public sealed record ModPackExportResult(string RootDirectory, string ModName, IReadOnlyList<ModPackSlotResult> Slots)
{
    /// <summary>写出的槽位数。</summary>
    public int WrittenSlotCount => Slots.Count(x => x.Written);

    /// <summary>写出的产物总数。</summary>
    public int WrittenFileCount => Slots.Where(x => x.Written).Sum(x => x.ArtifactCount);

    /// <summary>写出的种类目录（去重）。</summary>
    public IReadOnlyList<string> WrittenGroupFolders => Slots
        .Where(x => x.Written)
        .Select(x => ExportLayout.GroupFolder(x.Descriptor.Group, ModName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    /// <summary>一行中文摘要（状态栏 / 报告标题用）。</summary>
    public string Describe() => WrittenSlotCount == 0
        ? "没有写出任何产物（没有可导出的修改）。"
        : $"已写出 {WrittenSlotCount} 个格式槽位 / {WrittenFileCount} 个产物 → {RootDirectory}";
}

/// <summary>
/// plan-16 S4/S5：<b>按计划执行导出</b>——把每个「已点亮」的槽位写成
/// <c>&lt;目标目录&gt;/&lt;项目名&gt;_&lt;种类&gt;/&lt;格式&gt;/…</c> 下的产物。
///
/// <para><b>只复用不新造</b>：bank 走 <see cref="BankFormatHandler"/> + <c>BankAssembler</c>，
/// rebank 走 <c>RebankArchive</c>，carra 走 <see cref="UnityCacheExportService"/>——
/// 格式层一个都不重写。</para>
///
/// <para><b>空槽不建目录</b>：只有真的写出产物时才创建二级目录；跳过的槽位一个文件夹都不留。</para>
///
/// <para><b>不猜</b>：任何「拿不到真实数据就写不出正确包」的情形（缺 FMOD DLL 时做差分、
/// 样本结构与原版对不上、找不到原版 bank）一律跳过并在诊断里说明，绝不产出一个
/// 加载器会静默跳过的产物。</para>
/// </summary>
public sealed class ModPackExportService
{
    private readonly UnityCacheExportService _unityExporter = new();

    /// <param name="projectRoot">项目根目录（<c>.lmeproj</c> 所在目录）：重打包中间产物与
    /// 临时文件落在这里的 <c>builds/</c> 下。</param>
    public async Task<ModPackExportResult> ExportAsync(
        ModProject project,
        string projectRoot,
        ModExportPlan plan,
        ModExportPlanContext context,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(plan);
        context ??= new ModExportPlanContext();

        var slots = new List<ModPackSlotResult>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Planned)
            {
                slots.Add(new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                    [item.SkipReason ?? "没有可导出的修改"], []));
                continue;
            }
            slots.Add(item.Descriptor.Slot switch
            {
                ExportSlot.Bank => await ExportBankAsync(plan, item, context, progress, cancellationToken),
                ExportSlot.Rebank => await ExportRebankAsync(plan, item, context, progress, cancellationToken),
                ExportSlot.Carra => await ExportCarraAsync(project, projectRoot, plan, item, context, progress, cancellationToken),
                ExportSlot.Lunartique => await ExportLunartiqueAsync(project, projectRoot, plan, item, context, progress, cancellationToken),
                ExportSlot.LangBus or ExportSlot.LangPatch or ExportSlot.LangPathset =>
                    ExportLangAsync(plan, item),
                ExportSlot.StaticMod => ExportStaticMod(plan, item),
                _ => Skip(item, "未实现的槽位"),
            });
        }
        return new ModPackExportResult(plan.RootDirectory, plan.ModName, slots);
    }

    private static ModPackSlotResult Skip(ModExportPlanItem item, string reason)
        => new(item.Descriptor, false, item.Directory, 0, [], [reason], []);

    // ── 音频：_fmod/bank（整包）─────────────────────────────────────

    /// <summary>
    /// 每个被改的 bank 写一个完整 <c>.bank</c>。产物名 = 游戏内目标 bank 文件名
    /// （加载器按文件名整包替换）。
    /// </summary>
    private static async Task<ModPackSlotResult> ExportBankAsync(
        ModExportPlan plan, ModExportPlanItem item, ModExportPlanContext context,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var statuses = new List<ExportAssetStatus>();
        var outputs = new List<string>();
        foreach (var edit in plan.Banks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"音频整包：{edit.TargetBankFileName}");
                var package = LoadBank(edit.OriginalBankPath);
                await ApplyAudioEditsAsync(package, edit, context, statuses, cancellationToken);
                var output = Path.Combine(item.Directory, edit.TargetBankFileName);
                Directory.CreateDirectory(item.Directory);
                var handler = new BankFormatHandler();
                await using (var stream = File.Create(output))
                {
                    await handler.ExportAsync(new ModPackage { SourceFormat = ModFormatKind.Bank, Payload = package },
                        stream, new ExportContext(ModFormatKind.Bank, CancellationToken: cancellationToken));
                }
                outputs.Add(output);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add($"{edit.TargetBankFileName}：{ex.Message}");
            }
        }
        return new ModPackSlotResult(item.Descriptor, outputs.Count > 0, item.Directory,
            outputs.Count, outputs, diagnostics, statuses);
    }

    // ── 音频：_fmod/rebank（差分）───────────────────────────────────

    /// <summary>
    /// 每个被改的 bank 写一个 <c>.rebank</c>。
    ///
    /// <para><b>为什么必须逐样本解码</b>：加载器（LCTA <c>launcher/bankmod.py</c>）按
    /// <c>(FSB 序号, <b>样本名</b>)</c> 在目标 bank 里找条目；条目名不是真实样本名就一条都
    /// 替换不上，而它还会因为「替换数 0」报错回滚。所以这里：① 用 FSB5 解析器取样本名与数量，
    /// 与原版逐样本对齐（数量/名字对不上 = 不是样本级替换，直接跳过并说明）；
    /// ② 用 FMOD 解码每个样本成 WAV 写进包；③ <c>base_bank</c> = 目标 bank 文件名。</para>
    /// </summary>
    private static async Task<ModPackSlotResult> ExportRebankAsync(
        ModExportPlan plan, ModExportPlanItem item, ModExportPlanContext context,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var outputs = new List<string>();
        foreach (var edit in plan.Banks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"音频差分：{edit.TargetBankFileName}");
                var original = LoadBank(edit.OriginalBankPath);
                var modified = LoadBank(edit.OriginalBankPath);
                await ApplyAudioEditsAsync(modified, edit, context, [], cancellationToken);

                var files = new List<(int Index, string Name, byte[] Data, string Status)>();
                foreach (var fsbIndex in edit.FsbIndexes)
                {
                    if (fsbIndex >= modified.FsbData.Count || fsbIndex >= original.FsbData.Count)
                    {
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：超出 bank 的 FSB 数量，跳过该 FSB 的差分");
                        continue;
                    }
                    var before = Fsb5Parser.TryParse(original.FsbData[fsbIndex]);
                    var after = Fsb5Parser.TryParse(modified.FsbData[fsbIndex]);
                    if (after is null)
                    {
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：改后的 FSB5 无法解析，跳过该 FSB 的差分");
                        continue;
                    }
                    if (!SampleLayoutMatches(before, after, out var layoutReason))
                    {
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：{layoutReason}，" +
                                        "差分包的条目按（FSB 序号 + 样本名）匹配，结构对不上就不写（宁缺勿错）");
                        continue;
                    }
                    if (!context.FmodCodecAvailable)
                    {
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：缺少 FMOD DLL，无法解码逐样本 WAV");
                        continue;
                    }
                    using var codec = new NativeFmodAudioCodec(context.FmodDirectory!);
                    foreach (var sample in after.Samples)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = sample.Name;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex} 样本 {sample.Index}：FSB 里没有样本名，" +
                                            "加载器按名字匹配，跳过该样本");
                            continue;
                        }
                        try
                        {
                            var wav = await codec.DecodeFsbToWaveAsync(modified.FsbData[fsbIndex], sample.Index, cancellationToken);
                            var status = before is null || sample.Index >= before.Samples.Count ? "added" : "modified";
                            files.Add((fsbIndex, name + ".wav", wav, status));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex} 样本 {name}：解码失败（{ex.Message}），跳过该样本");
                        }
                    }
                }

                if (files.Count == 0)
                {
                    diagnostics.Add($"{edit.TargetBankFileName}：没有可写入的样本差分，未生成 .rebank");
                    continue;
                }

                var package = RebankDiffService.Create(edit.TargetBankFileName, plan.ModName,
                    version: "1.0", author: string.Empty, description: string.Empty, files);
                var output = Path.Combine(item.Directory, edit.BaseName + ".rebank");
                Directory.CreateDirectory(item.Directory);
                using (var stream = File.Create(output)) RebankArchive.Write(package, stream);
                outputs.Add(output);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add($"{edit.TargetBankFileName}：{ex.Message}");
            }
        }
        return new ModPackSlotResult(item.Descriptor, outputs.Count > 0, item.Directory,
            outputs.Count, outputs, diagnostics, []);
    }

    /// <summary>改后必须仍是「同结构样本集」——否则 FSB 级替换无法用样本级差分包表达。</summary>
    private static bool SampleLayoutMatches(Fsb5Info? before, Fsb5Info after, out string reason)
    {
        if (before is null)
        {
            reason = "原版 FSB5 无法解析（无法判断改动是否是样本级替换）";
            return false;
        }
        if (before.Samples.Count != after.Samples.Count)
        {
            reason = $"样本数量变了（原版 {before.Samples.Count} → 改后 {after.Samples.Count}）";
            return false;
        }
        for (var i = 0; i < after.Samples.Count; i++)
        {
            var beforeName = before.Samples[i].Name;
            var afterName = after.Samples[i].Name;
            if (string.IsNullOrWhiteSpace(beforeName) || string.IsNullOrWhiteSpace(afterName))
            {
                reason = $"样本 {i} 缺少样本名";
                return false;
            }
            if (!string.Equals(beforeName, afterName, StringComparison.Ordinal))
            {
                reason = $"样本 {i} 的名字变了（{beforeName} → {afterName}）";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    // ── 语言文本：_text/{bus|patch|pathset} ─────────────────────────

    /// <summary>
    /// 每条被改文本表在每个格式槽位各出一份文件（plan-16 §3）：产物的相对路径沿用
    /// 条目路径（<c>StoryData/S1.json</c>），因此子目录结构在多语言目录下天然隔离。
    ///
    /// <para>bus 有「可表达性门」：差分里出现删除/数组增删时只跳过 bus 槽位并说明原因，
    /// patch / pathset 照常产出。</para>
    /// </summary>
    private static ModPackSlotResult ExportLangAsync(ModExportPlan plan, ModExportPlanItem item)
    {
        var format = item.Descriptor.Slot switch
        {
            ExportSlot.LangBus => LangExportFormat.Bus,
            ExportSlot.LangPatch => LangExportFormat.Patch,
            _ => LangExportFormat.Pathset,
        };
        var outputs = new List<string>();
        var diagnostics = new List<string>();
        foreach (var entry in plan.LangEntries)
        {
            var result = LangExportFormatter
                .Format(entry.RelativePath, entry.PatchKey, plan.ModName, entry.VanillaText, entry.ModifiedText)
                .First(x => x.Format == format);
            foreach (var note in result.Notes) diagnostics.Add($"{entry.RelativePath}：{note}");
            if (!result.Written) continue;
            var output = Path.Combine(item.Directory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, result.JsonText!, new System.Text.UTF8Encoding(false));
            outputs.Add(output);
        }
        return new ModPackSlotResult(item.Descriptor, outputs.Count > 0, item.Directory,
            outputs.Count, outputs, diagnostics, []);
    }

    // ── 静态数据：_static/staticmod ─────────────────────────────────

    /// <summary>
    /// 把静态表编辑集写成 LCTA 兼容的 <c>.staticmod</c>（plan-16 S8）。
    ///
    /// <para>复用既有能力：<see cref="StaticModService.CreateJsonPatchPackage"/> 生成
    /// 「官方 vs 修改」的 RFC6902 补丁与 manifest（<c>dataClass/file/container/opType=jsonpatch</c>），
    /// <see cref="StaticModService.Write"/> 打包——两者本来就是按 <c>launcher/staticmod.py</c>
    /// 的布局写的，本轮只是把它从「静态页的按钮」搬到统一的槽位导出。</para>
    /// </summary>
    private static ModPackSlotResult ExportStaticMod(ModExportPlan plan, ModExportPlanItem item)
    {
        if (plan.StaticEntries.Count == 0)
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [], ["没有静态表修改"], []);
        var work = Path.Combine(Path.GetTempPath(), "lme-staticmod-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var entries = new List<(string DataClass, string File, string? Container, string OfficialJsonPath, string ModifiedJsonPath)>();
            for (var index = 0; index < plan.StaticEntries.Count; index++)
            {
                var edit = plan.StaticEntries[index];
                var officialPath = Path.Combine(work, $"official-{index}.json");
                var modifiedPath = Path.Combine(work, $"modified-{index}.json");
                File.WriteAllText(officialPath, edit.OfficialText, new System.Text.UTF8Encoding(false));
                File.WriteAllText(modifiedPath, edit.ModifiedText, new System.Text.UTF8Encoding(false));
                entries.Add((edit.Entry.DataClass, edit.Entry.FileName,
                    string.IsNullOrWhiteSpace(edit.Entry.ContainerEntry) ? null : edit.Entry.ContainerEntry,
                    officialPath, modifiedPath));
            }
            var service = new StaticModService();
            var package = service.CreateJsonPatchPackage(plan.ModName, "1.0",
                "由 Limbus Mod Editor 生成（静态数据表 RFC6902 补丁）", entries);
            // 单文件归档按来源名命名（无来源时退回项目名）——但静态数据没有「源文件」概念，
            // 因此固定用项目名（与 launcher 侧真实样本一致：一个模组一个 .staticmod）。
            var output = Path.Combine(item.Directory, ExportLayout.Sanitize(plan.ModName) + ".staticmod");
            Directory.CreateDirectory(item.Directory);
            service.Write(package, output);
            return new ModPackSlotResult(item.Descriptor, true, item.Directory, 1, [output],
                [$"{package.Patches.Count} 个补丁条目（加载器需开启「启用静态数据 Mod」并由它重打包 bundle + 双写 catalog）"], []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [], [ex.Message], []);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception) { /* 临时目录 */ }
        }
    }

    // ── 资源：_data/lunartique ──────────────────────────────────────

    /// <summary>
    /// 把对象级修改写成 Lunartique 包（plan-16 S8）。
    ///
    /// <para><b>加载器语义</b>（<c>launcher/compress.py</c> + <c>patch.py</c>）：zip 里要有
    /// <c>&lt;根&gt;/Uninstallation/&lt;account&gt;/&lt;bundle&gt;/__data</c> 与
    /// <c>&lt;根&gt;/Installation/&lt;account&gt;/&lt;bundle&gt;/__data</c> 两侧同构的条目；
    /// 加载器把两侧 bundle 的每个对象按 pathId 比对，只把有差异的对象写进目标 bundle 缓存。</para>
    ///
    /// <para><b>本实现的诚实边界</b>：Uninstallation 侧需要「原版 bundle 的完整字节」。
    /// 编辑器只在配置了 Unity 缓存目录时能拿到它；拿不到时——若把 Installation 侧填成
    /// 「改后 bundle 全量对象」，加载器会认为所有对象都需要写回（改动面过大且与 carra 通道重复），
    /// 因此这里<b>只做已知正确的形态</b>：两侧都从缓存原版 bundle 派生
    /// （Uninstallation = 原版、Installation = 改后），缓存缺失则整份跳过并说明。</para>
    /// </summary>
    private async Task<ModPackSlotResult> ExportLunartiqueAsync(
        ModProject project, string projectRoot, ModExportPlan plan, ModExportPlanItem item,
        ModExportPlanContext context, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.UnityCacheDirectory) || !Directory.Exists(context.UnityCacheDirectory))
        {
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                ["Lunartique 包需要原版 bundle 作为 Uninstallation 侧，但没配置（或不存在）Unity 缓存目录；" +
                 "资源改动请用 carra 格式（加载器同样支持）"], []);
        }
        try
        {
            // 先按 carra 的既有能力把「改后 bundle」重打包出来，再从它派生两侧条目。
            var staging = Path.Combine(Path.GetTempPath(), "lme-lunartique-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var carraOutput = Path.Combine(staging, "objects.carra");
                progress?.Report("Lunartique：重打包被编辑的 bundle…");
                var carra = await _unityExporter.ExportCarra2Async(project, projectRoot, carraOutput,
                    context.UnityCacheDirectory, cancellationToken, progress);

                CarraPackage objects;
                using (var stream = File.OpenRead(carraOutput)) objects = CarraArchive.Read(stream);

                var package = new LunartiquePackage { Root = ExportLayout.Sanitize(plan.ModName) };
                var usedBundles = 0;
                foreach (var group in objects.Entries.GroupBy(x => (x.Key.Account, x.Key.Bundle)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cacheBundle = Path.Combine(context.UnityCacheDirectory, group.Key.Account, group.Key.Bundle, "__data");
                    if (!File.Exists(cacheBundle))
                    {
                        continue; // 缓存里没有原版 bundle：这一组跳过（下面按 usedBundles 报）
                    }
                    // 两侧同构：Uninstallation = 原版 bundle 字节，Installation = 改后对象写回后的字节。
                    var modified = Path.Combine(staging, "mod", group.Key.Account, group.Key.Bundle, "__data");
                    Directory.CreateDirectory(Path.GetDirectoryName(modified)!);
                    using (var backend = new AssetsToolsBackend())
                    {
                        var serializedNames = backend.BundleSerializedFileNames(cacheBundle);
                        var replacements = group.ToDictionary(x => x.Key.PathId, x => x.ReadData());
                        foreach (var serializedName in serializedNames)
                            backend.ReplaceBundleSerializedAssets(cacheBundle, serializedName, replacements, modified);
                    }
                    if (!File.Exists(modified)) continue;
                    package.Resources.Add(new LunartiqueResource(
                        $"{group.Key.Account}/{group.Key.Bundle}/__data",
                        await File.ReadAllBytesAsync(cacheBundle, cancellationToken),
                        await File.ReadAllBytesAsync(modified, cancellationToken)));
                    usedBundles++;
                }
                if (usedBundles == 0)
                {
                    return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                        ["缓存里找不到任何被编辑对象所属的原版 bundle（游戏可能已更新），未生成 Lunartique 包"], []);
                }

                var output = Path.Combine(item.Directory, ExportLayout.Sanitize(plan.ModName) + ".zip");
                Directory.CreateDirectory(item.Directory);
                await using (var stream = File.Create(output))
                    LunartiqueArchive.Write(package, stream, preserveUnknownFiles: true);
                return new ModPackSlotResult(item.Descriptor, true, item.Directory, 1, [output],
                    carra.Diagnostics.ToArray(), carra.AssetStatuses);
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch (Exception) { /* 临时目录 */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [], [ex.Message], []);
        }
    }

    // ── 资源：_data/carra ───────────────────────────────────────────

    private async Task<ModPackSlotResult> ExportCarraAsync(
        ModProject project, string projectRoot, ModExportPlan plan, ModExportPlanItem item,
        ModExportPlanContext context, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(item.Directory);
            // 单文件归档：按**来源名**命名（无来源时退回项目名），与 staticmod/lunartique 同一惯例。
            var sourceName = Path.GetFileNameWithoutExtension(project.Sources
                .Where(x => x.Format is ModFormatKind.Carra or ModFormatKind.Carra2 && !string.IsNullOrWhiteSpace(x.Path))
                .OrderByDescending(x => x.ImportedAt)
                .FirstOrDefault()?.Path ?? string.Empty);
            var fileName = ExportLayout.Sanitize(string.IsNullOrWhiteSpace(sourceName) ? plan.ModName : sourceName) + ".carra";
            var output = Path.Combine(item.Directory, fileName);
            var result = await _unityExporter.ExportCarra2Async(
                project, projectRoot, output, context.UnityCacheDirectory, cancellationToken, progress);
            return new ModPackSlotResult(item.Descriptor, true, item.Directory, 1, [output],
                result.Diagnostics.ToArray(), result.AssetStatuses);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [], [ex.Message], []);
        }
    }

    // ── 共用 ────────────────────────────────────────────────────────

    /// <summary>读一个 bank 文件成可写包（导出与调试共用）。</summary>
    public static BankPackage LoadBank(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var info = BankParser.TryParse(bytes) ?? throw new InvalidDataException("无法解析原版 bank 文件。");
        var package = new BankPackage { OriginalData = bytes, Info = info };
        package.FsbData.AddRange(BankParser.Extract(bytes, info).Select(x => x.ToArray()));
        return package;
    }

    /// <summary>
    /// 把该 bank 上登记的样本替换写进 <paramref name="package"/>：WAV 用 FMOD/FSBANK 编码成
    /// FSB5 后按 FSB 序号整块替换（与 bank 页「用 WAV 替换…」的语义一致）。
    /// <b>导出与调试共用本方法</b>——两条链路的音频语义必须逐字节一致。
    /// </summary>
    public static async Task ApplyAudioEditsAsync(
        BankPackage package, BankEdit edit, ModExportPlanContext context,
        List<ExportAssetStatus> statuses, CancellationToken cancellationToken)
    {
        IFmodAudioCodec? codec = null;
        try
        {
            foreach (var asset in edit.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!asset.Metadata.TryGetValue("replacementPath", out var replacement) || !File.Exists(replacement)) continue;
                var index = BankEdit.ParseFsbIndex(asset.LogicalPath);
                if (index < 0 || index >= package.FsbData.Count)
                {
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped,
                        $"FSB 序号超出 bank 范围（bank 有 {package.FsbData.Count} 个 FSB）"));
                    continue;
                }
                var data = await File.ReadAllBytesAsync(replacement, cancellationToken);
                if (IsWave(data))
                {
                    if (!context.FmodCodecAvailable)
                    {
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped,
                            "WAV 需要 FMOD/FSBANK 编码器（未找到 FMOD DLL）"));
                        continue;
                    }
                    codec ??= new NativeFmodAudioCodec(context.FmodDirectory!);
                    data = await codec.EncodeWaveToFsbAsync(data, cancellationToken);
                }
                if (!IsFsb5(data))
                {
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped,
                        "替换内容既不是 WAV 也不是完整的 FSB5 数据"));
                    continue;
                }
                package.FsbData[index] = data;
                package.HasModifications = true;
                var details = Fsb5Parser.TryParse(data);
                statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied,
                    details is null ? null : $"{details.SampleCount} 个样本 · {details.CodecName}"));
            }
        }
        finally
        {
            (codec as IDisposable)?.Dispose();
        }
    }

    private static bool IsWave(byte[] data)
        => data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8);

    private static bool IsFsb5(byte[] data)
        => data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("FSB5"u8);
}
