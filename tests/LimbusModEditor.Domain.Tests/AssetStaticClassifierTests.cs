using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 静态数据表判据（<see cref="AssetStaticClassifier"/>）：三条判据 → 一个位掩码。
///
/// <para>这些用例锚定的是**判据的边界**，不是实现细节：三条判据各自只看一个事实
/// （catalog 内层键 / bundle 名 / 容器路径），而位掩码的意义在于「catalog 缺席只影响位1」——
/// 这正是老功能「时灵时不灵」要修的东西，所以「不知道 ≠ 不是」必须有测试盯着。</para>
/// </summary>
public sealed class AssetStaticClassifierTests
{
    private const string StaticBundleName = "static_s1_0_assets_all_0123456789abcdef0123456789abcdef.bundle";
    private const string StaticTablePath = "Assets/Resources_moved/StaticData/static-data/item/item-02.json";

    [Theory]
    [InlineData(true, "abcdef0123456789abcdef0123456789", StaticKind.CatalogMark)]
    [InlineData(false, "abcdef0123456789abcdef0123456789", StaticKind.None)]
    [InlineData(false, StaticBundleName, StaticKind.BundleName)]
    [InlineData(true, StaticBundleName, StaticKind.CatalogMark | StaticKind.BundleName)]
    [InlineData(false, null, StaticKind.None)]
    public void Bundle_bits_combine_the_catalog_mark_and_the_bundle_name(
        bool catalogMark, string? innerKey, StaticKind expected)
        => Assert.Equal(expected, AssetStaticClassifier.BundleBits(catalogMark, innerKey));

    [Theory]
    [InlineData(StaticTablePath, StaticKind.ContainerPath)]
    [InlineData(@"Assets\Resources_moved\StaticData\static-data\item\item-02.json", StaticKind.ContainerPath)]
    [InlineData("Assets/Prefab/Unit/unit-01.prefab", StaticKind.None)]
    [InlineData("", StaticKind.None)]
    [InlineData(null, StaticKind.None)]
    public void Row_bits_come_from_the_container_path_only(string? containerEntry, StaticKind expected)
        => Assert.Equal(expected, AssetStaticClassifier.RowBits(containerEntry));

    [Fact]
    public void Merging_is_idempotent_and_any_bit_makes_it_static()
    {
        var merged = AssetStaticClassifier.Merge(StaticKind.CatalogMark, StaticKind.ContainerPath);
        Assert.Equal(StaticKind.CatalogMark | StaticKind.ContainerPath, merged);
        Assert.Equal(merged, AssetStaticClassifier.Merge(merged, StaticKind.ContainerPath));
        Assert.True(AssetStaticClassifier.IsStatic(StaticKind.BundleName));
        Assert.False(AssetStaticClassifier.IsStatic(StaticKind.None));
    }

    /// <summary>落库/落盘形态是十进制整数；老记录里的 <c>"true"</c> 仍要认。
    /// 认不出来的值一律当「非静态」——**宁可不藏，也不错藏**（藏错了用户找不到资源，
    /// 多显示一张表只是噪音）。</summary>
    [Theory]
    [InlineData("true", StaticKind.CatalogMark)]
    [InlineData("TRUE", StaticKind.CatalogMark)]
    [InlineData("false", StaticKind.None)]
    [InlineData("1", StaticKind.CatalogMark)]
    [InlineData("2", StaticKind.BundleName)]
    [InlineData("5", StaticKind.CatalogMark | StaticKind.ContainerPath)]
    [InlineData("0", StaticKind.None)]
    [InlineData("8", StaticKind.None)]   // 只有未知位：不当作静态
    [InlineData("garbage", StaticKind.None)]
    [InlineData("", StaticKind.None)]
    [InlineData(null, StaticKind.None)]
    public void Parse_accepts_both_the_legacy_and_the_bitmask_form(string? value, StaticKind expected)
        => Assert.Equal(expected, AssetStaticClassifier.Parse(value));

    [Fact]
    public void Parse_drops_unknown_bits_but_keeps_the_known_ones()
        => Assert.Equal(StaticKind.CatalogMark, AssetStaticClassifier.Parse("9")); // 位1 + 未知位8

    [Fact]
    public void Format_round_trips_and_is_invariant()
    {
        var kind = StaticKind.CatalogMark | StaticKind.ContainerPath;
        Assert.Equal("5", AssetStaticClassifier.Format(kind));
        Assert.Equal(kind, AssetStaticClassifier.Parse(AssetStaticClassifier.Format(kind)));
    }

    /// <summary>读一条记录的结论（记录上可能完全没有这个键 —— 老项目、导入的旧式资源）。</summary>
    [Fact]
    public void Reads_the_conclusion_from_the_record_metadata()
    {
        var record = new AssetRecord();
        Assert.Equal(StaticKind.None, AssetStaticClassifier.Of(record));
        record.Metadata[UnityCacheScanService.StaticBundleMetadataKey] = "4";
        Assert.Equal(StaticKind.ContainerPath, AssetStaticClassifier.Of(record));
        record.Metadata[UnityCacheScanService.StaticBundleMetadataKey] = "true";
        Assert.Equal(StaticKind.CatalogMark, AssetStaticClassifier.Of(record));
    }

    [Fact]
    public void Describe_spells_out_why_it_was_judged_static()
    {
        Assert.Equal("非静态", AssetStaticClassifier.Describe(StaticKind.None));
        Assert.Equal("catalog 标记", AssetStaticClassifier.Describe(StaticKind.CatalogMark));
        Assert.Equal("catalog 标记 + 容器路径",
            AssetStaticClassifier.Describe(StaticKind.CatalogMark | StaticKind.ContainerPath));
    }
}
