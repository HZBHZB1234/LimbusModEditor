using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// relation.describe / relation.links 的契约测试（WEB-IPC-CONTRACT §2.5）。
///
/// <para>覆盖三条前提：<b>库没建好 → 空列表 + 中文原因（不抛、不编造）</b>、
/// 有数据 → 能拿到对象身份与该条引用的形态、IPC 层找不到的资源给中文 NotFound。</para>
/// </summary>
public sealed class RelationDescribeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-relation-describe-" + Guid.NewGuid().ToString("N"));

    public RelationDescribeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录 */ }
    }

    private static RelationIndexSource Source()
        => RelationIndexSource.From("1459:100:999", "0:200", "10:20", "v4|1:2|hash|dir");

    private static AssetRecord AssetWithContainer(string containerEntry) => new()
    {
        LogicalPath = "bundle/inner/cab/123.28",
        Metadata = { ["containerEntry"] = containerEntry },
    };

    private static RelationQueryService Service(string cacheDirectory)
    {
        var store = new RelationStore(cacheDirectory);
        var source = Source();
        store.EnsureSource(source);
        store.PersistGraph(source, new RelationGraph(
            [new RelationSubject("persona:10201", RelationCategories.Persona, "浮士德 · LCB", "LCB", "Faust", "10201")
             {
                 CategoryLabel = "人格",
             }],
            [new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Image,
                 "Assets/Sprite/Unit/Profile/10201.png", "10201.png", "精灵图 · 200 B", 200)]));
        return new RelationQueryService(store);
    }

    // ── 空库 ────────────────────────────────────────────────────────

    [Fact]
    public void Describe_over_an_empty_store_returns_no_rows_with_a_chinese_reason()
    {
        var service = new RelationQueryService(new RelationStore(_root));

        var hits = service.DescribeHitsForAsset(AssetWithContainer("Assets/Sprite/Unit/Profile/10201.png"));

        Assert.Empty(hits.Hits);
        Assert.False(string.IsNullOrWhiteSpace(hits.Info));
        Assert.Contains("关联图", hits.Info);
    }

    // ── 有数据 ──────────────────────────────────────────────────────

    [Fact]
    public void Describe_returns_the_subject_identity_and_the_matched_reference_shape()
    {
        var service = Service(_root);

        var hits = service.DescribeHitsForAsset(AssetWithContainer("Assets/Sprite/Unit/Profile/10201.png"));
        var hit = Assert.Single(hits.Hits);

        // 对象身份（subjectId 同时也是维基页面 id）
        Assert.Equal("persona:10201", hit.SubjectId);
        Assert.Equal("浮士德 · LCB", hit.DisplayName);
        Assert.Equal(RelationCategories.Persona, hit.Category);
        Assert.Equal("人格", hit.CategoryLabel);
        Assert.Equal("LCB", hit.Subtitle);
        // 该条引用的形态
        Assert.Equal(RelationKind.Image.ToString(), hit.Kind);
        Assert.Equal("图像", hit.KindLabel);
        Assert.Equal("10201.png", hit.Display);
        Assert.Equal("精灵图 · 200 B", hit.Detail);

        // 没被引用的资源：空列表 + 中文原因（不是抛异常）
        var miss = service.DescribeHitsForAsset(AssetWithContainer("Assets/Sprite/Other/99999.png"));
        Assert.Empty(miss.Hits);
        Assert.Contains("没有关联到", miss.Info);
    }

    // ── IPC 层 ──────────────────────────────────────────────────────

    private static IpcGateway Gateway(RelationQueryService? relations = null)
    {
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(Path.GetTempPath(), "ipc-rel-" + Guid.NewGuid().ToString("N") + ".db")),
            EmptyAssetStateSource.Instance);
        var spineData = new SpineDataGateway(Path.Combine(Path.GetTempPath(), "ipc-rel-spine-" + Guid.NewGuid().ToString("N")));
        var bankIndex = new BankIndexService(new BankIndexStore(Path.GetTempPath()));
        var staticIndex = new StaticIndexService(new StaticTableIndexStore(Path.GetTempPath()));
        return new IpcGateway(catalog, new ProjectState(), spineData, bankIndex,
            new LangTextWorkbenchService(), staticIndex, relations: relations);
    }

    [Fact]
    public async Task Relation_describe_reports_a_missing_asset_in_chinese()
    {
        var gateway = Gateway(Service(_root));

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-rel-1", "relation.describe",
            SerializePayload(new RelationDescribeRequest("不存在的资源.lmeproj"))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.NotFound, response.Error!.Code);
        Assert.Contains("未找到资源", response.Error!.Message);
    }

    [Fact]
    public async Task Relation_links_over_an_unbuilt_graph_returns_no_links_with_a_reason()
    {
        var gateway = Gateway(new RelationQueryService(new RelationStore(_root)));

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-rel-2", "relation.links",
            SerializePayload(new RelationLinksRequest("persona:10201"))).ToJson());
        var payload = IpcResponse.FromJson(json).Payload!.Value.Deserialize<RelationLinksResponse>(Options)!;

        Assert.Empty(payload.Links);
        Assert.Contains("关联图", payload.Info);
    }
}
