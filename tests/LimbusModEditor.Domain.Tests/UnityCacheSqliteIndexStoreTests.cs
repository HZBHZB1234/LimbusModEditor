using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Domain.Tests;

public sealed class UnityCacheSqliteIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-index-v2-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.db");
    private static UnityCacheIndexBundle Bundle(string name) => new(name, 123, 456, "outer", name, true);
    private static UnityCacheScanEntry Entry(string name) => new("outer", name, name);
    private static UnityCacheIndexRow Row(int index, string? baseline = "基线") =>
        new(index, "共享容器", long.MaxValue - index, 49, AssetType.Text, 9876543210, baseline, index == 0 ? "assets/中文.json" : null);

    [Fact]
    public void Roundtrip_preserves_empty_bundles_nulls_large_ids_and_shared_strings()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(1, null), Row(0) }), (Bundle("b"), Array.Empty<UnityCacheIndexRow>())]);
        var batches = store.ReadAll().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(Bundle("a"), batches[0].Bundle);
        Assert.Equal(new[] { Row(0), Row(1, null) }, batches[0].Rows);
        Assert.Same(batches[0].Rows[0].Container, batches[0].Rows[1].Container);
        Assert.Empty(batches[1].Rows);
        Assert.Single(store.ReadContainerRows());
        Assert.Equal("b", Assert.Single(store.ReadAll(new HashSet<string> { "b" })).Bundle.DataPath);
    }

    [Fact]
    public void Failed_replacement_rolls_back_rows_metadata_and_pruning()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        Assert.Throws<SqliteException>(() => store.PersistAll([Entry("a")],
            [(Bundle("a") with { Size = 999 }, new[] { Row(0), Row(0) })]));
        var all = store.ReadAll().ToArray();
        Assert.Equal(2, all.Length);
        Assert.Equal(123, all[0].Bundle.Size);
        Assert.Equal(Row(0), Assert.Single(all[0].Rows));
        store.PersistAll([Entry("A")], [(Bundle("A"), new[] { Row(2) })]);
        Assert.Equal(Row(2), Assert.Single(Assert.Single(store.ReadAll()).Rows));
    }

    [Fact]
    public void Streaming_reader_keeps_one_snapshot_while_another_store_updates()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        using (var iterator = store.ReadAll().GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            new UnityCacheSqliteIndexStore(Database).PersistAll([Entry("a"), Entry("b")], [(Bundle("b"), new[] { Row(2) })]);
            Assert.True(iterator.MoveNext());
            Assert.Equal(Row(1), Assert.Single(iterator.Current.Rows));
        }
        Assert.Equal(Row(2), Assert.Single(store.ReadAll().Last().Rows));
    }

    [Fact]
    public async Task Rehydrate_remaps_known_ids_but_preserves_type_tree_classifications()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[]
        {
            Row(0) with { TypeId = 28, Type = AssetType.Unknown },
            Row(1) with { TypeId = int.MaxValue, Type = AssetType.SpriteAtlas }
        })]);
        var project = new ModProject();
        Assert.Equal(2, await new UnityCacheScanService(Database).RehydrateFromIndexAsync(project));
        Assert.Equal(new[] { AssetType.Texture, AssetType.SpriteAtlas }, project.Assets.Select(x => x.Type));
    }

    [Fact]
    public void Cancellation_rolls_back_pruning_and_updates()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> Changes()
        {
            yield return (Bundle("a"), new[] { Row(2) });
            cancellation.Cancel();
        }
        Assert.Throws<OperationCanceledException>(() => store.PersistAll([Entry("a")], Changes(), cancellation.Token));
        Assert.Equal(2, store.ReadAll().Count());
        Assert.Equal(Row(0), Assert.Single(store.ReadAll().First().Rows));
    }

    [Fact]
    public void Missing_asset_table_invalidates_bundle_freshness()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE assets";
            command.ExecuteNonQuery();
        }
        Assert.Empty(new UnityCacheSqliteIndexStore(Database).ReadBundleIndex(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Old_schema_is_invalidated_and_recreated()
    {
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE bundles(data_path TEXT); CREATE TABLE assets(data_path TEXT); INSERT INTO bundles VALUES('old');";
            command.ExecuteNonQuery();
        }
        var store = new UnityCacheSqliteIndexStore(Database);
        Assert.Empty(store.ReadAll());
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        Assert.Single(store.ReadAll());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
