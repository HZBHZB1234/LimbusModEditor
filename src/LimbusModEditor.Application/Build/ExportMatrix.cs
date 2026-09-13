using LimbusModEditor.Domain.Formats;
using NLog;

namespace LimbusModEditor.Application.Build;

/// <summary>One cell of the export compatibility matrix (P3.2): whether a
/// source format can be exported as a target format, and why not when it
/// cannot.</summary>
public sealed record ExportCompatibility(ModFormatKind Source, ModFormatKind Target, bool Supported, string Reason)
{
    public static ExportCompatibility Ok(ModFormatKind source, ModFormatKind target, string reason) => new(source, target, true, reason);
    public static ExportCompatibility No(ModFormatKind source, ModFormatKind target, string reason) => new(source, target, false, reason);
}

/// <summary>Static export capability rules mirroring ModExportService's actual
/// pipeline, used by the export wizard to enable/disable targets up front.</summary>
public static class ExportMatrix
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static ExportCompatibility Evaluate(ModFormatKind source, ModFormatKind target)
    {
        if (source == ModFormatKind.Unknown || target == ModFormatKind.Unknown)
            return Report(ExportCompatibility.No(source, target, "源格式未知，请先导入有效的源模组。"));
        if (source == ModFormatKind.Directory)
            return Report(target switch
            {
                ModFormatKind.Carra or ModFormatKind.Carra2 => ExportCompatibility.Ok(source, target, "从资源目录按 account/bundle/path_id 规则生成对象。"),
                ModFormatKind.Rebank => ExportCompatibility.Ok(source, target, "从资源目录生成 Rebank 差分包。"),
                ModFormatKind.Lunartique => ExportCompatibility.Ok(source, target, "从资源目录生成 Lunartique ZIP。"),
                ModFormatKind.Bank => ExportCompatibility.No(source, target, "Bank 需要原始 RIFF/FEV 结构，无法从普通目录合成。"),
                _ => ExportCompatibility.No(source, target, "不支持的输出格式。")
            });
        if (source == target)
            return Report(ExportCompatibility.Ok(source, target, "同格式导出：应用替换后按原始结构重写。"));
        if (IsCarraFamily(source) && IsCarraFamily(target))
            return Report(ExportCompatibility.Ok(source, target, "Carra/Carra2 同族转换：对象级兼容。"));
        if (source == ModFormatKind.Lunartique && IsCarraFamily(target))
            return Report(ExportCompatibility.Ok(source, target, "Lunartique → 对象级 Carra/Carra2：通过 Unity 对象载荷哈希对比转换；Installation 中须包含有效 Unity SerializedFile。"));
        return Report(ExportCompatibility.No(source, target, $"尚未实现 {source} 到 {target} 的跨格式导出。"));
    }

    public static IReadOnlyList<ExportCompatibility> MatrixFor(ModFormatKind source)
    {
        var rows = new[]
        {
            Evaluate(source, ModFormatKind.Carra),
            Evaluate(source, ModFormatKind.Carra2),
            Evaluate(source, ModFormatKind.Rebank),
            Evaluate(source, ModFormatKind.Bank),
            Evaluate(source, ModFormatKind.Lunartique)
        };
        Log.Debug("导出兼容性矩阵：源 {0}，共 {1} 行，其中支持 {2} 行", source, rows.Length, rows.Count(x => x.Supported));
        return rows;
    }

    private static bool IsCarraFamily(ModFormatKind kind) => kind is ModFormatKind.Carra or ModFormatKind.Carra2;

    /// <summary>Best-effort source format guess from a file extension (used by
    /// the wizard before a full probe runs).</summary>
    public static ModFormatKind GuessSourceKind(string path)
    {
        if (Directory.Exists(path))
        {
            Log.Debug("源格式猜测：{0} → {1}（是目录）", path, ModFormatKind.Directory);
            return ModFormatKind.Directory;
        }
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".carra" => ModFormatKind.Carra,
            ".carra2" => ModFormatKind.Carra2,
            ".rebank" => ModFormatKind.Rebank,
            ".bank" => ModFormatKind.Bank,
            ".zip" => ModFormatKind.Lunartique,
            _ => ModFormatKind.Unknown
        };
        if (kind == ModFormatKind.Unknown)
            Log.Warn("源格式猜测失败：{0} 的扩展名「{1}」不在已知清单（.carra/.carra2/.rebank/.bank/.zip），回退为 Unknown（向导会提示先导入有效源模组）",
                path, Path.GetExtension(path) ?? "-");
        else
            Log.Debug("源格式猜测：{0} → {1}", path, kind);
        return kind;
    }

    private static ExportCompatibility Report(ExportCompatibility result)
    {
        if (result.Supported)
            Log.Debug("导出兼容性结果：{0} → {1} 支持（{2}）", result.Source, result.Target, result.Reason);
        else
            Log.Warn("导出兼容性结果：{0} → {1} 不支持，原因：{2}", result.Source, result.Target, result.Reason);
        return result;
    }
}

/// <summary>Per-asset export outcome for the export report (P3.2).</summary>
public sealed record ExportAssetStatus(string LogicalPath, string Status, string? Note)
{
    public const string Applied = "已应用";
    public const string Skipped = "已跳过";
    public const string Preserved = "保留未知";
    public const string Converted = "已转换";
}
