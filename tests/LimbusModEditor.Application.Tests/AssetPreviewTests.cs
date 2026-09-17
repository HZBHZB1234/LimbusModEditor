using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// asset.preview 的 IPC 层契约测试（前端预览面板看图/看文本/看元数据就靠它）。
///
/// <para>钉住三件事：<b>不再是 unknown 桩</b>（形态按资源类型给、属性行走
/// <see cref="AssetPropertyService"/>）、<b>找不到资源给中文 NotFound</b>、
/// <b>解不出图像地址给 null 不编造 URL</b>。</para>
/// </summary>
public sealed class AssetPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-preview-" + Guid.NewGuid().ToString("N"));
    private readonly IpcGateway _gateway;

    /// <summary>索引里放两条：一张纹理（容器路径 + LogicalPath 都可定位）、一条文本。</summary>
    private const string TexturePath = "Assets/Animation/SD/cg_01.png";
    private const string TextPath = "Assets/Text/表/表-01.json";

    public AssetPreviewTests()
    {
        Directory.CreateDirectory(_root);
        var store = new UnityCacheSqliteIndexStore(Path.Combine(_root, "index.db"));
        var dataPath = Path.Combine(_root, "outer1", "inner1", "__data");
        store.PersistAll(
            [new UnityCacheScanEntry("outer1", "inner1", dataPath)],
            [(new UnityCacheIndexBundle(dataPath, 4096, 900, "outer1", "inner1", StaticKind.None),
                new[]
                {
                    new UnityCacheIndexRow(0, "CAB-1", -4060527305021521791, 28, AssetType.Texture, 1_024,
                        null, TexturePath, AssetStaticClassifier.RowBits(TexturePath)),
                    new UnityCacheIndexRow(1, "CAB-1", 3, 49, AssetType.Text, 30,
                        null, TextPath, AssetStaticClassifier.RowBits(TextPath)),
                })]);
        Assert.True(store.EnsureDerived().Rebuilt);

        _gateway = new IpcGateway(new AssetCatalog(store, EmptyAssetStateSource.Instance), new ProjectState(),
            new SpineDataGateway(Path.Combine(_root, "spine")),
            new BankIndexService(new BankIndexStore(_root)),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 测试结束，尽力清理 */ }
    }

    private async Task<IpcResponse> Preview(string assetId)
        => IpcResponse.FromJson(await _gateway.HandleRequestAsync(
            IpcRequest.Create("req-preview-" + Guid.NewGuid().ToString("N"), "asset.preview",
                SerializePayload(new AssetPreviewRequest(assetId))).ToJson()));

    [Fact]
    public async Task Preview_without_an_asset_id_reports_a_chinese_reason()
    {
        var response = await Preview(" ");

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("缺少资源标识", response.Error!.Message);
    }

    [Fact]
    public async Task Preview_of_an_unknown_asset_reports_not_found()
    {
        var response = await Preview("Assets/没有/这条.png");

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.NotFound, response.Error!.Code);
        Assert.Contains("未找到资源", response.Error!.Message);
    }

    /// <summary>纹理：形态是 Image，属性行来自 AssetPropertyService（类型/大小/容器路径…）。</summary>
    [Fact]
    public async Task Preview_of_a_texture_returns_the_image_kind_and_property_rows()
    {
        var response = await Preview(TexturePath);
        Assert.True(response.Ok, response.Error?.Message);

        var preview = response.Payload!.Value.Deserialize<AssetPreviewResponse>(IpcJson.Options)!;
        Assert.Equal("Image", preview.Kind);
        Assert.NotEmpty(preview.Rows);
        Assert.Contains(preview.Rows, r => r.Label == "类型");
        Assert.Contains(preview.Rows, r => r.Label == "大小");
        // 索引里没有真实像素文件：地址给 null（不编造 URL，前端降级）
        Assert.Null(preview.BinaryUrl);
    }

    /// <summary>文本：形态是 Text，属性行照给。</summary>
    [Fact]
    public async Task Preview_of_a_text_asset_returns_the_text_kind()
    {
        var response = await Preview(TextPath);
        Assert.True(response.Ok, response.Error?.Message);

        var preview = response.Payload!.Value.Deserialize<AssetPreviewResponse>(IpcJson.Options)!;
        Assert.Equal("Text", preview.Kind);
        Assert.NotEmpty(preview.Rows);
        Assert.Null(preview.BinaryUrl);
    }
}
