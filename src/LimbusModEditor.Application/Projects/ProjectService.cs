using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Projects;

public interface IProjectService
{
    Task<ModProject> CreateAsync(string directory, string name, CancellationToken cancellationToken = default);
    Task<ModProject> LoadAsync(string projectFile, CancellationToken cancellationToken = default);
    Task SaveAsync(ModProject project, string projectFile, CancellationToken cancellationToken = default);
}

public sealed class ProjectService : IProjectService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new SkipReferenceAssetsConverter(), new JsonStringEnumConverter() }
    };

    public async Task<ModProject> CreateAsync(string directory, string name, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        foreach (var child in new[] { "sources", "workspace", "edits", "previews", "builds", "backups", "logs" })
            Directory.CreateDirectory(Path.Combine(root, child));
        var project = new ModProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Mod" : name.Trim(),
            SourceDirectory = Path.Combine(root, "sources")
        };
        await SaveAsync(project, Path.Combine(root, $"{SanitizeFileName(project.Name)}.lmeproj"), cancellationToken);
        return project;
    }

    public async Task<ModProject> LoadAsync(string projectFile, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(projectFile));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var version = document.RootElement.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 1;
        if (version > ModProject.CurrentSchemaVersion)
            throw new InvalidDataException($"项目文件版本 {version} 高于当前编辑器支持的版本 {ModProject.CurrentSchemaVersion}。");
        var project = document.RootElement.Deserialize<ModProject>(Options)
            ?? throw new InvalidDataException("项目文件为空或格式无效。");
        project.SchemaVersion = ModProject.CurrentSchemaVersion;
        return project;
    }

    public async Task SaveAsync(ModProject project, string projectFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = Path.GetFullPath(projectFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        project.SchemaVersion = ModProject.CurrentSchemaVersion;
        var temp = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, project, Options, cancellationToken);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}

/// <summary>阶段 C 项目瘦身：扫描得到的「纯引用资产」（Metadata["reference"]
/// = true，指向游戏缓存只读数据）100% 可由扫描索引（unity-cache-index.db）
/// 重建，不值得每次保存都全量序列化——真实全缓存项目 119 万引用资产曾把
/// 项目文件撑到 1.6GB（保存 12s / 打开 65s）。写入时跳过它们；读取不过滤
/// （旧格式项目文件里的引用资产照常加载，回到内存后行为不变）。</summary>
public sealed class SkipReferenceAssetsConverter : JsonConverter<ObservableCollection<AssetRecord>>
{
    public override ObservableCollection<AssetRecord>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
        var collection = new ObservableCollection<AssetRecord>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            var item = JsonSerializer.Deserialize(ref reader, typeof(AssetRecord), options) as AssetRecord;
            if (item is not null) collection.Add(item);
            reader.Read();
        }
        return collection;
    }

    public override void Write(Utf8JsonWriter writer, ObservableCollection<AssetRecord> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (IsPureReference(item)) continue;
            JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }

    internal static bool IsPureReference(AssetRecord item)
        => item.Metadata.TryGetValue("reference", out var reference) &&
           string.Equals(reference, "true", StringComparison.OrdinalIgnoreCase);
}
