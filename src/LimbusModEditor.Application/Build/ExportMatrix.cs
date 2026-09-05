using LimbusModEditor.Domain.Formats;

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
    public static ExportCompatibility Evaluate(ModFormatKind source, ModFormatKind target)
    {
        if (source == ModFormatKind.Unknown || target == ModFormatKind.Unknown)
            return ExportCompatibility.No(source, target, "源格式未知，请先导入有效的源模组。");
        if (source == ModFormatKind.Directory)
            return target switch
            {
                ModFormatKind.Carra or ModFormatKind.Carra2 => ExportCompatibility.Ok(source, target, "从资源目录按 account/bundle/path_id 规则生成对象。"),
                ModFormatKind.Rebank => ExportCompatibility.Ok(source, target, "从资源目录生成 Rebank 差分包。"),
                ModFormatKind.Lunartique => ExportCompatibility.Ok(source, target, "从资源目录生成 Lunartique ZIP。"),
                ModFormatKind.Bank => ExportCompatibility.No(source, target, "Bank 需要原始 RIFF/FEV 结构，无法从普通目录合成。"),
                _ => ExportCompatibility.No(source, target, "不支持的输出格式。")
            };
        if (source == target)
            return ExportCompatibility.Ok(source, target, "同格式导出：应用替换后按原始结构重写。");
        if (IsCarraFamily(source) && IsCarraFamily(target))
            return ExportCompatibility.Ok(source, target, "Carra/Carra2 同族转换：对象级兼容。");
        if (source == ModFormatKind.Lunartique && IsCarraFamily(target))
            return ExportCompatibility.Ok(source, target, "Lunartique → 对象级 Carra/Carra2：通过 Unity 对象载荷哈希对比转换；Installation 中须包含有效 Unity SerializedFile。");
        return ExportCompatibility.No(source, target, $"尚未实现 {source} 到 {target} 的跨格式导出。");
    }

    public static IReadOnlyList<ExportCompatibility> MatrixFor(ModFormatKind source) =>
    [
        Evaluate(source, ModFormatKind.Carra),
        Evaluate(source, ModFormatKind.Carra2),
        Evaluate(source, ModFormatKind.Rebank),
        Evaluate(source, ModFormatKind.Bank),
        Evaluate(source, ModFormatKind.Lunartique)
    ];

    private static bool IsCarraFamily(ModFormatKind kind) => kind is ModFormatKind.Carra or ModFormatKind.Carra2;

    /// <summary>Best-effort source format guess from a file extension (used by
    /// the wizard before a full probe runs).</summary>
    public static ModFormatKind GuessSourceKind(string path)
    {
        if (Directory.Exists(path)) return ModFormatKind.Directory;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".carra" => ModFormatKind.Carra,
            ".carra2" => ModFormatKind.Carra2,
            ".rebank" => ModFormatKind.Rebank,
            ".bank" => ModFormatKind.Bank,
            ".zip" => ModFormatKind.Lunartique,
            _ => ModFormatKind.Unknown
        };
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
