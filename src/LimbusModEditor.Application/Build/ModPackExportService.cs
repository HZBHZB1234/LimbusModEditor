using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Unity;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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

        using var scope = Log.Scope("导出模组包");
        Log.Info("导出开始：项目「{0}」，计划 {1} 个槽位，目标目录 {2}，项目根 {3}",
            plan.ModName, plan.Items.Count, plan.RootDirectory ?? "-", projectRoot);

        // 上报节流：逐 bank / 逐样本的循环同样可以很密（详见 ThrottledProgress）。
        var throttled = new ThrottledProgress(progress);
        var slots = new List<ModPackSlotResult>();
        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
                Log.Info("取消请求：导出在槽位「{0}」开始前中止", item.Descriptor.DisplayName);
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Planned)
            {
                Log.Warn("槽位「{0}」未点亮，跳过：{1}",
                    item.Descriptor.DisplayName, item.SkipReason ?? "没有可导出的修改");
                slots.Add(new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                    [item.SkipReason ?? "没有可导出的修改"], []));
                continue;
            }
            if (Log.IsDebugEnabled)
                Log.Debug("槽位「{0}」开始：槽位类型 {1}，产物目录 {2}",
                    item.Descriptor.DisplayName, item.Descriptor.Slot.ToString(), item.Directory);
            var slot = item.Descriptor.Slot switch
            {
                ExportSlot.Bank => await ExportBankAsync(plan, item, context, throttled, cancellationToken),
                ExportSlot.Rebank => await ExportRebankAsync(plan, item, context, throttled, cancellationToken),
                ExportSlot.Carra => await ExportCarraAsync(project, projectRoot, plan, item, context, throttled, cancellationToken),
                ExportSlot.Lunartique => await ExportLunartiqueAsync(project, projectRoot, plan, item, context, throttled, cancellationToken),
                ExportSlot.LangBus or ExportSlot.LangPatch or ExportSlot.LangPathset =>
                    ExportLangAsync(plan, item, cancellationToken),
                ExportSlot.StaticMod => ExportStaticMod(plan, item, cancellationToken),
                _ => Skip(item, "未实现的槽位"),
            };
            if (Log.IsDebugEnabled)
                Log.Debug("槽位「{0}」结束：{1}，产物 {2} 个，诊断 {3} 条",
                    item.Descriptor.DisplayName, slot.Written ? "已写出" : "未写出",
                    slot.ArtifactCount, slot.Diagnostics.Count);
            if (!slot.Written)
                Log.Warn("槽位「{0}」未写出：{1}",
                    item.Descriptor.DisplayName, slot.Diagnostics.Count > 0 ? slot.Diagnostics[0] : "无诊断信息");
            slots.Add(slot);
        }
        throttled.Flush();
        var result = new ModPackExportResult(plan.RootDirectory!, plan.ModName, slots);
        Log.Info("导出完成：{0} 个槽位写出 / {1} 个产物，节流丢弃进度上报 {2} 条",
            result.WrittenSlotCount, result.WrittenFileCount, throttled.SuppressedCount);
        return result;
    }

    private static ModPackSlotResult Skip(ModExportPlanItem item, string reason)
    {
        Log.Warn("槽位「{0}」按兜底分支跳过：{1}", item.Descriptor.DisplayName, reason);
        return new(item.Descriptor, false, item.Directory, 0, [], [reason], []);
    }

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
                var bankWatch = System.Diagnostics.Stopwatch.StartNew();
                Log.Debug("bank 整包开始：目标 {0}，原版 bank {1}", edit.TargetBankFileName, edit.OriginalBankPath ?? "-");
                var package = LoadBank(edit.OriginalBankPath!);
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
                Log.Debug("bank 整包写出：{0}（输入 {1} 字节 / {2} 个 FSB，输出 {3} 字节，耗时 {4} ms）",
                    output, package.OriginalData.Length, package.FsbData.Count, new FileInfo(output).Length,
                    bankWatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "bank 整包导出失败：目标 {0}，原版 bank {1}", edit.TargetBankFileName, edit.OriginalBankPath ?? "-");
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
                var rebankWatch = System.Diagnostics.Stopwatch.StartNew();
                Log.Debug("rebank 差分开始：目标 {0}，待处理 FSB {1} 个",
                    edit.TargetBankFileName, edit.FsbIndexes.Count);
                var original = LoadBank(edit.OriginalBankPath!);
                var modified = LoadBank(edit.OriginalBankPath!);
                await ApplyAudioEditsAsync(modified, edit, context, [], cancellationToken);

                var files = new List<(int Index, string Name, byte[] Data, string Status)>();
                foreach (var fsbIndex in edit.FsbIndexes)
                {
                    if (fsbIndex >= modified.FsbData.Count || fsbIndex >= original.FsbData.Count)
                    {
                        Log.Warn("rebank 跳过 FSB {0}（{1}）：超出 bank 的 FSB 数量（原版 {2} / 改后 {3}）",
                            fsbIndex, edit.TargetBankFileName, original.FsbData.Count, modified.FsbData.Count);
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：超出 bank 的 FSB 数量，跳过该 FSB 的差分");
                        continue;
                    }
                    var before = Fsb5Parser.TryParse(original.FsbData[fsbIndex]);
                    var after = Fsb5Parser.TryParse(modified.FsbData[fsbIndex]);
                    if (after is null)
                    {
                        Log.Warn("rebank 跳过 FSB {0}（{1}）：改后的 FSB5 无法解析（{2} 字节）",
                            fsbIndex, edit.TargetBankFileName, modified.FsbData[fsbIndex].Length);
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：改后的 FSB5 无法解析，跳过该 FSB 的差分");
                        continue;
                    }
                    if (!SampleLayoutMatches(before, after, out var layoutReason))
                    {
                        Log.Warn("rebank 跳过 FSB {0}（{1}）：样本结构对不上——{2}",
                            fsbIndex, edit.TargetBankFileName, layoutReason);
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：{layoutReason}，" +
                                        "差分包的条目按（FSB 序号 + 样本名）匹配，结构对不上就不写（宁缺勿错）");
                        continue;
                    }
                    if (!context.FmodCodecAvailable)
                    {
                        Log.Warn("rebank 跳过 FSB {0}（{1}）：缺少 FMOD DLL，无法解码逐样本 WAV",
                            fsbIndex, edit.TargetBankFileName);
                        diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex}：缺少 FMOD DLL，无法解码逐样本 WAV");
                        continue;
                    }
                    using var codec = new NativeFmodAudioCodec(context.FmodDirectory!);
                    var sampleWatch = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var sample in after.Samples)
                    {
                        // 取消检查点就在这里：解码一个大样本可能要数秒，取消响应慢的现场证据全靠这条 Debug
                        if (Log.IsDebugEnabled)
                            Log.Debug("rebank 正在处理：{0} FSB {1} 样本 {2}（已解出 {3} 个，本节已耗时 {4} ms）",
                                edit.TargetBankFileName, fsbIndex, sample.Name ?? "-", files.Count, sampleWatch.ElapsedMilliseconds);
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = sample.Name;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            Log.Warn("rebank 跳过样本 {0}（{1} FSB {2}）：FSB 里没有样本名，加载器按名字匹配",
                                sample.Index, edit.TargetBankFileName, fsbIndex);
                            diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex} 样本 {sample.Index}：FSB 里没有样本名，" +
                                            "加载器按名字匹配，跳过该样本");
                            continue;
                        }
                        try
                        {
                            var wav = await codec.DecodeFsbToWaveAsync(modified.FsbData[fsbIndex], sample.Index, cancellationToken);
                            var status = before is null || sample.Index >= before.Samples.Count ? "added" : "modified";
                            files.Add((fsbIndex, name + ".wav", wav, status));
                            if (Log.IsDebugEnabled)
                                Log.Debug("rebank 样本解码完成：{0} FSB {1} 样本 {2}（{3} 字节，{4}）",
                                    edit.TargetBankFileName, fsbIndex, name, wav.Length, status);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Error(ex, "rebank 样本解码失败：{0} FSB {1} 样本 {2}，跳过该样本",
                                edit.TargetBankFileName, fsbIndex, name);
                            diagnostics.Add($"{edit.TargetBankFileName} FSB {fsbIndex} 样本 {name}：解码失败（{ex.Message}），跳过该样本");
                        }
                    }
                }

                if (files.Count == 0)
                {
                    Log.Warn("rebank 未生成：{0} 没有可写入的样本差分", edit.TargetBankFileName);
                    diagnostics.Add($"{edit.TargetBankFileName}：没有可写入的样本差分，未生成 .rebank");
                    continue;
                }

                var package = RebankDiffService.Create(edit.TargetBankFileName, plan.ModName,
                    version: "1.0", author: string.Empty, description: string.Empty, files);
                var output = Path.Combine(item.Directory, edit.BaseName + ".rebank");
                Directory.CreateDirectory(item.Directory);
                using (var stream = File.Create(output)) RebankArchive.Write(package, stream);
                outputs.Add(output);
                Log.Debug("rebank 差分写出：{0}（{1} 个样本 / {2} 字节，总耗时 {3} ms）",
                    output, files.Count, new FileInfo(output).Length, rebankWatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "rebank 差分失败：目标 {0}，原版 bank {1}",
                    edit.TargetBankFileName, edit.OriginalBankPath ?? "-");
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
    private static ModPackSlotResult ExportLangAsync(
        ModExportPlan plan, ModExportPlanItem item, CancellationToken cancellationToken)
    {
        var format = item.Descriptor.Slot switch
        {
            ExportSlot.LangBus => LangExportFormat.Bus,
            ExportSlot.LangPatch => LangExportFormat.Patch,
            _ => LangExportFormat.Pathset,
        };
        var outputs = new List<string>();
        var diagnostics = new List<string>();
        var langWatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("语言槽位 {0} 开始：{1} 条文本表，产物目录 {2}",
            item.Descriptor.DisplayName, plan.LangEntries.Count, item.Directory);
        foreach (var entry in plan.LangEntries)
        {
            if (Log.IsDebugEnabled)
                Log.Debug("语言槽位 {0} 正在处理：{1}（已写出 {2} 个，已耗时 {3} ms）",
                    item.Descriptor.DisplayName, entry.RelativePath ?? "-", outputs.Count, langWatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            var result = LangExportFormatter
                .Format(entry.RelativePath!, entry.PatchKey, plan.ModName, entry.VanillaText, entry.ModifiedText)
                .First(x => x.Format == format);
            foreach (var note in result.Notes) diagnostics.Add($"{entry.RelativePath}：{note}");
            if (!result.Written)
            {
                Log.Warn("语言槽位 {0} 跳过条目 {1}：该格式下未产出内容（{2}）",
                    item.Descriptor.DisplayName, entry.RelativePath ?? "-",
                    result.Notes.Count > 0 ? result.Notes[0] : "无说明");
                continue;
            }
            var output = Path.Combine(item.Directory, entry.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, result.JsonText!, new System.Text.UTF8Encoding(false));
            outputs.Add(output);
            if (Log.IsDebugEnabled)
                Log.Debug("语言槽位 {0} 写出：{1}（{2} 字节）",
                    item.Descriptor.DisplayName, output, new FileInfo(output).Length);
        }
        Log.Debug("语言槽位 {0} 结束：{1} 个产物，诊断 {2} 条，耗时 {3} ms",
            item.Descriptor.DisplayName, outputs.Count, diagnostics.Count, langWatch.ElapsedMilliseconds);
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
    private static ModPackSlotResult ExportStaticMod(
        ModExportPlan plan, ModExportPlanItem item, CancellationToken cancellationToken)
    {
        if (plan.StaticEntries.Count == 0)
        {
            Log.Warn("静态数据槽位跳过：没有静态表修改");
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [], ["没有静态表修改"], []);
        }
        var work = Path.Combine(Path.GetTempPath(), "lme-staticmod-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var staticWatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("静态数据槽位开始：{0} 条表编辑，临时目录 {1}，产物目录 {2}",
            plan.StaticEntries.Count, work, item.Directory);
        try
        {
            var entries = new List<(string DataClass, string File, string? Container, string OfficialJsonPath, string ModifiedJsonPath)>();
            for (var index = 0; index < plan.StaticEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edit = plan.StaticEntries[index];
                var officialPath = Path.Combine(work, $"official-{index}.json");
                var modifiedPath = Path.Combine(work, $"modified-{index}.json");
                File.WriteAllText(officialPath, edit.OfficialText, new System.Text.UTF8Encoding(false));
                File.WriteAllText(modifiedPath, edit.ModifiedText, new System.Text.UTF8Encoding(false));
                if (Log.IsDebugEnabled)
                    Log.Debug("静态数据表写出中间文件：{0} / {1}（官方 {2} 字节 / 改后 {3} 字节）",
                        edit.Entry.DataClass ?? "-", edit.Entry.FileName ?? "-",
                        edit.OfficialText.Length, edit.ModifiedText.Length);
                entries.Add((edit.Entry.DataClass!, edit.Entry.FileName!,
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
            Log.Debug("静态数据槽位写出：{0}（{1} 个补丁条目，{2} 字节，耗时 {3} ms）",
                output, package.Patches.Count, new FileInfo(output).Length, staticWatch.ElapsedMilliseconds);
            return new ModPackSlotResult(item.Descriptor, true, item.Directory, 1, [output],
                [$"{package.Patches.Count} 个补丁条目（加载器需开启「启用静态数据 Mod」并由它重打包 bundle + 双写 catalog）"], []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "静态数据槽位导出失败：{0} 条表编辑，临时目录 {1}", plan.StaticEntries.Count, work);
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
            Log.Warn("Lunartique 槽位跳过：未配置（或不存在）Unity 缓存目录 {0}，拿不到 Uninstallation 侧的原版 bundle",
                context.UnityCacheDirectory ?? "-");
            return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                ["Lunartique 包需要原版 bundle 作为 Uninstallation 侧，但没配置（或不存在）Unity 缓存目录；" +
                 "资源改动请用 carra 格式（加载器同样支持）"], []);
        }
        var lunartiqueWatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("Lunartique 槽位开始：缓存目录 {0}，产物目录 {1}", context.UnityCacheDirectory, item.Directory);
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

                // CarraArchive.Read 会把整包逐条目读成数组（同步重活），放后台线程。
                var readWatch = System.Diagnostics.Stopwatch.StartNew();
                var objects = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(carraOutput);
                    return CarraArchive.Read(stream);
                }, cancellationToken).ConfigureAwait(false);
                Log.Debug("Lunartique 读取中间 carra：{0}（{1} 字节 → {2} 个对象条目，耗时 {3} ms）",
                    carraOutput, new FileInfo(carraOutput).Length, objects.Entries.Count, readWatch.ElapsedMilliseconds);

                var package = new LunartiquePackage { Root = ExportLayout.Sanitize(plan.ModName) };
                var bundleWatch = System.Diagnostics.Stopwatch.StartNew();
                var usedBundles = await Task.Run(async () =>
                {
                    var used = 0;
                    foreach (var group in objects.Entries.GroupBy(x => (x.Key.Account, x.Key.Bundle)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Log.IsDebugEnabled)
                            Log.Debug("Lunartique 正在处理：{0}/{1}（{2} 个对象，已用 {3} 个 bundle，本段已耗时 {4} ms）",
                                group.Key.Account ?? "-", group.Key.Bundle ?? "-", group.Count(), used,
                                bundleWatch.ElapsedMilliseconds);
                        var cacheBundle = Path.Combine(context.UnityCacheDirectory!, group.Key.Account!, group.Key.Bundle!, "__data");
                        if (!File.Exists(cacheBundle))
                        {
                            Log.Warn("Lunartique 跳过 {0}/{1}：缓存里没有原版 bundle {2}",
                                group.Key.Account ?? "-", group.Key.Bundle ?? "-", cacheBundle);
                            continue; // 缓存里没有原版 bundle：这一组跳过（下面按 usedBundles 报）
                        }
                        // 两侧同构：Uninstallation = 原版 bundle 字节，Installation = 改后对象写回后的字节。
                        var modified = Path.Combine(staging, "mod", group.Key.Account!, group.Key.Bundle!, "__data");
                        Directory.CreateDirectory(Path.GetDirectoryName(modified)!);
                        using (var backend = new AssetsToolsBackend())
                        {
                            var serializedNames = backend.BundleSerializedFileNames(cacheBundle);
                            var replacements = group.ToDictionary(x => x.Key.PathId, x => x.ReadData());
                            foreach (var serializedName in serializedNames)
                                backend.ReplaceBundleSerializedAssets(cacheBundle, serializedName, replacements, modified);
                        }
                        if (!File.Exists(modified))
                        {
                            Log.Warn("Lunartique 跳过 {0}/{1}：对象写回后没有生成 {2}",
                                group.Key.Account ?? "-", group.Key.Bundle ?? "-", modified);
                            continue;
                        }
                        var originalBytes = await File.ReadAllBytesAsync(cacheBundle, cancellationToken).ConfigureAwait(false);
                        var modifiedBytes = await File.ReadAllBytesAsync(modified, cancellationToken).ConfigureAwait(false);
                        if (Log.IsDebugEnabled)
                            Log.Debug("Lunartique 重打包 bundle 完成：{0}/{1}（原版 {2} 字节 → 改后 {3} 字节）",
                                group.Key.Account ?? "-", group.Key.Bundle ?? "-", originalBytes.Length, modifiedBytes.Length);
                        package.Resources.Add(new LunartiqueResource(
                            $"{group.Key.Account}/{group.Key.Bundle}/__data",
                            originalBytes,
                            modifiedBytes));
                        used++;
                    }
                    return used;
                }, cancellationToken).ConfigureAwait(false);
                if (usedBundles == 0)
                {
                    Log.Warn("Lunartique 未生成：缓存里找不到任何被编辑对象所属的原版 bundle（共 {0} 组），缓存目录 {1}",
                        objects.Entries.GroupBy(x => (x.Key.Account, x.Key.Bundle)).Count(), context.UnityCacheDirectory);
                    return new ModPackSlotResult(item.Descriptor, false, item.Directory, 0, [],
                        ["缓存里找不到任何被编辑对象所属的原版 bundle（游戏可能已更新），未生成 Lunartique 包"], []);
                }

                var output = Path.Combine(item.Directory, ExportLayout.Sanitize(plan.ModName) + ".zip");
                Directory.CreateDirectory(item.Directory);
                var writeWatch = System.Diagnostics.Stopwatch.StartNew();
                var inputBytes = package.Resources.Sum(x => (long)x.Uninstallation.Length + x.Installation.Length);
                await Task.Run(async () =>
                {
                    await using var stream = File.Create(output);
                    LunartiqueArchive.Write(package, stream, preserveUnknownFiles: true);
                }, cancellationToken).ConfigureAwait(false);
                Log.Debug("Lunartique 归档写出：{0}（{1} 个 bundle 资源 / 输入 {2} 字节 → 输出 {3} 字节，压缩+写盘耗时 {4} ms，总耗时 {5} ms）",
                    output, package.Resources.Count, inputBytes, new FileInfo(output).Length,
                    writeWatch.ElapsedMilliseconds, lunartiqueWatch.ElapsedMilliseconds);
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
            Log.Error(ex, "Lunartique 槽位导出失败：产物目录 {0}，缓存目录 {1}，已耗时 {2} ms",
                item.Directory, context.UnityCacheDirectory ?? "-", lunartiqueWatch.ElapsedMilliseconds);
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
            if (string.IsNullOrWhiteSpace(sourceName))
                Log.Warn("carra 槽位找不到来源文件名，产物名退回项目名「{0}」", plan.ModName);
            var fileName = ExportLayout.Sanitize(string.IsNullOrWhiteSpace(sourceName) ? plan.ModName : sourceName) + ".carra";
            var output = Path.Combine(item.Directory, fileName);
            var carraWatch = System.Diagnostics.Stopwatch.StartNew();
            Log.Debug("carra 槽位开始：产物 {0}，缓存目录 {1}", output, context.UnityCacheDirectory ?? "-");
            var result = await _unityExporter.ExportCarra2Async(
                project, projectRoot, output, context.UnityCacheDirectory, cancellationToken, progress);
            Log.Debug("carra 槽位写出：{0}（{1} 字节，资源 {2} 个，诊断 {3} 条，耗时 {4} ms）",
                output, File.Exists(output) ? new FileInfo(output).Length : 0,
                result.AssetStatuses.Count, result.Diagnostics.Count, carraWatch.ElapsedMilliseconds);
            return new ModPackSlotResult(item.Descriptor, true, item.Directory, 1, [output],
                result.Diagnostics.ToArray(), result.AssetStatuses);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "carra 槽位导出失败：产物目录 {0}，缓存目录 {1}",
                item.Directory, context.UnityCacheDirectory ?? "-");
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
        if (Log.IsDebugEnabled)
            Log.Debug("读取 bank：{0}（{1} 字节 → {2} 个 FSB）", path, bytes.Length, package.FsbData.Count);
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
        var applied = 0;
        try
        {
            foreach (var asset in edit.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!asset.Metadata.TryGetValue("replacementPath", out var replacement) || !File.Exists(replacement))
                {
                    Log.Warn("跳过音频替换 {0}：没有登记 replacementPath 或文件不存在（{1}）",
                        asset.LogicalPath ?? "-", replacement ?? "-");
                    continue;
                }
                var index = BankEdit.ParseFsbIndex(asset.LogicalPath!);
                if (index < 0 || index >= package.FsbData.Count)
                {
                    Log.Warn("跳过音频替换 {0}：FSB 序号 {1} 超出 bank 范围（bank 有 {2} 个 FSB）",
                        asset.LogicalPath ?? "-", index, package.FsbData.Count);
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath!, ExportAssetStatus.Skipped,
                        $"FSB 序号超出 bank 范围（bank 有 {package.FsbData.Count} 个 FSB）"));
                    continue;
                }
                var data = await File.ReadAllBytesAsync(replacement, cancellationToken);
                if (IsWave(data))
                {
                    if (!context.FmodCodecAvailable)
                    {
                        Log.Warn("跳过音频替换 {0}：替换内容是 WAV，但找不到 FMOD/FSBANK 编码器（FMOD 目录 {1}）",
                            asset.LogicalPath ?? "-", context.FmodDirectory ?? "-");
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath!, ExportAssetStatus.Skipped,
                            "WAV 需要 FMOD/FSBANK 编码器（未找到 FMOD DLL）"));
                        continue;
                    }
                    codec ??= new NativeFmodAudioCodec(context.FmodDirectory!);
                    var encodeWatch = System.Diagnostics.Stopwatch.StartNew();
                    var waveBytes = data.Length;
                    data = await codec.EncodeWaveToFsbAsync(data, cancellationToken);
                    Log.Debug("WAV 编码成 FSB5：{0}（输入 {1} 字节 → 输出 {2} 字节，耗时 {3} ms）",
                        replacement, waveBytes, data.Length, encodeWatch.ElapsedMilliseconds);
                }
                if (!IsFsb5(data))
                {
                    Log.Warn("跳过音频替换 {0}：替换内容既不是 WAV 也不是完整的 FSB5 数据（{1} 字节，头 {2}）",
                        asset.LogicalPath ?? "-", data.Length, Convert.ToHexString(data.AsSpan(0, Math.Min(4, data.Length))));
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath!, ExportAssetStatus.Skipped,
                        "替换内容既不是 WAV 也不是完整的 FSB5 数据"));
                    continue;
                }
                package.FsbData[index] = data;
                package.HasModifications = true;
                applied++;
                var details = Fsb5Parser.TryParse(data);
                if (details is null)
                    Log.Warn("音频替换 {0} 的 FSB5 解析失败，状态详情留空（{1} 字节）", asset.LogicalPath ?? "-", data.Length);
                statuses.Add(new ExportAssetStatus(asset.LogicalPath!, ExportAssetStatus.Applied,
                    details is null ? null : $"{details.SampleCount} 个样本 · {details.CodecName}"));
                if (Log.IsDebugEnabled)
                    Log.Debug("音频替换生效：{0} → FSB {1}（{2} 字节，来源 {3}）",
                        asset.LogicalPath ?? "-", index, data.Length, replacement);
            }
            Log.Debug("音频替换结束：{0} 个资源替换成功 / 共 {1} 个（状态 {2} 条）",
                applied, edit.Assets.Count, statuses.Count);
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
