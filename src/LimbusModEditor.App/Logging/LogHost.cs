using System.IO;
using System.Windows;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Diagnostics;
using NLog;
using WpfApplication = System.Windows.Application;

namespace LimbusModEditor.App.Logging;

/// <summary>
/// WPF 宿主的日志装配（最早的时机调用一次）：NLog 装配 + 全局异常钩子 + UI 心跳。
///
/// <para>「报错收集」的三个入口：</para>
/// <list type="bullet">
/// <item><c>Application.DispatcherUnhandledException</c> —— UI 线程抛出的异常
/// （<b>不</b>改 <c>Handled</c>，行为与改动前一致）。</item>
/// <item><see cref="AppDomain.UnhandledException"/> —— 任意线程的漏网异常（进程即将结束，先把日志冲盘）。</item>
/// <item><see cref="TaskScheduler.UnobservedTaskException"/> —— 被丢弃的 Task 异常。</item>
/// </list>
/// 三者都会额外写一份 <c>logs/crash-&lt;时间&gt;.log</c>（异常 + 最近 400 条日志快照）。
///
/// <para>任何失败都不阻断启动：日志自身出问题时只把原因写到 <c>%TEMP%\lme-log-host-failure.txt</c>。
/// 注意 <see cref="Log"/> 用属性而不是静态字段 —— 静态字段会在类型初始化时碰 <c>LogManager</c>，
/// 那会触发 NLog 自动加载配置、绕过 <see cref="NLogBootstrap"/> 的目录决策。</para>
/// </summary>
internal static class LogHost
{
    private static int _installed;
    private static UiHeartbeat? _heartbeat;
    private static Logger? _logger;

    private static Logger Log => _logger ??= LogManager.GetLogger("LimbusModEditor.App");

    /// <summary>日志系统状态（设置页 / 状态栏展示）。</summary>
    public static LoggingStatus Status => NLogBootstrap.Current;

    /// <summary>日志目录（未落盘时为 null）。</summary>
    public static string? LogDirectory => NLogBootstrap.LogDirectory;

    /// <summary>当前会话日志文件（未落盘时为 null）。</summary>
    public static string? CurrentLogFile => NLogBootstrap.CurrentLogFile;

    /// <summary>最近日志文本（复制到剪贴板 / 导出诊断）。</summary>
    public static string RecentText(int count = 200) => NLogBootstrap.RecentText(count);

    /// <summary>装配日志系统（幂等）。</summary>
    public static void Initialize(WpfApplication application)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        try
        {
            NLogBootstrap.Initialize(AppContext.BaseDirectory, BuildHostInfo());
        }
        catch (Exception ex)
        {
            WriteFallbackNote(ex);
        }

        try
        {
            InstallHandlers(application);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "全局异常钩子装配失败（日志仍可用）");
        }

        try
        {
            _heartbeat = new UiHeartbeat(application.Dispatcher);
            _heartbeat.Start();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "UI 心跳启动失败");
        }

        Log.Info("日志系统就绪：{0}", Status.Describe());
        if (Status.Note is not null) Log.Warn("日志配置说明：{0}", Status.Note);
    }

    /// <summary>退出前冲刷并关闭日志（幂等）。</summary>
    public static void Shutdown()
    {
        try { _heartbeat?.Dispose(); } catch (Exception) { /* 退出路径 */ }
        _heartbeat = null;
        try { NLogBootstrap.Shutdown(); } catch (Exception) { /* 退出路径尽力而为 */ }
    }

    /// <summary>把最近日志整段写进指定文件（用户在 GUI 上导出诊断用）。</summary>
    public static string? DumpRecentTo(string path, int count = 1000)
    {
        try
        {
            File.WriteAllText(path, RecentText(count));
            return path;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出最近日志失败：{0}", path);
            return null;
        }
    }

    private static LogHostInfo BuildHostInfo()
    {
        var context = new List<KeyValuePair<string, string>>();
        try
        {
            var environment = AppEnvironment.Current;
            context.Add(new("共享配置", environment.ConfigFile));
            context.Add(new("缓存目录", environment.CacheDirectory));
            context.Add(new("游戏目录", environment.Config.GameDirectory ?? "(未配置)"));
            context.Add(new("Unity 缓存目录", environment.Config.UnityCacheDirectory ?? "(未配置)"));
            context.Add(new("模组目录", environment.Config.ModDirectory ?? "(未配置)"));
            context.Add(new("FMOD 目录", environment.EffectiveFmodLibraryDirectory(null) ?? "(未发现)"));
            context.Add(new("最近项目", environment.Config.LastProjectFile ?? "(无)"));
        }
        catch (Exception ex)
        {
            context.Add(new("环境信息", $"读取失败：{ex.GetType().Name}: {ex.Message}"));
        }

        return new LogHostInfo(ResolveApplicationName(), ResolveVersion(), context);
    }

    private static string ResolveApplicationName()
        => Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "LimbusModEditor.App";

    private static string ResolveVersion()
    {
        try
        {
            var assembly = typeof(LogHost).Assembly;
            return assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is [System.Reflection.AssemblyInformationalVersionAttribute info, ..]
                ? info.InformationalVersion
                : assembly.GetName().Version?.ToString() ?? "(未知版本)";
        }
        catch (Exception)
        {
            return "(未知版本)";
        }
    }

    private static void InstallHandlers(WpfApplication application)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "AppDomain 未处理异常（IsTerminating={0}）", args.IsTerminating);
                WriteCrashDump(exception, "AppDomain", args.IsTerminating);
            }
            else
            {
                Log.Fatal("AppDomain 未处理异常（非 Exception 对象：{0}）", args.ExceptionObject?.ToString() ?? "(null)");
                WriteCrashDump(null, "AppDomain", args.IsTerminating);
            }
            NLogBootstrap.Flush(3000);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常（TaskScheduler.UnobservedTaskException）");
            WriteCrashDump(args.Exception, "UnobservedTask", terminating: false);
        };

        application.DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "UI 线程未处理异常（DispatcherUnhandledException）");
            WriteCrashDump(args.Exception, "Dispatcher", terminating: true);
            NLogBootstrap.Flush(3000);
            // 刻意不设 args.Handled：保持改动前的行为（异常继续冒泡使进程退出），
            // 只是现在一定会留下完整日志与崩溃快照。
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => NLogBootstrap.Flush(2000);
    }

    private static void WriteCrashDump(Exception? exception, string source, bool terminating)
    {
        try
        {
            var directory = Status.Directory;
            if (string.IsNullOrWhiteSpace(directory)) return;
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var uiThreadId = WpfApplication.Current?.Dispatcher.Thread.ManagedThreadId ?? -1;
            var text = new System.Text.StringBuilder();
            text.AppendLine("================ 崩溃快照 ================");
            text.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            text.AppendLine($"来源：{source}；终止进程：{terminating}");
            text.AppendLine($"进程：{Environment.ProcessId}；版本：{ResolveVersion()}");
            text.AppendLine($"线程：t{Environment.CurrentManagedThreadId}（UI 线程：t{uiThreadId}）");
            text.AppendLine();
            text.AppendLine("---- 异常 ----");
            text.AppendLine(exception is null ? "(无 Exception 对象)" : exception.ToString());
            text.AppendLine();
            text.AppendLine("---- 最近 400 条日志 ----");
            text.AppendLine(RecentText(400));
            File.WriteAllText(path, text.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"崩溃快照写入失败：{ex.Message}");
        }
    }

    private static void WriteFallbackNote(Exception exception)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "lme-log-host-failure.txt"),
                $"{DateTimeOffset.Now:O}\n{exception}");
        }
        catch (Exception)
        {
            // 连兜底都写不进去就只能算了。
        }
    }
}
