using System.Text.Json;
using LimbusModEditor.Application.Relations.WikiBaseData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 静态表行级匹配（<see cref="StaticTableRows"/>）与 Lang 实体目录
/// （<see cref="WikiLangCatalog"/>）语义测试。
/// 覆盖：三种正文形态、行级主键精确匹配（坑 1）、同数字 id 跨文件类别不串（坑 2）、
/// 罪孽属性中文名映射、缺失文件/目录的降级。
/// </summary>
public sealed class WikiBaseDataCatalogTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "lme-wiki-base-data-tests-" + Guid.NewGuid().ToString("N"));

    public WikiBaseDataCatalogTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDirectory, recursive: true); } catch (IOException) { }
    }

    // ── StaticTableRows ────────────────────────────────────────────

    [Fact]
    public void Rows_parse_list_wrapper_and_top_level_array_and_single_object()
    {
        var asList = StaticTableRows.Parse("""{"list":[{"id":1},{"id":2}]}""");
        var asArray = StaticTableRows.Parse("""[{"id":1},{"id":2}]""");
        var asSingle = StaticTableRows.Parse("""{"id":1,"seasonId":1}""");

        Assert.Equal(2, asList.Count);
        Assert.Equal(2, asArray.Count);
        Assert.Single(asSingle);
    }

    [Fact]
    public void Row_match_is_exact_on_numeric_primary_key()
    {
        var rows = StaticTableRows.Parse("""{"list":[{"id":10201},{"id":1020101},{"personalityID":10201}]}""");

        // 10201 绝不能撞上 1020101 —— 文件级 links 会撞，行级必须精确。
        var hit = StaticTableRows.Find(rows, "id", 10201);
        Assert.NotNull(hit);
        Assert.Equal(10201, hit!.Value.GetProperty("id").GetInt64());
        Assert.Null(StaticTableRows.Find(rows, "id", 999));
        // 换主键属性名（personality-passive 表用 personalityID）。
        Assert.NotNull(StaticTableRows.Find(rows, "personalityID", 10201));
    }

    [Fact]
    public void Rows_parse_degrades_on_null_or_broken_text()
    {
        Assert.Empty(StaticTableRows.Parse(null));
        Assert.Empty(StaticTableRows.Parse("  "));
        Assert.Empty(StaticTableRows.Parse("{not json"));
    }

    // ── WikiLangCatalog ────────────────────────────────────────────

    [Fact]
    public void Skill_and_passive_names_are_separated_by_file_category()
    {
        // 坑 2：同一数字 1020101 在 Skills.json 是「纵斩」、在 Passives.json 是「分析」。
        // 必须按文件类别取，绝不许串。
        File.WriteAllText(Path.Combine(_tempDirectory, "Skills.json"),
            """{"dataList":[{"id":1020101,"levelList":[{"level":1,"name":"纵斩","desc":"","coinlist":[{"coindescs":[{"desc":"硬币A"}]}]},{"level":4,"name":"纵斩","desc":"高等级描述","coinlist":[{"coindescs":[{"desc":"硬币B"}]}]}]}]}""");
        File.WriteAllText(Path.Combine(_tempDirectory, "Passives.json"),
            """{"dataList":[{"id":1020101,"name":"分析","desc":"伤害+10%"}]}""");

        var catalog = WikiLangCatalog.TryCreate(_tempDirectory);
        Assert.NotNull(catalog);

        var skill = catalog!.Skill(1020101);
        Assert.NotNull(skill);
        Assert.Equal("纵斩", skill!.Name);
        // levelList 取最高等级（level 4）那条。
        Assert.Equal("高等级描述", skill.Desc);
        Assert.Equal(["硬币B"], skill.CoinDescs);

        var passive = catalog.Passive(1020101);
        Assert.NotNull(passive);
        Assert.Equal("分析", passive!.Name);
        Assert.Equal("伤害+10%", passive.Desc);
    }

    [Fact]
    public void Skill_lookup_merges_all_skills_files_by_id()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "Skills.json"),
            """{"dataList":[{"id":1020101,"levelList":[{"level":1,"name":"纵斩"}]}]}""");
        File.WriteAllText(Path.Combine(_tempDirectory, "Skills_personality-02.json"),
            """{"dataList":[{"id":1020501,"levelList":[{"level":1,"name":"地区巡查","desc":"防御等级提升"}]}]}""");

        var catalog = WikiLangCatalog.TryCreate(_tempDirectory)!;
        Assert.Equal("纵斩", catalog.Skill(1020101)!.Name);
        Assert.Equal("地区巡查", catalog.Skill(1020501)!.Name);
        // 进阶技能文件里没有的被动 id 不得到技能名（跨类别不串）。
        Assert.Null(catalog.Skill(1020121));
    }

    [Fact]
    public void Personality_and_attribute_labels_resolve()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "Personalities.json"),
            """{"dataList":[{"id":10201,"title":"LCB\n罪人","name":"浮士德","desc":"浮士德的第1人格"}]}""");
        File.WriteAllText(Path.Combine(_tempDirectory, "AttributeText.json"),
            """{"dataList":[{"id":"INDIGO","name":"傲慢"},{"id":"AZURE","name":"忧郁"}]}""");

        var catalog = WikiLangCatalog.TryCreate(_tempDirectory)!;
        var personality = catalog.Personality(10201);
        Assert.Equal("浮士德", personality!.Name);
        Assert.Equal("LCB\n罪人", personality.Title);

        Assert.Equal("傲慢", catalog.AttributeLabel("INDIGO"));
        Assert.Equal("忧郁", catalog.AttributeLabel("azure")); // 大小写不敏感
        Assert.Null(catalog.AttributeLabel("UNKNOWN"));        // 查不到回落原值，不编造
    }

    [Fact]
    public void Catalog_returns_null_when_directory_missing()
    {
        Assert.Null(WikiLangCatalog.TryCreate(null));
        Assert.Null(WikiLangCatalog.TryCreate(Path.Combine(_tempDirectory, "no-such-dir")));
    }

    [Fact]
    public void Missing_files_degrade_to_null_lookups()
    {
        // 目录存在但没有对应文件：查不到就是查不到，不抛、不编。
        var catalog = WikiLangCatalog.TryCreate(_tempDirectory)!;
        Assert.Null(catalog.Skill(1020101));
        Assert.Null(catalog.Passive(1020101));
        Assert.Null(catalog.Personality(10201));
        Assert.Null(catalog.AttributeLabel("INDIGO"));
    }
}
