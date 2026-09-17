using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// static.records 的契约测试（WEB-IPC-CONTRACT §2.7）。
///
/// <para>两层：①「行集合 → 记录分页」这一层（主键口径：有 id 用 id、没有用行下标；
/// 分页边界；正文不是行集合时返回空，不抛、不编造）；②IPC 层——前提缺失一律中文错误，
/// 不返回空列表充数。</para>
/// </summary>
public sealed class StaticTableRecordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-static-records-" + Guid.NewGuid().ToString("N"));

    public StaticTableRecordsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录 */ }
    }
    private const string ListBody = """
        {"list":[
          {"id":1,"name":"浮士德"},
          {"id":2,"name":"唐吉诃德"},
          {"id":3,"name":"良秀"}
        ]}
        """;

    [Fact]
    public void Rows_reads_the_list_shape_and_pages_by_offset_and_take()
    {
        var rows = StaticTableRecords.Rows(ListBody);
        Assert.Equal(3, rows.Count);

        var first = StaticTableRecords.Page(rows, 0, 2);
        Assert.Equal(2, first.Count);
        Assert.Equal("1", first[0].RecordId);
        Assert.Contains("id=1", first[0].Summary);
        Assert.Contains("浮士德", first[0].Summary);
        Assert.Equal("2", first[1].RecordId);

        var last = StaticTableRecords.Page(rows, 2, 2);
        var only = Assert.Single(last);
        Assert.Equal("3", only.RecordId);
        Assert.Contains("良秀", only.Summary);
    }

    [Fact]
    public void Rows_without_an_id_field_falls_back_to_the_row_index()
    {
        var rows = StaticTableRecords.Rows("""{"list":[{"name":"a"},{"name":"b"}]}""");

        var page = StaticTableRecords.Page(rows, 0, 10);

        Assert.Equal("0", page[0].RecordId);
        Assert.Equal("1", page[1].RecordId);
        // 摘要 = 主键 + 名称类字段的值（不重复写字段名）
        Assert.Contains("a", page[0].Summary);
    }

    [Fact]
    public void Rows_over_a_non_row_body_returns_empty_instead_of_throwing()
    {
        Assert.Empty(StaticTableRecords.Rows(null));
        Assert.Empty(StaticTableRecords.Rows("   "));
        Assert.Empty(StaticTableRecords.Rows("不是 JSON"));
    }

    [Fact]
    public void Rows_keeps_the_single_object_shape_as_one_row()
    {
        // 既有解析器把「对象但没有 list/dataList/dataArray」当成单对象表（如 season-info）——不是空表。
        var rows = StaticTableRecords.Rows("""{"season":1,"name":"第一赛季"}""");

        var only = Assert.Single(rows);
        var record = Assert.Single(StaticTableRecords.Page(rows, 0, 10));
        Assert.Equal("0", record.RecordId);
        Assert.Contains("season", record.RawJson);
    }

    [Fact]
    public void Rows_skips_a_utf8_bom_instead_of_dropping_the_whole_table()
    {
        // 真实缓存里 rpg-attack-type-data.json 的正文带 BOM 开头：不剥掉的话整张表会被当成「解析不出记录」。
        var rows = StaticTableRecords.Rows("\uFEFF" + """{"list":[{"id":7,"name":"Basic"}]}""");

        var page = StaticTableRecords.Page(rows, 0, 10);
        var only = Assert.Single(page);
        Assert.Equal("7", only.RecordId);
    }

    [Fact]
    public void Take_is_clamped_and_offset_beyond_the_end_yields_nothing()
    {
        var rows = StaticTableRecords.Rows(ListBody);

        Assert.Equal(3, StaticTableRecords.Page(rows, 0, 0).Count);
        Assert.Equal(3, StaticTableRecords.Page(rows, 0, StaticTableRecords.MaxTake + 1000).Count);
        Assert.Empty(StaticTableRecords.Page(rows, 99, 10));
    }

    // ── IPC 层：前提缺失 = 中文错误，不返回空列表充数 ──────────────────

    private IpcGateway Gateway(ProjectState state)
    {
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(Path.GetTempPath(), "ipc-static-" + Guid.NewGuid().ToString("N") + ".db")),
            EmptyAssetStateSource.Instance);
        var spineData = new SpineDataGateway(Path.Combine(Path.GetTempPath(), "ipc-static-spine-" + Guid.NewGuid().ToString("N")));
        var bankIndex = new BankIndexService(new BankIndexStore(_root));
        var staticIndex = new StaticIndexService(new StaticTableIndexStore(_root));
        return new IpcGateway(catalog, state, spineData, bankIndex, new LangTextWorkbenchService(), staticIndex);
    }

    [Fact]
    public async Task Records_without_an_open_project_asks_to_open_one_first()
    {
        var response = await Call(Gateway(new ProjectState()), "personality-01");

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("请先打开项目", response.Error!.Message);
    }

    [Fact]
    public async Task Records_without_a_locatable_bundle_reports_the_missing_prerequisite()
    {
        var state = new ProjectState();
        // 目录指向临时空目录：定位不到静态 bundle —— 也不该假装「这张表里没有数据」。
        state.SetProject(new ModProject { GameDirectory = _root, UnityCacheDirectory = _root }, "x.lmeproj");

        var response = await Call(Gateway(state), "personality-01");

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.NotFound, response.Error!.Code);
        Assert.Contains("无法定位静态数据 bundle", response.Error!.Message);
    }

    [Fact]
    public async Task Records_rejects_an_empty_table_id()
    {
        var state = new ProjectState();
        state.SetProject(new ModProject { GameDirectory = _root, UnityCacheDirectory = _root }, "x.lmeproj");

        var response = await Call(Gateway(state), "  ");

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("tableId", response.Error!.Message);
    }

    private static async Task<IpcResponse> Call(IpcGateway gateway, string tableId)
    {
        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-static-records",
            "static.records", SerializePayload(new StaticRecordsRequest(tableId))).ToJson());
        return IpcResponse.FromJson(json);
    }
}
