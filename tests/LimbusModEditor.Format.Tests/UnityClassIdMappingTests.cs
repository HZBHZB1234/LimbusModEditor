using AssetsTools.NET.Extra;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// Regression guard for the Unity class-ID mapping used by every importer.
/// The mapping must follow Unity's canonical class-ID table (as exposed by
/// AssetsTools.NET) so objects are never silently miscategorized.
/// </summary>
public class UnityClassIdMappingTests
{
    private static int IdOf(string name) => (int)Enum.Parse(typeof(AssetClassID), name);

    [Fact]
    public void MappedClassIdsMatchAssetsToolsCanonicalTable()
    {
        // Canonical ids from AssetsTools.NET's AssetClassID table.
        Assert.Equal(1, IdOf(nameof(AssetClassID.GameObject)));
        Assert.Equal(28, IdOf(nameof(AssetClassID.Texture2D)));
        Assert.Equal(49, IdOf(nameof(AssetClassID.TextAsset)));
        Assert.Equal(83, IdOf(nameof(AssetClassID.AudioClip)));
        Assert.Equal(114, IdOf(nameof(AssetClassID.MonoBehaviour)));
        Assert.Equal(115, IdOf(nameof(AssetClassID.MonoScript)));
        Assert.Equal(128, IdOf(nameof(AssetClassID.Font)));
        Assert.Equal(142, IdOf(nameof(AssetClassID.AssetBundle)));
        Assert.Equal(213, IdOf(nameof(AssetClassID.Sprite)));
    }

    [Theory]
    [InlineData(1, AssetType.GameObject)]
    [InlineData(28, AssetType.Texture)]
    // plan-01：TextAsset（49）此前未映射，导致真实缓存里的文本资源一律 Unknown、
    // 文本预览无法分派；这里固定为 Text 并纳入回归。
    [InlineData(49, AssetType.Text)]
    [InlineData(83, AssetType.Audio)]
    [InlineData(114, AssetType.MonoBehaviour)]
    [InlineData(115, AssetType.MonoScript)]
    [InlineData(128, AssetType.Font)]
    [InlineData(142, AssetType.Binary)]
    [InlineData(213, AssetType.Sprite)]
    public void BackendTypeMappingFollowsCanonicalTable(int typeId, AssetType expected)
    {
        Assert.True(UnityClassId.TryMap(typeId, out var mapped));
        Assert.Equal(expected, mapped);
    }
}
