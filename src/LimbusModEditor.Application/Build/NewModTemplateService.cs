using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;

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

    /// <summary>Templates only where a minimal legal package can honestly be
    /// produced. An empty Rebank is withdrawn: the real launcher judges a
    /// rebank whose wav count is 0 an error and rolls the install back
    /// (LCTA launcher/bankmod.py:189-198), so scaffolding one would create a
    /// mod that can never apply. Lunartique needs a real mod as the base.</summary>
    public static IReadOnlyList<(ModFormatKind Kind, string DisplayName, string Extension, string? UnavailableReason)> SupportedTemplates() =>
    [
        (ModFormatKind.Carra2, "Carra / Carra2", ".carra2", null),
        (ModFormatKind.Rebank, "Rebank 音频差分", ".rebank",
            "空白 Rebank 会被真实加载器判为错误并回滚安装（至少需要一条可匹配的 .wav）。请先导入现有 .bank 生成音频差分。"),
        (ModFormatKind.Lunartique, "Lunartique", ".zip",
            "Lunartique 模组由现有 zip 生成的资源目录构成，没有真实模组作为基底时无法生成有意义的空模板；请先导入一个现有 Lunartique 模组。")
    ];


    public async Task<NewModTemplateResult> CreateAsync(NewModTemplateRequest request, string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        var name = Sanitize(request.Name);
        if (request.Format is not ModFormatKind.Carra2)
            throw new NotSupportedException(
                request.Format is ModFormatKind.Rebank
                    ? "空白 Rebank 会被真实加载器判错并回滚（bankmod.py:189-198），因此不再提供该模板。"
                    : "该格式没有可生成的空白模板；请选择 Carra/Carra2。");
        if (request.BaseBank is { } baseBank && !IsPlainFileName(baseBank))
            throw new InvalidDataException(
                $"base_bank 必须是不含路径分隔符的纯文件名（与真实加载器 bankmod.py:104-108 一致）：{baseBank}");

        var project = await _projects.CreateAsync(directory, name, cancellationToken);
        var projectFile = Path.Combine(Path.GetFullPath(directory), $"{name}.lmeproj");
        var templateFile = Path.Combine(Path.GetFullPath(directory), $"{name}.carra2");

        var package = new ModPackage
        {
            SourceFormat = ModFormatKind.Carra2,
            Payload = new CarraPackage(),
            Project = project
        };

        var handler = BuiltInFormatRegistry.Create().Handlers
            .FirstOrDefault(x => x.Descriptor.Kind == ModFormatKind.Carra2)
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
                ModFormatKind.Carra2, PreserveUnknownFiles: true, ValidateBeforeExport: false, token, new JovelerXzCodec()));
        }, cancellationToken);

        project.Version = string.IsNullOrWhiteSpace(request.Version) ? "0.1.0" : request.Version.Trim();
        project.Author = string.IsNullOrWhiteSpace(request.Author) ? string.Empty : request.Author.Trim();
        project.Description = string.IsNullOrWhiteSpace(request.Description) ? string.Empty : request.Description.Trim();
        await _projects.SaveAsync(project, projectFile, cancellationToken);

        return new NewModTemplateResult(Path.GetFullPath(directory), projectFile, templateFile, diagnostics);
    }

    /// <summary>Mirrors the real loader's base_bank validation: the value must
    /// be a plain file name (no directory separators, no traversal, non-empty)
    /// because the launcher concatenates it under the game's FMOD directory.</summary>
    private static bool IsPlainFileName(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.IndexOfAny(['/', '\\', ':']) < 0 &&
           !value.Contains("..", StringComparison.Ordinal);

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "MyMod" : cleaned;
    }
}
