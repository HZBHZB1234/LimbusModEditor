using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using NLog;

namespace LimbusModEditor.Application.Build;

public sealed record BuildResult(string OutputPath, int AppliedEdits, IReadOnlyList<string> Diagnostics);

/// <summary>Builds the project workspace into a game-compatible overlay and
/// provides the common dispatch point for package exporters.</summary>
public sealed class ProjectBuildService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public async Task<BuildResult> BuildOverlayAsync(ModProject project, string projectDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var scope = Log.Scope("构建覆盖层");
        Log.Info("构建覆盖层开始：输出目录 {0}，项目目录 {1}，编辑 {2} 条 / 资产 {3} 个",
            outputDirectory, projectDirectory ?? "-", project.Edits.Count, project.Assets.Count);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var applied = 0;
        var diagnostics = new List<string>();
        // Unity object edits are materialized by UnityBundleBuildService. They
        // must not be copied as ordinary files (their logical path is an
        // object address such as bundle/serialized/pathId.typeId).
        var unityAssetIds = project.Assets
            .Where(x => x.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true")
            .Select(x => x.AssetId)
            .ToHashSet();
        foreach (var edit in project.Edits.Where(x => x.SourcePath is not null && File.Exists(x.SourcePath) &&
                     (!x.AssetId.HasValue || !unityAssetIds.Contains(x.AssetId.Value))))
        {
            // 逐文件复制属热路径：仅 Trace 开启时逐条记录（AtomicOutput 内部已有 Debug 级的字节/耗时）
            if (Log.IsTraceEnabled)
                Log.Trace("正在复制文件级编辑：{0} → {1}（第 {2} 条）", edit.SourcePath ?? "-", edit.TargetPath ?? "-", applied + 1);
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafeCombine(output, edit.TargetPath!);
            await AtomicOutput.CopyAsync(edit.SourcePath!, target, cancellationToken);
            applied++;
        }
        // 被过滤掉的编辑（源文件不存在 / 属于 Unity 对象编辑）静默不复制，这里留痕
        var skipped = project.Edits.Count - applied;
        if (skipped > 0)
            Log.Warn("有 {0} 条编辑未按文件复制（源文件不存在，或属于 Unity 对象编辑——后者由 UnityBundleBuildService 处理）：共 {1} 条",
                skipped, project.Edits.Count);
        diagnostics.Add($"已应用 {applied} 个文件级编辑操作。");
        Log.Info("构建覆盖层完成：复制 {0} 个文件 → {1}（跳过 {2} 条）", applied, output, skipped);
        return new(output, applied, diagnostics);
    }

    public async Task ExportAsync(IModFormatHandler handler, ModPackage package, string outputPath, ExportContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(package);
        Log.Debug("导出包开始：处理器 {0}，格式 {1}，目标 {2}",
            handler.GetType().Name, package.SourceFormat.ToString(), outputPath);
        var exportWatch = System.Diagnostics.Stopwatch.StartNew();
        await AtomicOutput.WriteAsync(outputPath, async (stream, token) =>
        {
            await using var output = stream;
            await handler.ExportAsync(package, output, context with { CancellationToken = token });
        }, cancellationToken);
        Log.Debug("导出包完成：{0}（{1}，输出 {2} 字节，耗时 {3} ms）",
            outputPath, package.SourceFormat.ToString(), File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
            exportWatch.ElapsedMilliseconds);
    }

    public static void ApplyCarraReplacements(CarraPackage package, ModProject project)
    {
        var replaced = 0;
        foreach (var asset in project.Assets)
        {
            if (!asset.Metadata.TryGetValue("replacementPath", out var source) || !File.Exists(source))
            {
                if (asset.Metadata.ContainsKey("replacementPath"))
                    Log.Warn("跳过 carra 替换 {0}：登记的替换文件不存在（{1}）", asset.LogicalPath ?? "-", source ?? "-");
                continue;
            }
            var entry = package.Find(asset.LogicalPath!);
            if (entry is null)
            {
                Log.Warn("跳过 carra 替换 {0}：包里找不到该对象（替换文件 {1}）", asset.LogicalPath ?? "-", source);
                continue;
            }
            var bytes = File.ReadAllBytes(source);
            entry.ReplaceData(bytes);
            replaced++;
            if (Log.IsDebugEnabled)
                Log.Debug("carra 替换生效：{0} ← {1}（{2} 字节）", asset.LogicalPath ?? "-", source, bytes.Length);
        }
        Log.Debug("carra 替换结束：{0} 个对象被替换 / 共 {1} 个资产", replaced, project.Assets.Count);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("构建目标路径超出输出目录。");
        return result;
    }
}
