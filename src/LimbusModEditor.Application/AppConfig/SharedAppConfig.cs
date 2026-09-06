using System.Text.Json;
using System.Text.Json.Serialization;

namespace LimbusModEditor.Application.AppConfig;

/// <summary>A recently opened project recorded in the shared config.</summary>
public sealed record RecentProject(string Path, string Name, DateTimeOffset LastOpenedAt);

/// <summary>
/// 全局共享设置（傻瓜化改造）：游戏目录、Unity 缓存目录、模组目录、FMOD DLL
/// 目录与最近项目保存在 <b>程序目录</b>（config/shared-config.json），
/// 所有项目共用，不再强迫每个 .lmeproj 重复配置。旧项目文件里已有的目录值
/// 会在首次打开时迁移进共享配置（只填充空位，不覆盖）。
/// </summary>
public sealed class SharedAppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? GameDirectory { get; set; }
    public string? UnityCacheDirectory { get; set; }
    public string? ModDirectory { get; set; }

    /// <summary>手动指定的 FMOD DLL 目录。留空时自动按「随包目录 → 程序目录 →
    /// 游戏目录」顺序发现；手动值永远优先且不会被自动发现覆盖。</summary>
    public string? FmodLibraryDirectory { get; set; }

    public string? LastProjectFile { get; set; }
    public List<RecentProject> RecentProjects { get; set; } = [];
}

/// <summary>Loads/saves the shared config atomically; a missing or corrupt
/// file degrades to defaults instead of blocking startup.</summary>
public static class SharedConfigService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SharedAppConfig Load(string configFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        try
        {
            if (!File.Exists(configFile)) return new SharedAppConfig();
            using var document = JsonDocument.Parse(File.ReadAllText(configFile));
            var config = document.RootElement.Deserialize<SharedAppConfig>(Options);
            if (config is null) return new SharedAppConfig();
            config.SchemaVersion = SharedAppConfig.CurrentSchemaVersion;
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置损坏不应阻塞启动：备份坏文件并回到默认值。
            try
            {
                if (File.Exists(configFile)) File.Move(configFile, configFile + ".bad", true);
            }
            catch (Exception) { /* best-effort backup */ }
            return new SharedAppConfig();
        }
    }

    public static void Save(SharedAppConfig config, string configFile)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        var path = Path.GetFullPath(configFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        config.SchemaVersion = SharedAppConfig.CurrentSchemaVersion;
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
