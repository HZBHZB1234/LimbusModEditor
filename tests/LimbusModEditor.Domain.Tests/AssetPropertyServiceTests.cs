using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-01 第 2 步：属性汇总服务。合成样本验证「通用行 + 单项失败
/// 降级不抛异常」；真实缓存样本验证 Texture / Sprite / TextAsset 能产出具体属性。</summary>
public class AssetPropertyServiceTests
{
    private static string? RealCacheRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir)) return overrideDir;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var candidate = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void Common_rows_are_always_present_and_missing_source_degrades_per_item()
    {
        var asset = new AssetRecord
        {
            LogicalPath = "outer/inner/CAB-x/42.28",
            SourcePath = null,
            ContainerPath = "CAB-x",
            Account = "00112233445566778899aabbccddeeff",
            Bundle = "ffeeddccbbaa99887766554433221100",
            UnityPathId = 42,
            UnityTypeId = 28,
            Type = AssetType.Texture,
            Size = 3 * 1024 * 1024,
            Metadata = { ["unityBundle"] = "true" }
        };
        var rows = new AssetPropertyService().Describe(asset);

        Assert.Contains(rows, r => r.Label == "类型" && r.Value.Contains("纹理"));
        Assert.Contains(rows, r => r.Label == "大小" && r.Value.Contains("MB") && r.Value.Contains("字节"));
        Assert.Contains(rows, r => r.Label == "所在 bundle" && r.Value.StartsWith("00112233…"));
        // 没有可读来源时逐项降级为「不可读」，不抛异常、不编造数值。
        var textureRow = rows.First(r => r.Label == "纹理属性");
        Assert.Contains("不可读", textureRow.Value);
    }

    [Fact]
    public void Real_cache_texture_sprite_and_text_assets_produce_concrete_properties()
    {
        var root = RealCacheRoot();
        if (root is null) return; // 无样本机器：跳过

        var bundles = EnumerateBundles(root, limit: 12);
        if (bundles.Count == 0) return;

        var service = new UnityAssetService();
        var properties = new AssetPropertyService();
        var seen = new HashSet<AssetType>();
        foreach (var bundle in bundles)
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception) { continue; }
            foreach (var asset in objects)
            {
                if (asset.Type is not (AssetType.Texture or AssetType.Sprite or AssetType.Text) || !seen.Add(asset.Type)) continue;
                var rows = properties.Describe(asset);
                Assert.NotEmpty(rows);
                Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
                // 这些类型在真实缓存里必须能读到具体属性（不是「不可读」降级）。
                Assert.DoesNotContain(rows, r => r.Value.Contains("不可读"));
                switch (asset.Type)
                {
                    case AssetType.Texture:
                        Assert.Contains(rows, r => r.Label == "尺寸" && r.Value.Contains("×"));
                        Assert.Contains(rows, r => r.Label == "格式");
                        break;
                    case AssetType.Sprite:
                        Assert.Contains(rows, r => r.Label == "rect 区域");
                        Assert.Contains(rows, r => r.Label == "图集纹理");
                        break;
                    case AssetType.Text:
                        Assert.Contains(rows, r => r.Label == "编码");
                        Assert.Contains(rows, r => r.Label == "JSON 可解析");
                        break;
                }
            }
            if (seen.Count == 3) break;
        }
        Assert.Equal(3, seen.Count); // 12 个真实 bundle 内三类样本都必须出现
    }

    private static IReadOnlyList<string> EnumerateBundles(string root, int limit)
    {
        var result = new List<string>();
        try
        {
            foreach (var outer in Directory.EnumerateDirectories(root))
            {
                foreach (var inner in Directory.EnumerateDirectories(outer))
                {
                    var data = Path.Combine(inner, "__data");
                    if (!File.Exists(data)) continue;
                    result.Add(data);
                    if (result.Count >= limit) return result;
                }
            }
        }
        catch (Exception) { /* 部分结果可用 */ }
        return result;
    }
}
