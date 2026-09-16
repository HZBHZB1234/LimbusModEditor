using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Relations.Authority;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 维基权威引擎（<see cref="WikiPageAuthorityEngine"/>）语义测试。
/// 覆盖：分节顺序、归类规则、深链载荷往返、负例（不得 id 猜测）、损坏/缺失数据的降级。
/// </summary>
public sealed class WikiAuthorityEngineTests : IDisposable
{
    public WikiAuthorityEngineTests()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static AuthorityExtractionContext CreateContext(
        IReadOnlyList<AssetRecord>? assets = null,
        IReadOnlyList<RelationAudioFact>? audio = null,
        IReadOnlyList<RelationStaticFact>? staticTables = null,
        IReadOnlyList<RelationLangFact>? langFiles = null,
        IReadOnlyList<RelationTextAnchor>? anchors = null)
    {
        return new AuthorityExtractionContext(
            assets ?? Array.Empty<AssetRecord>(),
            audio ?? Array.Empty<RelationAudioFact>(),
            staticTables ?? Array.Empty<RelationStaticFact>(),
            langFiles ?? Array.Empty<RelationLangFact>(),
            anchors ?? Array.Empty<RelationTextAnchor>());
    }

    private static AssetRecord Asset(string containerPath, AssetType type = AssetType.GameObject, long size = 1000)
        => new()
        {
            ContainerPath = containerPath,
            Type = type,
            Size = size,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["containerEntry"] = containerPath,
                ["reference"] = "true",
            },
        };

    private static RelationLangFact LangFile(string path, int keyCount = 10)
        => new(path, keyCount);

    private static RelationTextAnchor Anchor(string anchor, string table, string? name = null, string? desc = null, string? body = null, string? path = null)
        => new(anchor, path ?? $"PersonalityVoiceDlg/{anchor}.json", name, desc, body, table);

    [Fact]
    public void Persona_provider_extracts_facts_from_container_path()
    {
        var context = CreateContext(assets: new[]
        {
            Asset("/Prefab/SD/Personality/10201_yisang_LCBAppearance.prefab"),
            Asset("/Prefab/SD/Personality/10202_faust_LCBAppearance.prefab"),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        Assert.NotEmpty(facts.Facts);
        Assert.All(facts.Facts, f =>
        {
            Assert.Equal(ConfidenceLevel.Authoritative, f.Confidence);
            Assert.Equal(AuthoritySource.ContainerPathPrefix, f.Source);
            Assert.Equal(WritableSourceKind.Path, f.WritableSource);
            Assert.NotNull(f.WritableSourcePath);
            Assert.NotEmpty(f.WritableSourcePath);
        });
    }

    [Fact]
    public void Persona_provider_extracts_facts_from_lang_file_name()
    {
        var context = CreateContext(langFiles: new[]
        {
            LangFile("PersonalityVoiceDlg/Voice_First_10201.json", 5),
            LangFile("PersonalityVoiceDlg/Voice_First_10202.json", 3),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        Assert.Contains(facts.Facts, f =>
            f.Source == AuthoritySource.LangFileName &&
            f.WritableSource == WritableSourceKind.Path &&
            f.WritableSourcePath == "PersonalityVoiceDlg/Voice_First_10201.json");
    }

    [Fact]
    public void Facts_have_writable_source_for_mod_export()
    {
        var context = CreateContext(assets: new[]
        {
            Asset("/Prefab/SD/Personality/10201_yisang_LCBAppearance.prefab"),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        Assert.All(facts.Facts, f =>
        {
            Assert.Equal(WritableSourceKind.Path, f.WritableSource);
            Assert.NotNull(f.WritableSourcePath);
            Assert.NotEmpty(f.WritableSourcePath);
        });
    }

    [Fact]
    public void Unknown_category_returns_empty_facts()
    {
        var context = CreateContext();
        var engine = new WikiPageAuthorityEngine();

        var facts = engine.Extract("unknown:12345", context);
        Assert.Empty(facts.Facts);
    }

    [Fact]
    public void Negative_five_digit_number_in_ui_path_not_treated_as_id()
    {
        // 负例：UI 图标路径里的 5 位数字不得当 id
        var context = CreateContext(assets: new[]
        {
            Asset("/Assets/Art/UI/Icon/10201.png", AssetType.Texture, 500),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        // 不应该有任何事实（UI 图标路径不是权威来源）
        Assert.Empty(facts.Facts);
    }

    [Fact]
    public void Negative_passive_id_seven_digits_not_split_into_windows()
    {
        // 负例：静态表里的 7 位被动 id 不应被拆成多个 4-5 位窗口
        var context = CreateContext(staticTables: new[]
        {
            new RelationStaticFact("container/1010101.json", "Passive_1010101", "Passives", null, 100)
            {
                SizeBytes = 100,
            },
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        // 1010101 是 7 位被动 id，不是人格 10201 的外键
        // 新引擎不应产出任何事实（因为 characterId 不是 10201）
        Assert.DoesNotContain(facts.Facts, f => f.Source == AuthoritySource.StaticTableForeignKey);
    }

    [Fact]
    public void Missing_data_degrades_gracefully()
    {
        var context = CreateContext();
        var engine = new WikiPageAuthorityEngine();

        // 空上下文不应抛出异常
        var facts = engine.Extract("persona:10201", context);
        Assert.Empty(facts.Facts);
    }

    [Fact]
    public void Authoritative_only_filter_excludes_derived()
    {
        var context = CreateContext(assets: new[]
        {
            Asset("/Prefab/SD/Personality/10201_yisang_LCBAppearance.prefab", AssetType.GameObject),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        var authoritative = facts.AuthoritativeOnly;
        Assert.NotEmpty(authoritative);
        Assert.All(authoritative, f => Assert.Equal(ConfidenceLevel.Authoritative, f.Confidence));
    }

    [Fact]
    public void WritableSource_Unknown_means_not_yet_analyzed()
    {
        // WritableSourceKind.Unknown = 未知/尚未分析出出处（待办）
        var fact = new AuthorityFact("persona:10201", "text", "some/ref",
            AuthoritySource.LangFileName, ConfidenceLevel.Authoritative, WritableSourceKind.Unknown);

        Assert.Equal(WritableSourceKind.Unknown, fact.WritableSource);
    }

    [Fact]
    public void WritableSource_None_means_readable_but_not_writable()
    {
        // WritableSourceKind.None = 可读但不可写（已确认无可写出处）
        var fact = new AuthorityFact("persona:10201", "spine", "some/prefab",
            AuthoritySource.PrefabReferenceChain, ConfidenceLevel.Authoritative, WritableSourceKind.None);

        Assert.Equal(WritableSourceKind.None, fact.WritableSource);
    }

    [Fact]
    public void WritableSource_Path_means_writable()
    {
        // WritableSourceKind.Path = 有可写出处
        var fact = new AuthorityFact("persona:10201", "text", "some/ref",
            AuthoritySource.LangFileName, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
        {
            WritableSourcePath = "PersonalityVoiceDlg/10201.json",
        };

        Assert.Equal(WritableSourceKind.Path, fact.WritableSource);
        Assert.Equal("PersonalityVoiceDlg/10201.json", fact.WritableSourcePath);
    }

    [Fact]
    public void WritableSource_three_states_are_distinguishable()
    {
        // 验证三种状态是可区分的
        var unknown = new AuthorityFact("persona:10201", "text", "ref1",
            AuthoritySource.LangFileName, ConfidenceLevel.Authoritative, WritableSourceKind.Unknown);
        var none = new AuthorityFact("persona:10201", "spine", "ref2",
            AuthoritySource.PrefabReferenceChain, ConfidenceLevel.Authoritative, WritableSourceKind.None);
        var path = new AuthorityFact("persona:10201", "audio", "ref3",
            AuthoritySource.BankSampleExactMatch, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
        {
            WritableSourcePath = "some/path",
        };

        Assert.Equal(WritableSourceKind.Unknown, unknown.WritableSource);
        Assert.Equal(WritableSourceKind.None, none.WritableSource);
        Assert.Equal(WritableSourceKind.Path, path.WritableSource);

        // 验证它们互不相等
        Assert.NotEqual(unknown.WritableSource, none.WritableSource);
        Assert.NotEqual(none.WritableSource, path.WritableSource);
        Assert.NotEqual(unknown.WritableSource, path.WritableSource);
    }

    [Fact]
    public void Unavailable_types_tracked_separately()
    {
        var context = CreateContext();
        var engine = new WikiPageAuthorityEngine();

        var facts = engine.Extract("persona:10201", context);

        // 空上下文应该产出不可用类型
        Assert.True(facts.UnavailableCount > 0);
        Assert.Contains(facts.Unavailable, u => u.ContentType == "text");
        Assert.Contains(facts.Unavailable, u => u.ContentType == "audio");
    }
}
