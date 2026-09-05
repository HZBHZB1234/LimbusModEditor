using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using System.Text.Json;

namespace LimbusModEditor.Application.Build;

public sealed record UnityBundleBuildResult(string SourcePath, string OutputPath, int AppliedAssets);

/// <summary>Materializes project replacements back into Unity bundles. Each
/// source bundle is loaded once and repacked once; AssetsTools.NET performs all
/// serialized-file and compression details.</summary>
public sealed class UnityBundleBuildService
{
    public Task<IReadOnlyList<UnityBundleBuildResult>> BuildAsync(
        ModProject project, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
        var results = new List<UnityBundleBuildResult>();
        var candidates = project.Assets
            .Where(x => x.SourcePath is not null && File.Exists(x.SourcePath) &&
                        x.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true" &&
                        x.UnityPathId is not null && !string.IsNullOrWhiteSpace(x.ContainerPath) &&
                        (x.Metadata.ContainsKey("replacementPath") || x.Metadata.ContainsKey("spriteMetadata")))
            .GroupBy(x => Path.GetFullPath(x.SourcePath!), StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var output = Path.Combine(Path.GetFullPath(outputDirectory), Path.GetFileName(source));
            var current = source;
            var applied = 0;
            var intermediates = new List<string>();
            using (var backend = new AssetsToolsBackend())
            {
                // Grouping by container lets us preserve every other
                // SerializedFile while chaining repacks for multiple files.
                foreach (var container in group.GroupBy(x => x.ContainerPath!, StringComparer.Ordinal))
                {
                    foreach (var fieldAsset in container.Where(x => x.UnityPathId.HasValue && x.Metadata.ContainsKey("unityFieldEdits") && !x.Metadata.ContainsKey("replacementPath")))
                    {
                        if (!fieldAsset.Metadata.TryGetValue("unityFieldEdits", out var fieldJson)) continue;
                        Dictionary<string, string>? edits;
                        try { edits = JsonSerializer.Deserialize<Dictionary<string, string>>(fieldJson); }
                        catch (JsonException) { continue; }
                        if (edits is null || edits.Count == 0) continue;
                        var next = NextPath(output, intermediates.Count, intermediates);
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
                            if (replacement is null) continue;
                            raw[asset.UnityPathId!.Value] = File.ReadAllBytes(replacement);
                        }
                        if (raw.Count > 0)
                        {
                            var next = NextPath(output, intermediates.Count, intermediates);
                            backend.ReplaceBundleSerializedAssets(current, container.Key, raw, next);
                            current = next;
                            applied += raw.Count;
                        }
                    }
                    foreach (var texture in textures)
                    {
                        var replacement = GetReplacementPath(texture);
                        if (replacement is null || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement))) continue;
                        var next = NextPath(output, intermediates.Count, intermediates);
                        backend.ReplaceBundleTextureFromPng(current, container.Key, texture.UnityPathId!.Value, File.ReadAllBytes(replacement), next);
                        current = next;
                        applied++;
                    }
                    foreach (var sprite in container.Where(x => x.UnityTypeId == 115 && x.Metadata.ContainsKey("spriteMetadata")))
                    {
                        if (!sprite.Metadata.TryGetValue("spriteMetadata", out var json)) continue;
                        UnitySpriteMetadata? metadata;
                        try { metadata = JsonSerializer.Deserialize<UnitySpriteMetadata>(json); }
                        catch (JsonException) { continue; }
                        if (metadata is null) continue;
                        var next = NextPath(output, intermediates.Count, intermediates);
                        backend.ReplaceBundleSpriteMetadata(current, container.Key, sprite.UnityPathId!.Value,
                            metadata.Rect, metadata.Pivot, metadata.Border, metadata.PixelsToUnits, next);
                        current = next;
                        applied++;
                    }
                }
            }
            if (applied > 0)
            {
                if (!string.Equals(current, output, StringComparison.OrdinalIgnoreCase)) File.Move(current, output, true);
                using (var validator = new AssetsToolsBackend())
                    _ = validator.InspectBundle(output);
                results.Add(new(source, output, applied));
            }
            foreach (var temporary in intermediates.Where(x => !string.Equals(x, output, StringComparison.OrdinalIgnoreCase)))
                if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Task.FromResult<IReadOnlyList<UnityBundleBuildResult>>(results);
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
