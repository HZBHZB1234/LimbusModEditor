using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using System.Text.Json;

namespace LimbusModEditor.Application.Build;

public sealed record UnitySerializedFileBuildResult(string SourcePath, string OutputPath, int AppliedAssets);

/// <summary>Materializes edits to standalone Unity SerializedFiles (for
/// example .assets files used by a cache or a Lunartique installation).</summary>
public sealed class UnitySerializedFileBuildService
{
    public Task<IReadOnlyList<UnitySerializedFileBuildResult>> BuildAsync(
        ModProject project, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var results = new List<UnitySerializedFileBuildResult>();
        var candidates = project.Assets
            .Where(x => x.SourcePath is not null && File.Exists(x.SourcePath) &&
                        x.Metadata.TryGetValue("unitySerializedFile", out var marker) && marker == "true" &&
                        x.UnityPathId.HasValue && x.ContainerPath is not null &&
                        (x.Metadata.ContainsKey("replacementPath") || x.Metadata.ContainsKey("spriteMetadata")))
            .GroupBy(x => Path.GetFullPath(x.SourcePath!), StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var output = Path.Combine(Path.GetFullPath(outputDirectory), Path.GetFileName(source));
            var current = source; var applied = 0; var intermediates = new List<string>();
            using var backend = new AssetsToolsBackend();
            foreach (var fieldAsset in group.Where(x => x.UnityPathId.HasValue && x.Metadata.ContainsKey("unityFieldEdits") && !x.Metadata.ContainsKey("replacementPath")))
            {
                var editSet = UnityFieldEditSetCodec.Deserialize(fieldAsset.Metadata.TryGetValue("unityFieldEdits", out var fieldJson) ? fieldJson : null);
                if (editSet is null) continue;
                var edits = UnityFieldEditSetCodec.ToPathValueMap(editSet);
                if (edits.Count == 0) continue;
                var problems = backend.ValidateObjectFieldEdits(current, fieldAsset.UnityPathId!.Value, edits)
                    .Where(x => !x.IsOk).ToArray();
                if (problems.Length > 0)
                    throw new InvalidDataException(
                        $"字段预校验失败 ({fieldAsset.LogicalPath}): " +
                        string.Join("; ", problems.Select(x => $"{x.Path}: {x.Message}")));
                var next = NextPath(output, intermediates.Count, intermediates);
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
                var next = NextPath(output, intermediates.Count, intermediates);
                backend.ReplaceSerializedAssets(current, replacements, next);
                current = next; applied += replacements.Count;
            }
            foreach (var texture in group.Where(x => x.UnityTypeId == 28))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replacement = GetReplacementPath(texture);
                if (replacement is null || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement))) continue;
                var next = NextPath(output, intermediates.Count, intermediates);
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
                var next = NextPath(output, intermediates.Count, intermediates);
                backend.ReplaceSpriteMetadata(current, sprite.UnityPathId!.Value, metadata.Rect,
                    metadata.Pivot, metadata.Border, metadata.PixelsToUnits, next);
                current = next; applied++;
            }
            if (applied > 0)
            {
                if (!string.Equals(current, output, StringComparison.OrdinalIgnoreCase)) File.Move(current, output, true);
                using (var validator = new AssetsToolsBackend())
                    _ = validator.ReadSerializedObjects(output);
                results.Add(new(source, output, applied));
            }
            foreach (var temporary in intermediates)
                if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Task.FromResult<IReadOnlyList<UnitySerializedFileBuildResult>>(results);
    }

    private static string? GetReplacementPath(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path) ? path : null;

    private static string NextPath(string output, int index, ICollection<string> intermediates)
    {
        var path = index == 0 ? output : output + $".step{index}.tmp";
        if (index > 0) intermediates.Add(path);
        return path;
    }
}
