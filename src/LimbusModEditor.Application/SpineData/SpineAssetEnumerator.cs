using LimbusModEditor.Application.Relations;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 集合枚举结果。
/// </summary>
public sealed class SpineSetInfo
{
    /// <summary>目录路径（容器路径的目录部分）。</summary>
    public required string Folder { get; init; }
    
    /// <summary>骨架文件信息（JSON 或 PSB）。</summary>
    public SpineAssetInfo? Skeleton { get; init; }
    
    /// <summary>图集文本文件信息。</summary>
    public SpineAssetInfo? Atlas { get; init; }
    
    /// <summary>纹理页文件信息列表。</summary>
    public List<SpineAssetInfo> Textures { get; init; } = new();
    
    /// <summary>是否为完整三件套（骨架 + atlas + 纹理）。</summary>
    public bool IsComplete => Skeleton is not null && Atlas is not null && Textures.Count > 0;
}

/// <summary>
/// 扩展的 Spine 资产定位器，支持枚举和按索引定位。
/// </summary>
internal sealed class SpineAssetEnumerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _dbPath;

    public SpineAssetEnumerator(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>枚举所有完整的 Spine 三件套集合。</summary>
    public IReadOnlyList<SpineSetInfo> EnumerateCompleteSets(CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            
            // 查询所有 Spine 路径相关的 TextAsset/JSON/Texture 对象
            // 扩展判据：除既有 4 条外，增加 StorySpine_* 目录（如 StorySpine_Sinclair 在 /Story/CG/ 下）
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT a.path_id, a.type_id, a.size, a.container_entry, b.data_path, s.value as container_name
                FROM assets a
                JOIN bundles b ON a.bundle_id = b.id
                LEFT JOIN strings s ON a.container_id = s.id
                WHERE a.container_entry IS NOT NULL
                  AND (a.container_entry LIKE '%/SpineIllustPrefab/%'
                    OR a.container_entry LIKE '%.psb'
                    OR a.container_entry LIKE '%SkeletonData%'
                    OR a.container_entry LIKE '%/Story/Spine/%'
                    OR a.container_entry LIKE '%StorySpine%')
                  AND a.type_id IN (49, 28, 213)
                ORDER BY a.container_entry";
            
            var results = new List<SpineAssetInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new SpineAssetInfo(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                ));
            }

            // 按目录分组
            var folderMap = new Dictionary<string, List<SpineAssetInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in results)
            {
                var folder = SpinePathRules.FolderOf(item.ContainerEntry);
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (!folderMap.TryGetValue(folder, out var list))
                    folderMap[folder] = list = new List<SpineAssetInfo>();
                list.Add(item);
            }

            // 组装三件套
            var sets = new List<SpineSetInfo>();
            foreach (var (folder, items) in folderMap)
            {
                SpineAssetInfo? skeleton = null;
                SpineAssetInfo? atlas = null;
                var textures = new List<SpineAssetInfo>();
                
                foreach (var item in items)
                {
                    var name = Path.GetFileName(item.ContainerEntry);
                    if ((name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase))
                        || name.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
                    {
                        skeleton = item;
                    }
                    else if (name.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        atlas = item;
                    }
                    else if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        textures.Add(item);
                    }
                }
                
                if (skeleton is not null && atlas is not null && textures.Count > 0)
                {
                    sets.Add(new SpineSetInfo 
                    { 
                        Folder = folder, 
                        Skeleton = skeleton, 
                        Atlas = atlas, 
                        Textures = textures 
                    });
                }
            }

            Log.Info("枚举 Spine 集合完成：{0} 个完整三件套（共 {1} 个目录）", sets.Count, folderMap.Count);
            return sets;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "枚举 Spine 集合失败");
            return [];
        }
    }

    /// <summary>按目录前缀查找骨架集合。</summary>
    public IReadOnlyList<SpineSetInfo> FindByFolderPrefix(string folderPrefix, CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT a.path_id, a.type_id, a.size, a.container_entry, b.data_path, s.value as container_name
                FROM assets a
                JOIN bundles b ON a.bundle_id = b.id
                LEFT JOIN strings s ON a.container_id = s.id
                WHERE a.container_entry IS NOT NULL
                  AND a.container_entry LIKE $prefix || '%'
                  AND a.type_id IN (49, 28, 213)
                ORDER BY a.container_entry";
            cmd.Parameters.AddWithValue("$prefix", folderPrefix);
            
            var results = new List<SpineAssetInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new SpineAssetInfo(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                ));
            }

            // 按目录分组并组装三件套
            var folderMap = new Dictionary<string, List<SpineAssetInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in results)
            {
                var folder = SpinePathRules.FolderOf(item.ContainerEntry);
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (!folderMap.TryGetValue(folder, out var list))
                    folderMap[folder] = list = new List<SpineAssetInfo>();
                list.Add(item);
            }

            var sets = new List<SpineSetInfo>();
            foreach (var (folder, items) in folderMap)
            {
                SpineAssetInfo? skeleton = null;
                SpineAssetInfo? atlas = null;
                var textures = new List<SpineAssetInfo>();
                
                foreach (var item in items)
                {
                    var name = Path.GetFileName(item.ContainerEntry);
                    if ((name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase))
                        || name.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
                    {
                        skeleton = item;
                    }
                    else if (name.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        atlas = item;
                    }
                    else if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        textures.Add(item);
                    }
                }
                
                sets.Add(new SpineSetInfo 
                { 
                    Folder = folder, 
                    Skeleton = skeleton, 
                    Atlas = atlas, 
                    Textures = textures 
                });
            }

            return sets;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "按前缀查找 Spine 集合失败：{0}", folderPrefix);
            return [];
        }
    }
}
