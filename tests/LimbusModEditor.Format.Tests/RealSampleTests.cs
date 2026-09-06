using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// Verification against REAL game samples found via LCTA's documented paths
/// (Unity cache LocalLow/Unity/ProjectMoon_LimbusCompany, mods under
/// %APPDATA%\LimbusCompanyMods). Tests skip silently when a sample is not
/// present, so machines without the game stay green.
/// </summary>
public class RealSampleTests
{
    [Fact]
    public void Real_carra2_round_trips_every_entry_and_payload()
    {
        var modPath = RealSamples.RealCarra2;
        if (modPath is null)
        {
            // no real mod installed on this machine
            return;
        }

        CarraPackage package;
        using (var input = File.OpenRead(modPath)) package = CarraArchive.Read(input, preserveUnknownFiles: true);
        Assert.NotEmpty(package.Entries);

        // a round-trip must preserve every key and every payload byte-for-byte
        using var output = new MemoryStream();
        CarraArchive.Write(package, output, preserveUnknownFiles: true);
        var roundTripped = CarraArchive.Read(new MemoryStream(output.ToArray()), preserveUnknownFiles: true);

        Assert.Equal(package.Entries.Count, roundTripped.Entries.Count);
        var byPath = roundTripped.Entries.ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
        foreach (var entry in package.Entries)
        {
            var twin = byPath[entry.SourcePath];
            Assert.Equal(entry.CompressedData, twin.CompressedData);
        }
        // preserved unknown files (e.g. the converter's carra.json marker)
        Assert.Equal(package.UnknownFiles.Count, roundTripped.UnknownFiles.Count);
    }

    [Fact]
    public void Real_carra2_key_type_field_is_a_type_table_index()
    {
        var modPath = RealSamples.RealCarra2;
        if (modPath is null) return;

        using var input = File.OpenRead(modPath);
        var package = CarraArchive.Read(input, preserveUnknownFiles: true);
        Assert.NotEmpty(package.Entries);

        // LCTA's loader reads the suffix as the SerializedFile TYPE TABLE index
        // (patch.py:287-301), never as a global Unity class ID: real mods carry
        // small indices (0..~80). A global-class-ID reading would classify
        // typeIdx=28 entries as Texture2D etc., which is semantically wrong.
        var indices = package.Entries.Select(x => x.Key.TypeId).Where(t => t.HasValue).Select(t => t!.Value).ToArray();
        Assert.NotEmpty(indices);
        Assert.All(indices, i => Assert.InRange(i, 0, 512));
    }

    [Fact]
    public void Real_bundles_parse_through_our_stack()
    {
        var root = RealSamples.UnityCacheRoot;
        if (root is null) return;

        var bundles = RealSamples.BundleDataFiles(limit: 6);
        Assert.NotEmpty(bundles);

        var service = new UnityAssetService();
        var stats = new List<string>();
        foreach (var bundle in bundles)
        {
            var descriptors = service.ScanBundle(bundle);
            // every parsed object needs a non-empty container path and ids
            Assert.All(descriptors, d => Assert.True(d.UnityPathId.HasValue));
            stats.Add($"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(bundle)))}: {descriptors.Count} 对象, " +
                      $"类型 {string.Join(",", descriptors.Select(d => d.Type).Distinct())}");
        }
        // probe output is captured by the test framework log
        Console.WriteLine("真实 bundle 解析结果:\n" + string.Join("\n", stats));
    }

    [Fact]
    public void Real_bundle_objects_survive_the_field_read_pipeline()
    {
        var root = RealSamples.UnityCacheRoot;
        if (root is null) return;

        var service = new UnityAssetService();
        var readTextures = 0;
        var readSummaries = 0;
        foreach (var bundle in RealSamples.BundleDataFiles(limit: 60))
        {
            var descriptors = service.ScanBundle(bundle);
            var texture = descriptors.FirstOrDefault(d => d.Type == AssetType.Texture && d.UnityPathId.HasValue);
            if (texture is { UnityPathId: not null })
            {
                var summary = service.ReadTextureSummary(bundle, texture.UnityPathId.Value);
                if (summary is not null)
                {
                    Assert.True(summary.Width > 0 && summary.Height > 0);
                    readTextures++;
                }
            }
            // P1.5 summaries exist only for Mesh/AnimationClip/Font; probe those
            var supported = descriptors.FirstOrDefault(d => d.UnityPathId.HasValue &&
                d.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font);
            if (supported is { UnityPathId: not null, ContainerPath: not null })
            {
                var objectSummary = service.ReadBundleObjectSummary(bundle, supported.ContainerPath, supported.UnityPathId.Value);
                if (objectSummary is not null) readSummaries++;
            }
            if (readSummaries >= 2 && readTextures >= 5) break;
        }
        // real data must flow through the field-read pipelines, not come back
        // silently empty
        Assert.True(readSummaries >= 1, $"Mesh/AnimationClip/Font 摘要在真实 bundle 上读取了 {readSummaries} 个");
        Assert.True(readTextures >= 5, $"真实纹理摘要读取了 {readTextures} 个");
        Console.WriteLine($"真实对象读取: 纹理摘要 {readTextures} 个, Mesh/Animation/Font 摘要 {readSummaries} 个");
    }

    [Fact]
    public void Real_bundle_typetree_availability_survey()
    {
        var root = RealSamples.UnityCacheRoot;
        if (root is null) return;

        using var backend = new AssetsToolsBackend();
        var withTrees = 0;
        var withoutTrees = 0;
        var versions = new List<string>();
        foreach (var bundle in RealSamples.BundleDataFiles(limit: 12))
        {
            foreach (var (enabled, version, types) in backend.SurveyBundle(bundle))
            {
                if (enabled) withTrees++; else withoutTrees++;
                versions.Add($"{version} types={types}");
            }
        }
        Console.WriteLine($"真实 bundle 类型树勘察: 内嵌类型树 {withTrees}, 剥离 {withoutTrees}; 版本: {string.Join("; ", versions.Take(6))}");
    }
}
