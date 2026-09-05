using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Carra;

public sealed class CarraFormatHandler : IModFormatHandler
{
    public FormatDescriptor Descriptor { get; } = new(ModFormatKind.Carra2, "Carra / Carra2", [".carra", ".carra2"], true, true);

    public async ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (input.CanSeek) input.Position = 0;
        return await Task.Run(() => CarraArchive.Probe(input, fileName), cancellationToken);
    }

    public async Task<ModPackage> ImportAsync(Stream input, ImportContext context)
    {
        var payload = await Task.Run(() => CarraArchive.Read(input, context.PreserveUnknownFiles), context.CancellationToken);
        var package = new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = payload };
        foreach (var entry in payload.Entries)
        {
            package.Project.Assets.Add(new AssetRecord
            {
                LogicalPath = entry.Key.LogicalPath,
                ContainerPath = entry.SourcePath,
                Account = entry.Key.Account,
                Bundle = entry.Key.Bundle,
                UnityPathId = entry.Key.PathId,
                UnityTypeId = entry.Key.TypeId,
                Type = MapAssetType(entry.Key.TypeId),
                Size = entry.CompressedData.LongLength
            });
        }
        foreach (var unknown in payload.UnknownFiles) package.PreservedFiles.Add(unknown.Path);
        return package;
    }

    public Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default)
    {
        var report = new ValidationReport();
        if (package.Payload is not CarraPackage payload)
            report.Diagnostics.Add(new(DiagnosticSeverity.Error, "CARRA_PAYLOAD", "无效的 Carra 数据模型。"));
        else if (payload.Entries.Count == 0)
            report.Diagnostics.Add(new(DiagnosticSeverity.Warning, "CARRA_EMPTY", "Carra 包中没有对象条目。"));
        return Task.FromResult(report);
    }

    public Task ExportAsync(ModPackage package, Stream output, ExportContext context)
    {
        if (package.Payload is not CarraPackage payload)
            throw new InvalidDataException("无效的 Carra 数据模型。");
        var codec = context.Codec as IXzCodec;
        CarraArchive.Write(payload, output, context.PreserveUnknownFiles,
            data => CarraCodec.EncodeOrThrow(data, codec));
        return Task.CompletedTask;
    }

    private static AssetType MapAssetType(int? typeId) => typeId switch
    {
        1 => AssetType.GameObject,
        28 => AssetType.Texture,
        83 => AssetType.Audio,
        114 => AssetType.MonoBehaviour,
        115 => AssetType.MonoScript,
        128 => AssetType.Font,
        213 => AssetType.Sprite,
        _ => AssetType.Binary
    };
}
