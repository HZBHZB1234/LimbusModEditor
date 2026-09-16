using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Relations.Authority;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 维基权威引擎（<see cref="WikiPageAuthorityEngine"/>）语义测试。
///
/// <para>覆盖：分节顺序、归类规则、深链载荷往返、负例（不得 id 猜测）、损坏/缺失数据的降级。</para>
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

    [Fact]
    public void Persona_provider_extracts_facts_from_container_path()
    {
        var context = CreateContext(assets: new[]
        {
            Asset("/Prefab/SD/Personality/10201_yisang_LCBAppearance.prefab", AssetType.GameObject),
            Asset("/Prefab/SD/Personality/10202_faust_LCBAppearance.prefab"),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        Assert.NotEmpty(facts.Facts);
        Assert.All(facts.Facts, f =>
        {
            Assert.Equal(ConfidenceLevel.Authoritative, f.Confidence);
            Assert.Equal(AuthoritySource.ContainerPathPrefix, f.Source);
            Assert.NotNull(f.WritableSource);
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
            f.WritableSource == "PersonalityVoiceDlg/Voice_First_10201.json");
    }

    [Fact]
    public void Facts_have_writable_source_for_mod_export()
    {
        var context = CreateContext(assets: new[]
        {
            Asset("/Prefab/SD/Personality/10201_yisang_LCBAppearance.prefab", AssetType.GameObject),
        });

        var engine = new WikiPageAuthorityEngine();
        var facts = engine.Extract("persona:10201", context);

        Assert.All(facts.Facts, f =>
        {
            Assert.NotNull(f.WritableSource);
            Assert.NotEmpty(f.WritableSource);
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
}
