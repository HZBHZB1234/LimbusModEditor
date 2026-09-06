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

    [Fact]
    public async Task Export_reports_cache_alignment_for_mismatched_outer_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-align-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.carra2");
        var output = Path.Combine(root, "out.carra2");
        try
        {
            // two keys: one whose outer key exists in the fake cache with the
            // bundle present, one that points at a vanished (updated-away) key
            await using (var file = File.Create(source))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var present = archive.CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/1.0").Open())
                    present.Write([1]);
                using (var missing = archive.CreateEntry("ffffffffffffffffffffffffffffffff/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/2.1").Open())
                    missing.Write([2]);
            }
            // fake Unity cache with only the first (outer, inner) present
            var cache = Path.Combine(root, "cache", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(cache, "__data"), [0]);
            var project = new ModProject { UnityCacheDirectory = Path.Combine(root, "cache") };
            var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(source, project, output);
            var alignment = result.Diagnostics.Where(x => x.StartsWith("缓存对齐", StringComparison.Ordinal)).ToArray();
            Assert.Single(alignment);
            Assert.Contains("ffffffffffffffffffffffffffffffff", alignment[0]);
            Assert.DoesNotContain(alignment, x => x.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
