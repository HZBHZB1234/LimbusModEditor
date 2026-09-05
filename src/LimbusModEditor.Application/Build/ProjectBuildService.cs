using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Application.Build;

public sealed record BuildResult(string OutputPath, int AppliedEdits, IReadOnlyList<string> Diagnostics);

/// <summary>Builds the project workspace into a game-compatible overlay and
/// provides the common dispatch point for package exporters.</summary>
public sealed class ProjectBuildService
{
    public async Task<BuildResult> BuildOverlayAsync(ModProject project, string projectDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var applied = 0;
        var diagnostics = new List<string>();
        // Unity object edits are materialized by UnityBundleBuildService. They
        // must not be copied as ordinary files (their logical path is an
        // object address such as bundle/serialized/pathId.typeId).
        var unityAssetIds = project.Assets
            .Where(x => x.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true")
            .Select(x => x.AssetId)
            .ToHashSet();
        foreach (var edit in project.Edits.Where(x => x.SourcePath is not null && File.Exists(x.SourcePath) &&
                     (!x.AssetId.HasValue || !unityAssetIds.Contains(x.AssetId.Value))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafeCombine(output, edit.TargetPath);
            await AtomicOutput.CopyAsync(edit.SourcePath!, target, cancellationToken);
            applied++;
        }
        diagnostics.Add($"已应用 {applied} 个文件级编辑操作。");
        return new(output, applied, diagnostics);
    }

    public async Task ExportAsync(IModFormatHandler handler, ModPackage package, string outputPath, ExportContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(package);
        await AtomicOutput.WriteAsync(outputPath, async (stream, token) =>
        {
            await using var output = stream;
            await handler.ExportAsync(package, output, context with { CancellationToken = token });
        }, cancellationToken);
    }

    public static void ApplyCarraReplacements(CarraPackage package, ModProject project)
    {
        foreach (var asset in project.Assets)
        {
            if (!asset.Metadata.TryGetValue("replacementPath", out var source) || !File.Exists(source)) continue;
            var entry = package.Find(asset.LogicalPath);
            if (entry is null) continue;
            entry.ReplaceData(File.ReadAllBytes(source));
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("构建目标路径超出输出目录。");
        return result;
    }
}
