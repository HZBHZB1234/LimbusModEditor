using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Lunartique;

public sealed class LunartiqueFormatHandler : IModFormatHandler
{
    public FormatDescriptor Descriptor { get; } = new(ModFormatKind.Lunartique, "Lunartique", [".zip"], true, true);

    public ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (input.CanSeek) input.Position = 0;
        var match = LunartiqueArchive.IsMatch(input);
        return ValueTask.FromResult(new FormatProbeResult(ModFormatKind.Lunartique, match, match ? 95 : 0,
            match ? "检测到 Lunartique 安装/卸载资源" : "不是 Lunartique 包", []));
    }

    public Task<ModPackage> ImportAsync(Stream input, ImportContext context)
    {
        var payload = LunartiqueArchive.Read(input, context.PreserveUnknownFiles);
        var package = new ModPackage { SourceFormat = ModFormatKind.Lunartique, Payload = payload };
        var changed = LunartiqueArchive.ChangedResources(payload).ToHashSet();
        foreach (var resource in payload.Resources)
            package.Project.Assets.Add(new LimbusModEditor.Domain.Assets.AssetRecord
            {
                LogicalPath = resource.RelativePath,
                ContainerPath = resource.RelativePath,
                Type = LimbusModEditor.Domain.Assets.AssetType.Binary,
                Size = resource.Installation.LongLength,
                EditState = resource.Uninstallation.Length == 0
                    ? LimbusModEditor.Domain.Assets.AssetEditState.Added
                    : resource.Installation.Length == 0
                        ? LimbusModEditor.Domain.Assets.AssetEditState.Deleted
                        : changed.Contains(resource)
                            ? LimbusModEditor.Domain.Assets.AssetEditState.Modified
                            : LimbusModEditor.Domain.Assets.AssetEditState.Unchanged
            });
        package.PreservedFiles.AddRange(payload.PreservedFiles.Select(x => x.Path));
        return Task.FromResult(package);
    }

    public Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default)
    {
        var report = new ValidationReport();
        if (package.Payload is not LunartiquePackage payload)
            report.Diagnostics.Add(new(DiagnosticSeverity.Error, "LUNARTIQUE_PAYLOAD", "无效的 Lunartique 数据模型。"));
        else if (payload.Resources.Count == 0)
            report.Diagnostics.Add(new(DiagnosticSeverity.Warning, "LUNARTIQUE_EMPTY", "包中没有成对的 Installation/Uninstallation 资源。"));
        return Task.FromResult(report);
    }

    public Task ExportAsync(ModPackage package, Stream output, ExportContext context)
    {
        if (package.Payload is not LunartiquePackage payload)
            throw new InvalidDataException("无效的 Lunartique 数据模型。");
        LunartiqueArchive.Write(payload, output, context.PreserveUnknownFiles);
        return Task.CompletedTask;
    }
}
