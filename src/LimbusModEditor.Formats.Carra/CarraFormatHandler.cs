using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using NLog;

namespace LimbusModEditor.Formats.Carra;

public sealed class CarraFormatHandler : IModFormatHandler
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public FormatDescriptor Descriptor { get; } = new(ModFormatKind.Carra2, "Carra / Carra2", [".carra", ".carra2"], true, true);

    public async ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (Log.IsDebugEnabled) Log.Debug($"Carra 探测入口：{fileName ?? "-"}（输入流可定位：{input.CanSeek}）");
        if (input.CanSeek) input.Position = 0;
        return await Task.Run(() => CarraArchive.Probe(input, fileName), cancellationToken);
    }

    public async Task<ModPackage> ImportAsync(Stream input, ImportContext context)
    {
        using var scope = Log.Scope("Carra 导入");
        var payload = await Task.Run(() => CarraArchive.Read(input, context.PreserveUnknownFiles), context.CancellationToken);
        if (Log.IsDebugEnabled) Log.Debug("Carra 导入：保留未知文件 {0}", context.PreserveUnknownFiles);
        var package = new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = payload };
        foreach (var entry in payload.Entries)
        {
            package.Project.Assets.Add(new AssetRecord
            {
                LogicalPath = entry.Key.LogicalPath,
                ContainerPath = entry.SourcePath,
                // Field-name caveat (kept for key round-trip): the "Account"
                // segment is really the Unity cache OUTER key (a 32-hex
                // directory name), not a player account, and the trailing
                // ".<typeIdx>" is the target SerializedFile's TYPE TABLE index
                // (verified against LimbusModLoader's patch.py:287-301), not a
                // global Unity class ID. Without the target bundle the real
                // class cannot be resolved, so imported objects surface as
                // Binary instead of pretending to know better.
                Account = entry.Key.Account,
                Bundle = entry.Key.Bundle,
                UnityPathId = entry.Key.PathId,
                UnityTypeId = entry.Key.TypeId,
                Type = AssetType.Binary,
                Size = entry.CompressedData.LongLength
            });
        }
        foreach (var unknown in payload.UnknownFiles) package.PreservedFiles.Add(unknown.Path);
        Log.Info("Carra 导入完成：对象条目 {0} 个，保留未知文件 {1} 个", package.Project.Assets.Count, package.PreservedFiles.Count);
        return package;
    }

    public Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default)
    {
        var report = new ValidationReport();
        if (package.Payload is not CarraPackage payload)
        {
            Log.Warn("Carra 校验不通过：CARRA_PAYLOAD，负载类型为 {0}", package.Payload?.GetType().Name ?? "-");
            report.Diagnostics.Add(new(DiagnosticSeverity.Error, "CARRA_PAYLOAD", "无效的 Carra 数据模型。"));
        }
        else if (payload.Entries.Count == 0)
        {
            Log.Warn("Carra 校验警告：CARRA_EMPTY，包内没有对象条目");
            report.Diagnostics.Add(new(DiagnosticSeverity.Warning, "CARRA_EMPTY", "Carra 包中没有对象条目。"));
        }
        else if (Log.IsDebugEnabled)
        {
            Log.Debug("Carra 校验通过：对象条目 {0} 个", payload.Entries.Count);
        }
        return Task.FromResult(report);
    }

    public Task ExportAsync(ModPackage package, Stream output, ExportContext context)
    {
        using var scope = Log.Scope("Carra 导出");
        if (package.Payload is not CarraPackage payload)
        {
            Log.Error("Carra 导出失败：CARRA_PAYLOAD，负载类型为 {0}", package.Payload?.GetType().Name ?? "-");
            throw new InvalidDataException("无效的 Carra 数据模型。");
        }
        var codec = context.Codec as IXzCodec;
        Log.Info("Carra 导出开始：对象条目 {0} 个，保留未知文件 {1}，XZ 编码器 {2}",
            payload.Entries.Count, context.PreserveUnknownFiles, codec is null ? "未提供" : "已提供");
        CarraArchive.Write(payload, output, context.PreserveUnknownFiles,
            data => CarraCodec.EncodeOrThrow(data, codec));
        Log.Info("Carra 导出完成：对象条目 {0} 个", payload.Entries.Count);
        return Task.CompletedTask;
    }
}
