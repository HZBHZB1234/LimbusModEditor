using System.IO.Compression;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public sealed class GenericZipImportTests
{
    [Fact]
    public async Task NonLunartiqueZipIsSafelyExtractedAndIndexed()
    {
        var root = Directory.CreateTempSubdirectory("lme-zip-");
        try
        {
            var zipPath = Path.Combine(root.FullName, "mod.zip");
            await using (var file = File.Create(zipPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry("textures/hero.png").Open();
                entry.Write([1, 2, 3]);
            }
            var project = new ModProject { SourceDirectory = Path.Combine(root.FullName, "sources") };
            var result = await new ModImportService(BuiltInFormatRegistry.Create()).ImportIntoProjectAsync(zipPath, project);
            Assert.Equal(LimbusModEditor.Domain.Formats.ModFormatKind.Directory, result.Format);
            var asset = Assert.Single(project.Assets);
            Assert.Equal(AssetType.Texture, asset.Type);
            Assert.True(File.Exists(asset.SourcePath));
            Assert.Single(project.Sources);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ZipPathTraversalIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("lme-zip-");
        try
        {
            var zipPath = Path.Combine(root.FullName, "bad.zip");
            await using (var file = File.Create(zipPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry("../escape.bin").Open();
                entry.Write([1]);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => new ModImportService(BuiltInFormatRegistry.Create()).ImportGenericZipIntoProjectAsync(zipPath, new ModProject()));
        }
        finally { root.Delete(true); }
    }
}
