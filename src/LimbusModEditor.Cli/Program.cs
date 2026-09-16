using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Diagnostics;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Cli;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

// 日志系统必须最早装配：命令行参数、项目路径、每一步耗时与异常都进 logs/。
var logging = NLogBootstrap.Initialize(
    AppContext.BaseDirectory,
    new LogHostInfo("LimbusModEditor.Cli", typeof(NLogBootstrap).Assembly.GetName().Version?.ToString()));
CliLog.Log.Info("CLI 启动：参数 [{0}]；日志目录 {1}", string.Join(' ', args), logging.Directory ?? "(未落盘)");

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Limbus Mod Editor CLI");
    Console.WriteLine("  probe <package>                          Detect package format");
    Console.WriteLine("  import <project.lmeproj> <file-or-dir>    Import package, bundle or directory");
    Console.WriteLine("  export <project.lmeproj> <output> [src]   Export project edits");
    Console.WriteLine("  export-mod <project.lmeproj> <targetDir>  Headless 导出模组（与 GUI 同链路；用于真实数据性能/取消排查）");
    Console.WriteLine("  wiki-generate <project.lmeproj> [pageId] [out.json]");
    Console.WriteLine("                                           Headless 生成维基页面（与 Wiki 首页按钮同链路）；");
    Console.WriteLine("                                           给 pageId（可逗号分隔多个）时把该页完整内容（含权威标注）导出到 out.json");
    Console.WriteLine("                                           环境变量 LME_BASE=<程序目录> 指定共享配置/缓存目录");
    Console.WriteLine("  logs                                      Print the log directory");
    NLogBootstrap.Shutdown();
    return;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "probe" when args.Length >= 2:
            await ProbeAsync(args[1]);
            break;
        case "import" when args.Length >= 3:
            await ImportAsync(args[1], args[2]);
            break;
        case "export" when args.Length >= 3:
            await ExportAsync(args[1], args[2], args.Length >= 4 ? args[3] : null);
            break;
        case "export-mod" when args.Length >= 3:
            Environment.ExitCode = await ExportModCommand.RunAsync(
                args[1],
                args[2],
                Environment.GetEnvironmentVariable("LME_BASE"),
                int.TryParse(Environment.GetEnvironmentVariable("LME_EXPORT_TIMEOUT_S"), out var timeoutSeconds)
                    ? Math.Max(0, timeoutSeconds)
                    : 0);
            break;
        case "wiki-generate" when args.Length >= 2:
            Environment.ExitCode = await WikiGenerateCommand.RunAsync(
                args[1],
                Environment.GetEnvironmentVariable("LME_BASE"),
                args.Length >= 3 ? args[2] : null,
                args.Length >= 4 ? args[3] : null);
            break;
        case "logs":
            Console.WriteLine(logging.CurrentFile ?? logging.Describe());
            break;
        default:
            CliLog.Log.Warn("无法识别的参数：{0}", string.Join(' ', args));
            Console.Error.WriteLine("Invalid arguments. Run 'help' for usage.");
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex)
{
    CliLog.Log.Error(ex, "CLI 命令失败：{0}", string.Join(' ', args));
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

NLogBootstrap.Shutdown();

static async Task ProbeAsync(string path)
{
    using var scope = CliLog.Log.Scope($"probe {path}");
    await using var input = File.OpenRead(Path.GetFullPath(path));
    var result = await BuiltInFormatRegistry.Create().ProbeAsync(input, Path.GetFileName(path));
    CliLog.Log.Info("probe 结果：{0}（{1}%）", result?.Format.ToString() ?? "Unknown", result?.Confidence);
    Console.WriteLine(result is null ? "Unknown" : $"{result.Format}: {result.Confidence}% - {result.Description}");
}

static async Task ImportAsync(string projectFile, string inputPath)
{
    using var scope = CliLog.Log.Scope($"import {inputPath} → {projectFile}");
    var projects = new ProjectService();
    var project = await projects.LoadAsync(projectFile);
    var importer = new ModImportService(BuiltInFormatRegistry.Create());
    var result = Directory.Exists(inputPath)
        ? await importer.ImportDirectoryIntoProjectAsync(inputPath, project)
        : Path.GetExtension(inputPath).Equals(".bundle", StringComparison.OrdinalIgnoreCase)
            ? await importer.ImportUnityBundleIntoProjectAsync(inputPath, project)
            : await importer.ImportIntoProjectAsync(inputPath, project);
    await projects.SaveAsync(project, projectFile);
    CliLog.Log.Info("import 完成：格式 {0}，新增 {1}，更新 {2}", result.Format, result.AddedAssets, result.UpdatedAssets);
    Console.WriteLine($"Imported {result.Format}: added={result.AddedAssets}, updated={result.UpdatedAssets}");
}

static async Task ExportAsync(string projectFile, string outputPath, string? source)
{
    using var scope = CliLog.Log.Scope($"export {projectFile} → {outputPath}");
    var project = await new ProjectService().LoadAsync(projectFile);
    var result = await new ModExportService(BuiltInFormatRegistry.Create())
        .ExportWithEditsAsync(source, project, outputPath);
    CliLog.Log.Info("export 完成：{0}，替换 {1} 处", result.OutputPath, result.AppliedReplacements);
    Console.WriteLine($"Exported {result.Format}: {result.OutputPath}, replacements={result.AppliedReplacements}");
}

/// <summary>CLI 日志器（顶层语句里没有字段，用一个静态类承载；类型声明必须放在顶层语句之后）。</summary>
internal static class CliLog
{
    internal static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli");
}
