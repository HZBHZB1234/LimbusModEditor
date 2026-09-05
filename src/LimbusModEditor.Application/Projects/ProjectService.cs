using System.Text.Json;
using System.Text.Json.Serialization;
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
        Converters = { new JsonStringEnumConverter() }
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
