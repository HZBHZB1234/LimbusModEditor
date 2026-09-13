using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Cli;

/// <summary>
/// 无界面的「导出模组」：与侧边栏「导出模组…」走**同一条链路**
/// （<see cref="ModExportPlanService"/> 分析 → <see cref="ModPackExportService"/> 按槽位写出），
/// 但没有 WPF 消息循环。
///
/// <para>用途：排查「导出很久没成功 / 取消很久不生效」这类只在真实数据下出现的性能与取消问题 ——
/// 命令行能精确测量每一段耗时，日志也落在同一套 <c>logs/</c> 里（<c>current.log</c> 与 <c>errors.log</c>）。
/// 用户界面上的导出行为不受影响。</para>
///
/// <para>环境变量：<c>LME_BASE=&lt;程序目录&gt;</c> 指定共享配置目录（默认取 CLI 自己的程序目录，
/// 那里通常没有 config/，因此真实数据自测要显式给）；
/// <c>LME_EXPORT_TIMEOUT_S=&lt;秒&gt;</c> 到点自动取消（用来单独验证取消响应速度）。</para>
/// </summary>
internal static class ExportModCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.ExportMod");

    /// <summary>执行一次完整导出。返回进程退出码（0 成功 / 1 失败 / 3 已取消）。</summary>
    public static async Task<int> RunAsync(string projectFile, string targetDirectory, string? baseDirectory, int timeoutSeconds)
    {
        using var scope = Log.Scope($"export-mod {projectFile} → {targetDirectory}");
        var project = await new ProjectService().LoadAsync(projectFile);
        Log.Info("项目已载入：{0}；资产 {1} 条", project.Name, project.Assets.Count);

        var environment = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppEnvironment.Current
            : new AppEnvironment(baseDirectory);
        // LME_REHYDRATE=1：复现 GUI 的真实形态 —— GUI 打开项目时会从扫描索引回灌
        // 全部引用资产（真实数据 127 万条）。不回灌时项目只有项目文件里的几百条，
        // 「导出计划 / 重打包候选」这类按资产全表扫描的开销**不会**出现，
        // 于是 CLI 测不出 GUI 的慢。
        if (Environment.GetEnvironmentVariable("LME_REHYDRATE") == "1")
        {
            var scan = new UnityCacheScanService(Path.Combine(environment.CacheDirectory, "unity-cache-index.json"));
            var rehydrateWatch = Stopwatch.StartNew();
            var added = await scan.RehydrateFromIndexAsync(project);
            Log.Info("回灌引用资产完成：新增 {0:N0} 条，项目共 {1:N0} 条，耗时 {2} ms",
                added, project.Assets.Count, rehydrateWatch.ElapsedMilliseconds);
        }
        var context = new ModExportPlanContext(
            environment.EffectiveUnityCacheDirectory(project),
            environment.EffectiveFmodLibraryDirectory(project));
        Log.Info("导出环境：Unity 缓存 {0}；FMOD {1}；项目目录 {2}",
            context.UnityCacheDirectory ?? "(未配置)",
            context.FmodDirectory ?? "(未发现)",
            Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? "-");

        var planWatch = Stopwatch.StartNew();
        var plan = new ModExportPlanService().Plan(
            project,
            targetDirectory,
            new LangEditSession(),
            new StaticEditSession(),
            context);
        Log.Info("计划完成：{0} 个槽位 / 点亮 {1} 个 / 产物 {2} 个，耗时 {3} ms",
            plan.Items.Count, plan.PlannedSlotCount, plan.PlannedArtifactCount, planWatch.ElapsedMilliseconds);
        foreach (var item in plan.Items)
        {
            Log.Info("  · {0}", item.Describe());
            if (item.Warnings is { Count: > 0 } warnings)
            {
                foreach (var warning in warnings) Log.Warn("     提示：{0}", warning);
            }
        }

        if (plan.PlannedSlotCount == 0)
        {
            Log.Warn("没有点亮的槽位：没有可导出的修改");
            Console.WriteLine("没有可导出的修改（先改点东西：替换图片 / 音频样本 / 编辑文本表 / 静态表）。");
            return 0;
        }

        var progressCount = 0;
        var progress = new Progress<string>(message =>
        {
            var index = Interlocked.Increment(ref progressCount);
            if (index == 1 || index % 50 == 0) Log.Debug("进度 #{0}：{1}", index, message);
        });

        using var cancellation = new CancellationTokenSource();
        if (timeoutSeconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var cancelRequestedAt = 0L;
        using var registration = cancellation.Token.Register(() =>
        {
            Interlocked.Exchange(ref cancelRequestedAt, Stopwatch.GetTimestamp());
            Log.Warn("取消已请求（{0}）", timeoutSeconds > 0 ? $"{timeoutSeconds} 秒超时" : "Ctrl+C / 外部取消");
        });

        ConsoleCancelEventHandler onCancelKey = (_, e) =>
        {
            e.Cancel = true;
            Log.Info("收到 Ctrl+C：请求取消导出");
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancelKey;

        var watch = Stopwatch.StartNew();
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? ".";
            var result = await Task.Run(
                () => new ModPackExportService().ExportAsync(
                    project, projectDirectory, plan, context, progress, cancellation.Token),
                cancellation.Token);

            Log.Info("导出完成：{0} 个槽位 / {1} 个产物，总耗时 {2} ms；进度回调 {3} 次",
                result.WrittenSlotCount, result.WrittenFileCount, watch.ElapsedMilliseconds, progressCount);
            Console.WriteLine(result.Describe());
            foreach (var slot in result.Slots)
            {
                Log.Info("  槽位 {0}", slot.Describe());
                Console.WriteLine("  " + slot.Describe());
                foreach (var diagnostic in slot.Diagnostics) Log.Warn("     诊断：{0}", diagnostic);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            var requested = Interlocked.Read(ref cancelRequestedAt);
            var responseMs = requested == 0
                ? -1
                : (Stopwatch.GetTimestamp() - requested) * 1000 / Stopwatch.Frequency;
            Log.Warn("导出已取消：从请求取消到真正中止共 {0} ms（总耗时 {1} ms）", responseMs, watch.ElapsedMilliseconds);
            Console.Error.WriteLine($"已取消（取消响应耗时 {responseMs} ms，总耗时 {watch.ElapsedMilliseconds} ms）");
            return 3;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出失败（总耗时 {0} ms）", watch.ElapsedMilliseconds);
            Console.Error.WriteLine("导出失败：" + ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
        }
    }
}
