using LimbusModEditor.Domain.Diagnostics;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// 从 unity-cache-index.db 查询 Spine 资源定位信息。
/// 只读，不修改任何游戏数据。
/// </summary>
internal sealed class SpineAssetLocator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _dbPath;

    public SpineAssetLocator(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>按容器路径查询其所在目录的全部资源。</summary>
    internal IReadOnlyList<SpineAssetInfo> FindSiblings(string containerEntry)
    {
        var folder = SpinePathRules.FolderOf(containerEntry);
        if (string.IsNullOrWhiteSpace(folder)) return [];

        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT a.path_id, a.type_id, a.size, a.container_entry, b.data_path, s.value as container_name
                FROM assets a
                JOIN bundles b ON a.bundle_id = b.id
                LEFT JOIN strings s ON a.container_id = s.id
                WHERE a.container_entry IS NOT NULL AND a.container_entry LIKE $folder || '%'
                ORDER BY a.container_entry";
            cmd.Parameters.AddWithValue("$folder", folder);

            var results = new List<SpineAssetInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SpineAssetInfo(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                ));
            }

            // 只返回同目录的（子目录不算）
            return results.Where(r =>
            {
                var entryFolder = SpinePathRules.FolderOf(r.ContainerEntry);
                return string.Equals(entryFolder, folder, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "查询 Spine 资源目录失败：{0}", folder);
            return [];
        }
    }
}

/// <summary>Spine 资源的定位信息（从索引库查出）。</summary>
public sealed record SpineAssetInfo(
    long PathId,
    int TypeId,
    int Size,
    string ContainerEntry,
    string BundleDataPath,
    string ContainerName);
