using System.IO.Compression;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Domain.Tests;

public sealed class SourceImportTests
{
    [Fact]
    public async Task PackageImportMaterializesSourceAndTracksItOnProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var packagePath = Path.Combine(root, "demo.carra");
        try
        {
            await using (var stream = File.Create(packagePath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry("acct/bundle/1.28").Open();
                entry.Write([1, 2]);
            }
            var project = await new ProjectService().CreateAsync(Path.Combine(root, "project"), "Demo");
            var result = await new ModImportService(BuiltInFormatRegistry.Create()).ImportIntoProjectAsync(packagePath, project);
            Assert.Equal(ModFormatKind.Carra2, result.Format);
            var source = Assert.Single(project.Sources);
            Assert.True(File.Exists(source.Path));
            Assert.Equal(source.Path, project.Assets.Single().SourcePath);
            Assert.Equal(source.Path, project.Assets.Single().Metadata["sourcePackagePath"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirectoryImportCopiesFilesAndInfersAssetTypes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-dir-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "input");
        Directory.CreateDirectory(Path.Combine(source, "textures"));
        await File.WriteAllBytesAsync(Path.Combine(source, "textures", "hero.png"), [1, 2, 3]);
        try
        {
            var project = new ModProject { SourceDirectory = Path.Combine(root, "project-sources") };
            var result = await new ModImportService(BuiltInFormatRegistry.Create()).ImportDirectoryIntoProjectAsync(source, project);
            Assert.Equal(ModFormatKind.Directory, result.Format);
            var asset = Assert.Single(project.Assets);
            Assert.Equal("textures/hero.png", asset.LogicalPath);
            Assert.Equal(LimbusModEditor.Domain.Assets.AssetType.Texture, asset.Type);
            Assert.True(File.Exists(asset.SourcePath));
            Assert.Single(project.Sources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirectoryProjectCanExportCarraWithoutSelectingSourceAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-dir-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "objects");
        Directory.CreateDirectory(Path.Combine(input, "acct", "bundle"));
        await File.WriteAllBytesAsync(Path.Combine(input, "acct", "bundle", "42.28"), [4, 5, 6]);
        try
        {
            var project = new ModProject { SourceDirectory = Path.Combine(root, "sources") };
            await new ModImportService(BuiltInFormatRegistry.Create()).ImportDirectoryIntoProjectAsync(input, project);
            var output = Path.Combine(root, "out.carra2");
            var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(null, project, output);
            Assert.Equal(ModFormatKind.Carra2, result.Format);
            using var exported = File.OpenRead(output);
            var package = CarraArchive.Read(exported);
            Assert.Equal(new byte[] { 4, 5, 6 }, package.Entries.Single().ReadData());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StructuredUnityAssetsAreNotExportedAsWholeBundleBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-structured-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bundle = Path.Combine(root, "source.bundle");
        await File.WriteAllBytesAsync(bundle, [1, 2, 3]);
        try
        {
            var project = new ModProject { SourceDirectory = Path.Combine(root, "sources") };
            project.Sources.Add(new() { DisplayName = "source.bundle", Path = bundle, Format = ModFormatKind.Directory });
            project.Assets.Add(new LimbusModEditor.Domain.Assets.AssetRecord
            {
                LogicalPath = "source.bundle/main.assets/42.28",
                SourcePath = bundle,
                UnityPathId = 42,
                UnityTypeId = 28,
                Metadata = { ["unityBundle"] = "true" }
            });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(null, project, Path.Combine(root, "out.carra2")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
