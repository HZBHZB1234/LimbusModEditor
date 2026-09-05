using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public class ProjectBuildServiceTests
{
    [Fact]
    public async Task BuildsOverlayWithSafeAtomicCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var project = new ModProject();
        project.Edits.Add(new EditOperation { Kind = EditOperationKind.ReplaceFile, TargetPath = "assets/a.bin", SourcePath = source });
        var output = Path.Combine(root, "overlay");
        try
        {
            var result = await new ProjectBuildService().BuildOverlayAsync(project, root, output);
            Assert.Equal(1, result.AppliedEdits);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(output, "assets/a.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SkipsUnityObjectEditsBecauseBundleBuilderOwnsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-build-unity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "replacement.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var asset = new LimbusModEditor.Domain.Assets.AssetRecord
        {
            LogicalPath = "bundle/main.assets/123.28",
            SourcePath = Path.Combine(root, "bundle").ToString(),
            UnityPathId = 123,
            UnityTypeId = 28,
            Metadata = { ["unityBundle"] = "true" }
        };
        var project = new ModProject();
        project.Assets.Add(asset);
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            SourcePath = source
        });
        var output = Path.Combine(root, "overlay");
        try
        {
            var result = await new ProjectBuildService().BuildOverlayAsync(project, root, output);
            Assert.Equal(0, result.AppliedEdits);
            Assert.False(File.Exists(Path.Combine(output, "bundle", "main.assets", "123.28")));
        }
        finally { Directory.Delete(root, true); }
    }
}
