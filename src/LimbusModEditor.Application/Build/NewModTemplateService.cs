using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Rebank;

namespace LimbusModEditor.Application.Build;

public sealed record NewModTemplateRequest(
    string Name,
    string Version,
    string Author,
    string Description,
    ModFormatKind Format,
    string? BaseBank = null);

public sealed record NewModTemplateResult(
    string ProjectDirectory,
    string ProjectFile,
    string TemplateFile,
    IReadOnlyList<string> Diagnostics);

/// <summary>P3.1: scaffolds a new mod project and generates a minimal,
/// handler-validated package template for the chosen format. Templates are
/// only offered where a minimal legal package can be honestly produced:
/// Carra2 (empty entry list) and Rebank (rebank.json with a declared
/// base_bank). Lunartique needs a real mod as the base and offers no empty
/// template.</summary>
public sealed class NewModTemplateService
{
    private readonly IProjectService _projects;

    public NewModTemplateService(IProjectService projects) => _projects = projects;

    public static IReadOnlyList<(ModFormatKind Kind, string DisplayName, string Extension, string? UnavailableReason)> SupportedTemplates() =>
    [
        (ModFormatKind.Carra2, "Carra / Carra2", ".carra2", null),
        (ModFormatKind.Rebank, "Rebank 音频差分", ".rebank", null),
        (ModFormatKind.Lunartique, "Lunartique", ".zip",
            "Lunartique 模组由现有 zip 生成的资源目录构成，没有真实模组作为基底时无法生成有意义的空模板；请先导入一个现有 Lunartique 模组。")
    ];


    public async Task<NewModTemplateResult> CreateAsync(NewModTemplateRequest request, string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        var name = Sanitize(request.Name);
        if (request.Format is not (ModFormatKind.Carra2 or ModFormatKind.Rebank))
            throw new NotSupportedException("该格式没有可生成的空白模板；请选择 Carra/Carra2 或 Rebank。");
        if (request.Format is ModFormatKind.Rebank && string.IsNullOrWhiteSpace(request.BaseBank))
            throw new InvalidDataException("Rebank 模板必须声明 base_bank（目标游戏 .bank 文件名）。");

        var project = await _projects.CreateAsync(directory, name, cancellationToken);
        var projectFile = Path.Combine(Path.GetFullPath(directory), $"{name}.lmeproj");
        var extension = request.Format is ModFormatKind.Carra2 ? ".carra2" : ".rebank";
        var templateFile = Path.Combine(Path.GetFullPath(directory), $"{name}{extension}");

        var package = request.Format is ModFormatKind.Carra2
            ? new ModPackage
            {
                SourceFormat = ModFormatKind.Carra2,
                Payload = new CarraPackage(),
                Project = project
            }
            : new ModPackage
            {
                SourceFormat = ModFormatKind.Rebank,
                Payload = RebankDiffService.Create(request.BaseBank!.Trim(), name,
                    string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
                    string.IsNullOrWhiteSpace(request.Author) ? "unknown" : request.Author.Trim(),
                    string.IsNullOrWhiteSpace(request.Description) ? string.Empty : request.Description.Trim(),
                    []),
                Project = project
            };

        var handler = BuiltInFormatRegistry.Create().Handlers
            .FirstOrDefault(x => x.Descriptor.Kind == request.Format)
            ?? throw new InvalidOperationException("未找到所选格式的处理器。");
        var validation = await handler.ValidateAsync(package, cancellationToken);
        if (!validation.IsValid)
            throw new InvalidDataException("模板未通过格式校验：\n" + string.Join("\n",
                validation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code}: {d.Message}")));
        var diagnostics = validation.Diagnostics
            .Select(d => $"{d.Severity}: {d.Message}")
            .ToArray();

        await AtomicOutput.WriteAsync(templateFile, async (stream, token) =>
        {
            await handler.ExportAsync(package, stream, new ExportContext(
                request.Format, PreserveUnknownFiles: true, ValidateBeforeExport: false, token, new JovelerXzCodec()));
        }, cancellationToken);

        project.Version = string.IsNullOrWhiteSpace(request.Version) ? "0.1.0" : request.Version.Trim();
        project.Author = string.IsNullOrWhiteSpace(request.Author) ? string.Empty : request.Author.Trim();
        project.Description = string.IsNullOrWhiteSpace(request.Description) ? string.Empty : request.Description.Trim();
        if (request.Format is ModFormatKind.Rebank)
            project.Description = string.IsNullOrEmpty(project.Description)
                ? $"Rebank 模板（base_bank: {request.BaseBank!.Trim()}）"
                : project.Description;
        await _projects.SaveAsync(project, projectFile, cancellationToken);

        return new NewModTemplateResult(Path.GetFullPath(directory), projectFile, templateFile, diagnostics);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "MyMod" : cleaned;
    }
}
