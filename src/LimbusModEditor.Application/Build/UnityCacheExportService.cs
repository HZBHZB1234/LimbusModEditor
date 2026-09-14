using System.IO;
using System.Text;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Unity;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Application.Build;

/// <summary>
/// 傻瓜化一键导出：把项目里所有「扫描自 Unity 缓存并已编辑」的对象按真实
/// 加载器的 Carra2 布局（键 = 缓存外层键/内层键/pathId.类型表索引，逐条目 XZ）
/// 打包成可直接放进模组目录的 .carra2。内部先用 UnityBundleBuildService 把
/// 每个被编辑的 bundle 重打包（含引用完整性验证），再从重打包结果读取修改后
/// 的对象原始字节（口径与 T1 真实写回验证一致）。
/// </summary>
public sealed class UnityCacheExportService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly UnityBundleBuildService _bundleBuilder = new();

    /// <param name="projectRoot">项目根目录（.lmeproj 所在目录），重打包
    /// 中间产物写入 &lt;projectRoot&gt;/builds/unity-bundles。</param>
    /// <param name="unityCacheDirectory">可选：用于缓存对齐诊断。</param>
    /// <param name="progress">可选：阶段与逐对象进度消息（供 UI 进度窗口）。</param>
    public async Task<ModExportResult> ExportCarra2Async(
        ModProject project, string projectRoot, string outputPath,
        string? unityCacheDirectory = null, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = Log.Scope("导出 carra2 对象包");
        Log.Info("carra2 导出开始：产物 {0}，项目根 {1}，缓存目录 {2}",
            outputPath, projectRoot, unityCacheDirectory ?? "-");

        // 逐资源 / 逐对象的上报量在真实规模下可达十万级，而 UI 侧的 Progress<string>
        // 每条都 Post 到 Dispatcher 队列；未节流时导出会把 UI 队列淹掉（详见
        // ThrottledProgress 的说明）。首条与末条仍会到达。
        var throttled = new ThrottledProgress(progress);

        var edited = project.Assets.Where(IsEditedCacheAsset).ToList();
        if (edited.Count == 0)
            throw new InvalidDataException(
                "没有找到任何已编辑的缓存对象。请先在资源列表中替换图片、编辑 Sprite 元数据或 Unity 字段。");

        var outputFullPath = Path.GetFullPath(outputPath);
        // Carra 与 Lunartique 两个槽位都要一份「改后对象」：同一目标路径（同一次导出内）
        // 直接复用上一次的结果，不重跑整条流水线（重跑 = 重打包 + 逐对象 XZ ×2）。
        if (TryTakeInFlight(outputFullPath, out var reused))
        {
            Log.Info("复用本次导出已生成的对象包，跳过重打包与逐对象压缩：{0}（{1} 个对象）",
                outputFullPath, reused.AssetStatuses.Count);
            throttled.Report($"复用本次导出已生成的对象包：{outputFullPath}");
            return reused;
        }

        try
        {
            // 1) 重打包所有被编辑的 bundle（BuildAsync 自带 staging + 引用完整性验证，
            //    内部已切到线程池并检查取消）。
            var buildsDirectory = Path.Combine(Path.GetFullPath(projectRoot), "builds", "unity-bundles");
            throttled.Report("正在重打包被编辑的 bundle（实体化 → 验证 → 写出）…");
            var repackWatch = System.Diagnostics.Stopwatch.StartNew();
            var builds = await _bundleBuilder.BuildAsync(project, buildsDirectory, cancellationToken).ConfigureAwait(false);
            Log.Debug("bundle 重打包阶段结束：{0} 个 bundle 写出到 {1}，耗时 {2} ms",
                builds.Count, buildsDirectory, repackWatch.ElapsedMilliseconds);
            throttled.Report($"已重打包 {builds.Count} 个 bundle，开始逐对象读取修改后的数据…");
            var bySource = builds.ToDictionary(
                x => Path.GetFullPath(x.SourcePath), x => x.OutputPath, StringComparer.OrdinalIgnoreCase);

            // 2) 后台按 bundle 读取：组内共享解包与 SerializedFile，组结束立即释放。
            var readWatch = System.Diagnostics.Stopwatch.StartNew();
            var (package, statuses) = await Task.Run(() =>
            {
                var built = new CarraPackage();
                var results = new List<ExportAssetStatus>();
                var objectIndex = 0;
                var appliedCount = 0;
                foreach (var group in edited.GroupBy(x => x.SourcePath is null ? string.Empty : Path.GetFullPath(x.SourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    bySource.TryGetValue(group.Key, out var repacked);
                    using var reader = repacked is null ? null : new AssetsToolsBackend.BundleObjectReader(repacked);
                    foreach (var asset in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        objectIndex++;
                        throttled.Report($"[{objectIndex}/{edited.Count}] {asset.LogicalPath}");
                        if (reader is null)
                        {
                            Log.Warn("跳过缓存对象 {0}：所属 bundle 没有生成重打包结果", asset.LogicalPath);
                            results.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "所属 bundle 没有生成重打包结果。"));
                            continue;
                        }
                        try
                        {
                            var raw = reader.Read(asset.ContainerPath!, asset.UnityPathId!.Value);
                            if (raw.Data.Length == 0)
                            {
                                results.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "重打包后对象数据为空。"));
                                continue;
                            }
                            var key = new CarraObjectKey(asset.Metadata["cacheOuter"], asset.Metadata["cacheInner"],
                                asset.UnityPathId.Value, raw.TypeTableIndex);
                            built.Entries.Add(CarraEntry.CreateNew(key, raw.Data));
                            results.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                            appliedCount++;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "读取重打包后的对象失败：{0}（bundle {1}，容器 {2}，pathId {3}）",
                                asset.LogicalPath, repacked ?? "-", asset.ContainerPath ?? "-", asset.UnityPathId!.Value);
                            results.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, $"读取修改后对象失败：{ex.Message}"));
                        }
                    }
                    Log.Debug("bundle 对象读取完成：{0} · 进度 {1}/{2} · 累计生效 {3} · 耗时 {4} ms",
                        group.Key, objectIndex, edited.Count, appliedCount, readWatch.ElapsedMilliseconds);
                }
                return (built, results);
            }, cancellationToken).ConfigureAwait(false);
            Log.Debug("逐对象读回阶段结束：{0}/{1} 个对象进入包，耗时 {2} ms",
                package.Entries.Count, edited.Count, readWatch.ElapsedMilliseconds);
            if (package.Entries.Count == 0)
                throw new InvalidDataException(
                    "没有对象成功写入 Carra2 包；请查看逐资源状态了解原因。");
            package.UnknownFiles.Add(("carra.json",
                Encoding.UTF8.GetBytes($"{{\"format\": \"carra2\", \"objects\": {package.Entries.Count}}}")));

            var handler = new CarraFormatHandler();
            var validation = await handler.ValidateAsync(
                new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package }, cancellationToken).ConfigureAwait(false);
            if (validation.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException(string.Join("; ", validation.Diagnostics.Select(x => x.Message)));

            // 3) 缓存对齐诊断（与 ModExportService 同口径）：外层键缺失 = 游戏更新换键。
            var diagnostics = CheckCacheAlignment(package, unityCacheDirectory).ToList();
            foreach (var warning in diagnostics)
                Log.Warn("缓存对齐诊断：{0}", warning);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
            throttled.Report($"正在压缩写出 {package.Entries.Count} 个对象（逐条目 XZ）…");
            // 逐条目 XZ 压缩 + 归档写盘（原子写）：导出耗时的大头，必须记输入/输出字节与耗时。
            var inputBytes = package.Entries.Sum(x => (long)(x.ModifiedData?.Length ?? x.CompressedData.Length));
            var xzWatch = System.Diagnostics.Stopwatch.StartNew();
            await AtomicOutput.WriteAsync(outputFullPath, async (stream, token) =>
            {
                await using var output = stream;
                await handler.ExportAsync(
                    new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package },
                    output, new ExportContext(ModFormatKind.Carra2, Codec: new JovelerXzCodec(), CancellationToken: token));
            }, cancellationToken).ConfigureAwait(false);
            Log.Debug("carra2 归档写出完成：{0}（输入 {1} 字节 / {2} 个对象 → 输出 {3} 字节，XZ 压缩+写盘耗时 {4} ms）",
                outputFullPath, inputBytes, package.Entries.Count,
                File.Exists(outputFullPath) ? new FileInfo(outputFullPath).Length : 0, xzWatch.ElapsedMilliseconds);

            var applied = statuses.Count(x => x.Status == ExportAssetStatus.Applied);
            var result = new ModExportResult(ModFormatKind.Carra2, outputFullPath, applied, diagnostics, statuses);
            RememberInFlight(outputFullPath, result);
            Log.Info("carra2 导出完成：{0} 个对象生效 / 共 {1} 个候选，诊断 {2} 条，节流丢弃进度上报 {3} 条",
                applied, statuses.Count, diagnostics.Count, throttled.SuppressedCount);
            return result;
        }
        finally
        {
            throttled.Flush();
        }
    }

    // ── 同一次导出内的对象包复用（carra + lunartique 两个槽位）──────────
    //
    // 两个槽位共用「改后 bundle 的对象字节」这一份中间产物：Lunartique 槽位内部
    // 就是调 ExportCarra2Async 拿 objects.carra。早先它会整条重跑一遍
    // （重打包 + 逐对象 XZ 各两份），这既是双倍卡顿也是双倍内存。
    // 复用键 = 目标路径 + 文件签名，避免跨次导出复用过期结果。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, ModExportResult Result)> InFlightPackages =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool TryTakeInFlight(string outputPath, out ModExportResult result)
    {
        result = null!;
        if (!InFlightPackages.TryGetValue(outputPath, out var cached)) return false;
        try
        {
            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length != cached.Length || info.LastWriteTimeUtc.Ticks != cached.Ticks)
            {
                Log.Debug("对象包复用失效（文件签名不匹配），重新生成：{0}", outputPath);
                InFlightPackages.TryRemove(outputPath, out _);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "读取对象包签名失败，本次不复用（重新生成）：{0}", outputPath);
            InFlightPackages.TryRemove(outputPath, out _);
            return false;
        }
        result = cached.Result;
        return true;
    }

    private static void RememberInFlight(string outputPath, ModExportResult result)
    {
        try
        {
            var info = new FileInfo(outputPath);
            if (!info.Exists) return;
            InFlightPackages[outputPath] = (info.Length, info.LastWriteTimeUtc.Ticks, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 复用只是省时间：拿不到签名就不缓存，绝不影响正确性。
            Log.Error(ex, "记录对象包签名失败，本次导出内不再复用：{0}", outputPath);
        }
    }

    /// <summary>
    /// 该资源是否属于「Unity 缓存对象级导出」的候选（plan-16 S4 起公开：导出计划
    /// <c>ModExportPlanService</c> 与导出执行器必须用<b>同一口径</b>判定，
    /// 否则会出现「计划说有、导出说没有」这种自相矛盾的报告）。
    /// </summary>
    public static bool IsEditedCacheAsset(AssetRecord asset)
    {
        // 顺序就是性能（2026-09 实测，1.27M 资产项目）：启动回灌后项目里有 127 万条
        // 引用资产，而真正带编辑的只有个位数。File.Exists 是系统调用（实测
        // 1,275,623 次 ≈ 44 s），所以必须先用 O(1) 的编辑标记把绝大多数资产筛掉，
        // 再去问「源文件还在不在」。原来 File.Exists 排在最前面，导致
        // 「生成导出计划」46 s、每次 carra2 导出前 44 s、重打包入口两遍 88 s。
        if (!asset.Metadata.ContainsKey("replacementPath")
            && !asset.Metadata.ContainsKey("spriteMetadata")
            && !asset.Metadata.ContainsKey("unityFieldEdits")) return false;
        if (asset.UnityPathId is null || string.IsNullOrWhiteSpace(asset.ContainerPath)) return false;
        if (asset.SourcePath is null || !File.Exists(asset.SourcePath)) return false;
        if (!asset.Metadata.TryGetValue("unityBundle", out var isBundle) || isBundle != "true") return false;
        if (!asset.Metadata.ContainsKey("cacheOuter") || !asset.Metadata.ContainsKey("cacheInner")) return false;
        return true;
    }

    private static IEnumerable<string> CheckCacheAlignment(CarraPackage package, string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory)) yield break;
        foreach (var outer in package.Entries.Select(x => x.Key.Account).Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(Path.Combine(cacheDirectory, outer)))
                yield return $"缓存对齐：外层键 {outer} 不在当前缓存目录中（游戏可能已更新），加载器将无法匹配这些对象";
        }
    }
}
