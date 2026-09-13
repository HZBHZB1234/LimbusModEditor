using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using NLog;
using System.Text.Json;

namespace LimbusModEditor.Application.Build;

public sealed record UnitySerializedFileBuildResult(string SourcePath, string OutputPath, int AppliedAssets);

/// <summary>Materializes edits to standalone Unity SerializedFiles (for
/// example .assets files used by a cache or a Lunartique installation).</summary>
public sealed class UnitySerializedFileBuildService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public Task<IReadOnlyList<UnitySerializedFileBuildResult>> BuildAsync(
        ModProject project, string outputDirectory, CancellationToken cancellationToken = default)
    {
        using var scope = Log.Scope("构建 Unity SerializedFile");
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Log.Info("构建 Unity SerializedFile 开始：输出目录 {0}，项目 {1}，资产 {2} 条",
            Path.GetFullPath(outputDirectory), project.Name ?? "-", project.Assets.Count);
        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var results = new List<UnitySerializedFileBuildResult>();
        var candidates = project.Assets
            // 与 UnityBundleBuildService 同一口径：编辑标记（O(1)）先行，File.Exists 后置；
            // unityFieldEdits 也是候选（循环体里的字段编辑分支就在处理它）。
            .Where(x => (x.Metadata.ContainsKey("replacementPath")
                         || x.Metadata.ContainsKey("spriteMetadata")
                         || x.Metadata.ContainsKey("unityFieldEdits")) &&
                        x.SourcePath is not null && File.Exists(x.SourcePath) &&
                        x.Metadata.TryGetValue("unitySerializedFile", out var marker) && marker == "true" &&
                        x.UnityPathId.HasValue && x.ContainerPath is not null)
            .GroupBy(x => Path.GetFullPath(x.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groupCount = 0;
        foreach (var group in candidates)
        {
            groupCount++;
            Log.Debug("处理 SerializedFile 源文件：{0}，{1} 个资源", group.Key, group.Count());
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var output = Path.Combine(Path.GetFullPath(outputDirectory), Path.GetFileName(source));
            if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"输出目录与源文件相同（{source}）。请选择不同的输出目录，避免覆盖原始资源。");
            // Every step writes to a .stepN.tmp intermediate; the final result is
            // staged, reference-verified against the source and only then moved
            // onto the output. A failed verify/cancel leaves the source untouched.
            var staging = output + ".lme-build.tmp";
            var intermediates = new List<string>();
            var current = source;
            var applied = 0;
            try
            {
                using var backend = new AssetsToolsBackend();
                foreach (var fieldAsset in group.Where(x => x.UnityPathId.HasValue && x.Metadata.ContainsKey("unityFieldEdits") && !x.Metadata.ContainsKey("replacementPath")))
                {
                    if (Log.IsTraceEnabled)
                        Log.Trace("应用字段编辑：{0}（PathId {1}）", fieldAsset.LogicalPath, fieldAsset.UnityPathId!.Value);
                    var editSet = UnityFieldEditSetCodec.Deserialize(fieldAsset.Metadata.TryGetValue("unityFieldEdits", out var fieldJson) ? fieldJson : null);
                    if (editSet is null)
                    {
                        Log.Debug("字段编辑集反序列化为空，跳过：{0}（PathId {1}）", fieldAsset.LogicalPath, fieldAsset.UnityPathId!.Value);
                        continue;
                    }
                    var edits = UnityFieldEditSetCodec.ToPathValueMap(editSet);
                    if (edits.Count == 0)
                    {
                        Log.Debug("字段编辑集为空，跳过：{0}（PathId {1}）", fieldAsset.LogicalPath, fieldAsset.UnityPathId!.Value);
                        continue;
                    }
                    var problems = backend.ValidateObjectFieldEdits(current, fieldAsset.UnityPathId!.Value, edits)
                        .Where(x => !x.IsOk).ToArray();
                    if (problems.Length > 0)
                        throw new InvalidDataException(
                            $"字段预校验失败 ({fieldAsset.LogicalPath}): " +
                            string.Join("; ", problems.Select(x => $"{x.Path}: {x.Message}")));
                    var next = NextPath(output, intermediates.Count);
                    intermediates.Add(next);
                    backend.ReplaceObjectFields(current, fieldAsset.UnityPathId!.Value, edits, next);
                    current = next; applied++;
                }
                var replacements = new Dictionary<long, byte[]>();
                foreach (var asset in group.Where(x => x.UnityTypeId != 28))
                {
                    if (GetReplacementPath(asset) is { } path) replacements[asset.UnityPathId!.Value] = File.ReadAllBytes(path);
                }
                if (replacements.Count > 0)
                {
                    Log.Debug("替换序列化对象：{0} 个（源 {1}）", replacements.Count, source);
                    var next = NextPath(output, intermediates.Count);
                    intermediates.Add(next);
                    backend.ReplaceSerializedAssets(current, replacements, next);
                    current = next; applied += replacements.Count;
                }
                foreach (var texture in group.Where(x => x.UnityTypeId == 28))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var replacement = GetReplacementPath(texture);
                    if (replacement is null || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement))) continue;
                    Log.Debug("替换纹理（TypeId 28）：PathId {0}，贴图 {1}", texture.UnityPathId!.Value, replacement);
                    var next = NextPath(output, intermediates.Count);
                    intermediates.Add(next);
                    backend.ReplaceTextureFromPng(current, texture.UnityPathId!.Value, File.ReadAllBytes(replacement), next);
                    current = next; applied++;
                }
                foreach (var sprite in group.Where(x => x.UnityTypeId == UnityClassId.Sprite && x.Metadata.ContainsKey("spriteMetadata")))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!sprite.Metadata.TryGetValue("spriteMetadata", out var json)) continue;
                    UnitySpriteMetadata? metadata;
                    try { metadata = JsonSerializer.Deserialize<UnitySpriteMetadata>(json); }
                    catch (JsonException) { continue; }
                    if (metadata is null) continue;
                    var next = NextPath(output, intermediates.Count);
                    intermediates.Add(next);
                    backend.ReplaceSpriteMetadata(current, sprite.UnityPathId!.Value, metadata.Rect,
                        metadata.Pivot, metadata.Border, metadata.PixelsToUnits, next);
                    current = next; applied++;
                }
                if (applied > 0)
                {
                    File.Move(current, staging, true);
                    using (var validator = new AssetsToolsBackend())
                    {
                        _ = validator.ReadSerializedObjects(staging);
                        var verify = validator.VerifySerializedReferences(source, staging);
                        if (!verify.Ok)
                            throw new InvalidDataException(
                                $"重写后引用完整性检查失败 ({Path.GetFileName(source)}): " +
                                string.Join("; ", verify.Changes.Where(c => c.IsRegression)
                                    .Select(c => $"Path {c.SourcePathId} {c.FieldPath}: {c.Before} → {c.After}")));
                    }
                    File.Move(staging, output, true);
                    results.Add(new(source, output, applied));
                }
            }
            finally
            {
                foreach (var temporary in intermediates)
                    if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
        return Task.FromResult<IReadOnlyList<UnitySerializedFileBuildResult>>(results);
    }

    private static string? GetReplacementPath(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path) ? path : null;

    private static string NextPath(string output, int index)
        => output + $".step{index}.tmp";
}
