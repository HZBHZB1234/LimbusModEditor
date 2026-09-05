using System.IO.Compression;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Domain.Tests;

public class ModExportServiceTests
{
    [Fact]
    public async Task ExportsCarraWithRecordedReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.carra");
        var replacement = Path.Combine(root, "replacement.bin");
        var output = Path.Combine(root, "out.carra");
        try
        {
            await using (var file = File.Create(source))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var entry = archive.CreateEntry("acct/bundle/1.28").Open();
                entry.Write([1]);
            }
            await File.WriteAllBytesAsync(replacement, [9, 8]);
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "acct/bundle/1.28", Type = AssetType.Binary };
            asset.Metadata["replacementPath"] = replacement;
            project.Assets.Add(asset);
            var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(source, project, output);
            Assert.Equal(1, result.AppliedReplacements);
            using (var exported = File.OpenRead(output))
            {
                var package = CarraArchive.Read(exported);
                Assert.Equal(new byte[] { 9, 8 }, package.Entries.Single().ReadData());
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
