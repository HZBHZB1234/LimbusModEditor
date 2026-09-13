using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using NLog;
using System.Text.Json;

namespace LimbusModEditor.Application.Build;

public sealed record UnityBundleBuildResult(string SourcePath, string OutputPath, int AppliedAssets);

/// <summary>Materializes project replacements back into Unity bundles. Each
/// source bundle is loaded once and repacked once; AssetsTools.NET performs all
/// serialized-file and compression details.</summary>
public sealed class UnityBundleBuildService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 重打包全部被编辑的 bundle。
    ///
    /// <para><b>为什么整段跑在后台线程</b>：本方法体全部是同步重活
    /// （AssetsTools.NET 解包 / 重打包 / 整份写盘 / 引用完整性校验）。
    /// 早先它声明 <c>async Task</c> 却一行 <c>await</c> 都没有、末尾
    /// <c>return Task.FromResult(...)</c>，于是调用方的 <c>await</c> 原地同步继续，
    /// 整段重活落在 <b>UI 线程</b>上 —— 导出模态窗口弹出后软件「未响应且不恢复」
    /// 的主因就是它。现在用 <c>Task.Run</c> 显式切到线程池，并在每个 bundle /
    /// 每个编辑步骤之间检查取消。</para>
    /// </summary>
    public Task<IReadOnlyList<UnityBundleBuildResult>> BuildAsync(
        ModProject project, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        // 入口在 UI 线程调用：这里只能同步记一条 Info（不做重活），重活全部在 Build 里（线程池）。
        Log.Info("重打包 bundle 任务提交：输出目录 {0}，资产 {1} 个", outputDirectory, project.Assets.Count);
        return Task.Run(() => Build(project, outputDirectory, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<UnityBundleBuildResult> Build(
        ModProject project, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var results = new List<UnityBundleBuildResult>();
        var candidates = project.Assets
            // 编辑标记先行（O(1)）：SourcePath 的 File.Exists 是系统调用，1.27M 条
            // 资产上单遍约 44 s（2026-09 实测），必须让绝大多数资产在它之前就被筛掉。
            // unityFieldEdits 也是候选（下面的字段编辑分支就在处理它，而导出计划的
            // 口径 IsEditedCacheAsset 早已包含它 —— 少了它会出现「计划说有、导出没有」）。
            .Where(x => (x.Metadata.ContainsKey("replacementPath")
                         || x.Metadata.ContainsKey("spriteMetadata")
                         || x.Metadata.ContainsKey("unityFieldEdits")) &&
                        x.SourcePath is not null && File.Exists(x.SourcePath) &&
                        x.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true" &&
                        x.UnityPathId is not null && !string.IsNullOrWhiteSpace(x.ContainerPath))
            .GroupBy(x => Path.GetFullPath(x.SourcePath!), StringComparer.OrdinalIgnoreCase)
            // 物化一次：GroupBy 是惰性的，`.Count()` + `foreach` 会把整条谓词链
            // 完整跑两遍（等于白烧一遍 1.27M 全表，实测每遍约 44 s）。
            .ToArray();
        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        var groupIndex = 0;
        var candidateCount = candidates.Length;
        Log.Debug("bundle 重打包开始：输出目录 {0}，待处理 bundle {1} 个（资产 {2} 个）",
            Path.GetFullPath(outputDirectory), candidateCount, project.Assets.Count);
        foreach (var group in candidates)
        {
            groupIndex++;
            if (cancellationToken.IsCancellationRequested)
                Log.Info("取消请求：bundle 重打包在 {0} 处中止", group.Key);
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var output = Path.Combine(Path.GetFullPath(outputDirectory), Path.GetFileName(source));
            if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"输出目录与源 Bundle 相同（{source}）。请选择不同的输出目录，避免覆盖原始资源。");
            // All steps write .stepN.tmp intermediates; the final repack is staged,
            // verified against the source, then moved onto the output. Failure or
            // cancellation leaves the source and the output untouched.
            var staging = output + ".lme-build.tmp";
            var intermediates = new List<string>();
            var current = source;
            var applied = 0;
            var bundleWatch = System.Diagnostics.Stopwatch.StartNew();
            var sourceBytes = new FileInfo(source).Length;
            if (Log.IsDebugEnabled)
                Log.Debug("bundle {0}/{1} 开始：源 {2}（{3} 字节，{4} 个待替换资产）",
                    groupIndex, candidateCount, source, sourceBytes, group.Count());
            try
            {
                using (var backend = new AssetsToolsBackend())
                {
                    // Grouping by container lets us preserve every other
                    // SerializedFile while chaining repacks for multiple files.
                    foreach (var container in group.GroupBy(x => x.ContainerPath!, StringComparer.Ordinal))
                    {
                        foreach (var fieldAsset in container.Where(x => x.UnityPathId.HasValue && x.Metadata.ContainsKey("unityFieldEdits") && !x.Metadata.ContainsKey("replacementPath")))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var editSet = UnityFieldEditSetCodec.Deserialize(fieldAsset.Metadata.TryGetValue("unityFieldEdits", out var fieldJson) ? fieldJson : null);
                            if (editSet is null)
                            {
                                Log.Warn("跳过字段编辑 {0}：unityFieldEdits 反序列化结果为空", fieldAsset.LogicalPath ?? "-");
                                continue;
                            }
                            var edits = UnityFieldEditSetCodec.ToPathValueMap(editSet);
                            if (edits.Count == 0)
                            {
                                Log.Warn("跳过字段编辑 {0}：字段路径映射为空", fieldAsset.LogicalPath ?? "-");
                                continue;
                            }
                            var problems = backend.ValidateBundleObjectFieldEdits(current, container.Key, fieldAsset.UnityPathId!.Value, edits)
                                .Where(x => !x.IsOk).ToArray();
                            if (problems.Length > 0)
                                throw new InvalidDataException(
                                    $"字段预校验失败 ({fieldAsset.LogicalPath}): " +
                                    string.Join("; ", problems.Select(x => $"{x.Path}: {x.Message}")));
                            var next = NextPath(output, intermediates.Count);
                            intermediates.Add(next);
                            if (Log.IsDebugEnabled)
                                Log.Debug("bundle {0} / 容器 {1} 正在写字段编辑：pathId {2}，{3} 个字段，输入 {4} → 输出 {5}",
                                    Path.GetFileName(source), container.Key, fieldAsset.UnityPathId!.Value, edits.Count, current, next);
                            backend.ReplaceBundleObjectFields(current, container.Key, fieldAsset.UnityPathId!.Value, edits, next);
                            current = next; applied++;
                        }
                        var replacements = container.Where(x => x.UnityTypeId != 28).ToArray();
                        var textures = container.Where(x => x.UnityTypeId == 28).ToArray();
                        if (replacements.Length > 0)
                        {
                            var raw = new Dictionary<long, byte[]>();
                            foreach (var asset in replacements)
                            {
                                var replacement = GetReplacementPath(asset);
                                if (replacement is null)
                                {
                                    Log.Warn("跳过资产替换 {0}：没有 replacementPath 或文件不存在", asset.LogicalPath ?? "-");
                                    continue;
                                }
                                raw[asset.UnityPathId!.Value] = File.ReadAllBytes(replacement);
                                if (Log.IsDebugEnabled)
                                    Log.Debug("读取替换文件：{0}（{1} 字节）→ pathId {2}",
                                        replacement, raw[asset.UnityPathId!.Value].Length, asset.UnityPathId!.Value);
                            }
                            if (raw.Count > 0)
                            {
                                var next = NextPath(output, intermediates.Count);
                                intermediates.Add(next);
                                if (Log.IsDebugEnabled)
                                    Log.Debug("bundle {0} / 容器 {1} 正在重打包对象：{2} 个 pathId，输入 {3} → 输出 {4}",
                                        Path.GetFileName(source), container.Key, raw.Count, current, next);
                                backend.ReplaceBundleSerializedAssets(current, container.Key, raw, next);
                                current = next;
                                applied += raw.Count;
                            }
                        }
                        foreach (var texture in textures)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var replacement = GetReplacementPath(texture);
                            if (replacement is null || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement)))
                            {
                                Log.Warn("跳过贴图替换 {0}：替换文件缺失或扩展名不受支持（{1}）",
                                    texture.LogicalPath ?? "-", replacement ?? "-");
                                continue;
                            }
                            var next = NextPath(output, intermediates.Count);
                            intermediates.Add(next);
                            var textureBytesData = File.ReadAllBytes(replacement);
                            if (Log.IsDebugEnabled)
                                Log.Debug("bundle {0} / 容器 {1} 正在写贴图：pathId {2}，PNG {3} 字节，输入 {4} → 输出 {5}",
                                    Path.GetFileName(source), container.Key, texture.UnityPathId!.Value, textureBytesData.Length, current, next);
                            backend.ReplaceBundleTextureFromPng(current, container.Key, texture.UnityPathId!.Value, textureBytesData, next);
                            current = next;
                            applied++;
                        }
                        foreach (var sprite in container.Where(x => x.UnityTypeId == UnityClassId.Sprite && x.Metadata.ContainsKey("spriteMetadata")))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!sprite.Metadata.TryGetValue("spriteMetadata", out var json))
                            {
                                Log.Warn("跳过 Sprite 元数据 {0}：spriteMetadata 键存在但取不到值", sprite.LogicalPath ?? "-");
                                continue;
                            }
                            UnitySpriteMetadata? metadata;
                            try { metadata = JsonSerializer.Deserialize<UnitySpriteMetadata>(json); }
                            catch (JsonException ex)
                            {
                                Log.Error(ex, "Sprite 元数据 JSON 解析失败，跳过该 Sprite：{0}（{1} 字符）",
                                    sprite.LogicalPath ?? "-", json.Length);
                                continue;
                            }
                            if (metadata is null)
                            {
                                Log.Warn("跳过 Sprite 元数据 {0}：JSON 反序列化为 null", sprite.LogicalPath ?? "-");
                                continue;
                            }
                            var next = NextPath(output, intermediates.Count);
                            intermediates.Add(next);
                            if (Log.IsDebugEnabled)
                                Log.Debug("bundle {0} / 容器 {1} 正在写 Sprite 元数据：pathId {2}，输入 {3} → 输出 {4}",
                                    Path.GetFileName(source), container.Key, sprite.UnityPathId!.Value, current, next);
                            backend.ReplaceBundleSpriteMetadata(current, container.Key, sprite.UnityPathId!.Value,
                                metadata.Rect, metadata.Pivot, metadata.Border, metadata.PixelsToUnits, next);
                            current = next;
                            applied++;
                        }
                    }
                }
                if (applied > 0)
                {
                    // 原子写：临时文件 → 校验 → 替换目标（staging 是 <输出>.lme-build.tmp）
                    File.Move(current, staging, true);
                    if (Log.IsDebugEnabled)
                        Log.Debug("原子写暂存：{0} → {1}（{2} 字节）",
                            current, staging, File.Exists(staging) ? new FileInfo(staging).Length : 0);
                    using (var validator = new AssetsToolsBackend())
                    {
                        _ = validator.InspectBundle(staging);
                        var verify = validator.VerifyBundleReferences(source, staging);
                        if (!verify.Ok)
                            throw new InvalidDataException(
                                $"重打包后引用完整性检查失败 ({Path.GetFileName(source)}): " +
                                string.Join("; ", verify.Changes.Where(c => c.IsRegression)
                                    .Select(c => $"{c.SerializedFile} Path {c.SourcePathId} {c.FieldPath}: {c.Before} → {c.After}")));
                    }
                    var replaced = File.Exists(output);
                    File.Move(staging, output, true);
                    results.Add(new(source, output, applied));
                    Log.Debug("bundle 重打包写出：{0}（输入 {1} 字节 → 输出 {2} 字节，{3} 个资产，{4} 步中间产物，耗时 {5} ms，替换已有文件 {6}）",
                        output, sourceBytes, File.Exists(output) ? new FileInfo(output).Length : 0,
                        applied, intermediates.Count, bundleWatch.ElapsedMilliseconds, replaced);
                }
                else
                {
                    Log.Warn("bundle {0} 没有任何编辑生效，未写出产物（{1} 个待替换资产）",
                        source, group.Count());
                }
            }
            finally
            {
                var pendingDeletes = intermediates.Count(temporary => File.Exists(temporary));
                foreach (var temporary in intermediates)
                    if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(staging)) File.Delete(staging);
                if (Log.IsDebugEnabled)
                    Log.Debug("bundle {0} 清理中间产物：删除 {1} 个 .stepN.tmp / 暂存文件 {2}",
                        source, pendingDeletes, staging);
            }
        }
        Log.Debug("bundle 重打包结束：写出 {0} 个 bundle，总耗时 {1} ms", results.Count, buildWatch.ElapsedMilliseconds);
        return results;
    }

    private static string? GetReplacementPath(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path) ? path : null;

    private static string NextPath(string output, int index)
        => output + $".step{index}.tmp";
}
