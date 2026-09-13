using System.Globalization;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace LimbusModEditor.Application.Diagnostics;

/// <summary>宿主信息（写进会话头部，便于事后判断日志是哪次、哪个版本的运行）。</summary>
/// <param name="ApplicationName">程序名（App / Cli）。</param>
/// <param name="Version">版本串（可空）。</param>
/// <param name="Context">额外键值（项目路径、游戏目录、缓存目录……）。</param>
public sealed record LogHostInfo(
    string ApplicationName,
    string? Version = null,
    IReadOnlyList<KeyValuePair<string, string>>? Context = null);

/// <summary>日志系统当前状态（设置页展示 / 状态栏提示 / 排障时报告给用户）。</summary>
/// <param name="Directory">日志目录（生效值）。</param>
/// <param name="CurrentFile">当前会话日志文件。</param>
/// <param name="ErrorFile">错误汇总文件。</param>
/// <param name="MinimumLevel">生效的最低级别。</param>
/// <param name="FileLoggingEnabled">是否真的在落盘。</param>
/// <param name="Failure">落盘失败原因（成功时为 null）。</param>
/// <param name="Note">降级说明（例如没找到 nlog.config 而用了内置配置）。</param>
public sealed record LoggingStatus(
    string? Directory,
    string? CurrentFile,
    string? ErrorFile,
    LogLevel MinimumLevel,
    bool FileLoggingEnabled,
    string? Failure = null,
    string? Note = null)
{
    /// <summary>给用户看的一行摘要。</summary>
    public string Describe() => FileLoggingEnabled
        ? $"日志目录：{Directory}（级别 {MinimumLevel}）"
        : $"日志未落盘（{Failure ?? "已关闭"}）";
}

/// <summary>
/// NLog 装配（Application 层策略，宿主在最早时机调一次）：解析环境变量 → 决定日志目录 →
/// 加载 <c>nlog.config</c>（缺失时从程序集内嵌副本还原到程序目录）→ 装进 NLog → 写会话头部。
///
/// <para>环境变量：<c>LME_LOG_DIR</c>（目录）、<c>LME_LOG_LEVEL</c>（trace|debug|info|warn|error|off）、
/// <c>LME_LOG_OFF=1</c>（关闭全部输出）、<c>LME_LOG_CONSOLE=1</c>（同时输出控制台）。</para>
///
/// <para>默认目录＝<c>&lt;程序目录&gt;/logs</c>；目录不可写时自动退到
/// <c>%TEMP%/LimbusModEditor-logs</c>。**日志系统自身绝不阻断宿主**：任何异常都被吃掉并记在
/// <see cref="Current"/> 里，最坏情况退化成「只在内存里留 2000 条」。</para>
/// </summary>
public static class NLogBootstrap
{
    public const string DirectoryVariable = "LME_LOG_DIR";
    public const string LevelVariable = "LME_LOG_LEVEL";
    public const string OffVariable = "LME_LOG_OFF";
    public const string ConsoleVariable = "LME_LOG_CONSOLE";

    /// <summary>配置文件（随程序发布，<c>autoReload="true"</c> 支持运行中改级别）。</summary>
    public const string ConfigFileName = "nlog.config";

    /// <summary>内存环形缓冲 target 名（UI 展示 / 崩溃快照）。</summary>
    public const string MemoryTargetName = "memory";

    /// <summary>控制台 target 名（默认被移除，<c>LME_LOG_CONSOLE=1</c> 时保留）。</summary>
    public const string ConsoleTargetName = "console";

    private const string LoggerName = "LimbusModEditor";

    private static readonly object Gate = new();
    private static LoggingStatus _current = new(null, null, null, LogLevel.Debug, false, "未初始化");
    private static Logger? _host;

    /// <summary>宿主日志器：首次访问时才创建 —— 必须在 <see cref="Initialize"/> 之后，
    /// 否则会触发 NLog 自动加载配置、绕过本类的目录决策。</summary>
    private static Logger Host => _host ??= LogManager.GetLogger(LoggerName);

    /// <summary>最近一次初始化的结果。</summary>
    public static LoggingStatus Current { get { lock (Gate) { return _current; } } }

    /// <summary>日志目录（未落盘时为 null）。</summary>
    public static string? LogDirectory => Current.Directory;

    /// <summary>当前会话日志文件。</summary>
    public static string? CurrentLogFile => Current.CurrentFile;

    /// <summary>错误汇总文件。</summary>
    public static string? ErrorLogFile => Current.ErrorFile;

    /// <summary>按环境变量初始化（宿主最早时机调用；重复调用会重装配置）。</summary>
    public static LoggingStatus Initialize(string baseDirectory, LogHostInfo? host = null)
    {
        LoggingStatus status;
        if (IsOff())
        {
            LogManager.Configuration = CreateMinimalConfiguration();
            status = new LoggingStatus(null, null, null, LogLevel.Debug, false, $"{OffVariable}=1");
        }
        else
        {
            var level = ParseLevel(Environment.GetEnvironmentVariable(LevelVariable));
            var directory = ResolveDirectory(baseDirectory, out var directoryFailure);
            var configuration = LoadConfiguration(out var configFailure, out var note);
            configuration.Variables["logdir"] = Layout.FromLiteral(directory);
            if (!ConsoleEnabled()) TrimTarget(configuration, ConsoleTargetName);
            LogManager.Configuration = configuration;
            LogManager.GlobalThreshold = level;
            LogManager.ReconfigExistingLoggers();
            status = new LoggingStatus(
                directory,
                Path.Combine(directory, "current.log"),
                Path.Combine(directory, "errors.log"),
                level,
                directoryFailure is null,
                directoryFailure,
                note ?? configFailure);
        }

        lock (Gate) { _current = status; }
        WriteSessionHeader(status, host, baseDirectory);
        if (status.Note is not null) Host.Warn("日志配置已降级：{0}", status.Note);
        return status;
    }

    /// <summary>尽力把待写日志刷到磁盘（崩溃处理 / 退出路径；不抛异常）。</summary>
    public static void Flush(int timeoutMs = 3000)
    {
        try { LogManager.Flush(timeoutMs); } catch (Exception) { /* 退出路径尽力而为 */ }
    }

    /// <summary>退出：记录会话结束并关闭 NLog（幂等）。</summary>
    public static void Shutdown()
    {
        try
        {
            Host.Info("会话结束（日志冲刷）");
        }
        catch (Exception)
        {
            // 配置都没装起来时忽略。
        }

        Flush(3000);
        try { LogManager.Shutdown(); } catch (Exception) { /* 退出路径 */ }
    }

    /// <summary>内存环形缓冲里的最近日志（时间升序，已按布局格式化）。</summary>
    public static IReadOnlyList<string> RecentLines(int count = 200)
    {
        try
        {
            var target = LogManager.Configuration?.FindTargetByName<MemoryTarget>(MemoryTargetName);
            var logs = target?.Logs;
            if (logs is null || logs.Count == 0 || count <= 0) return Array.Empty<string>();
            var take = Math.Min(count, logs.Count);
            var result = new string[take];
            for (var i = 0; i < take; i++) result[i] = logs[logs.Count - take + i];
            return result;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>最近日志的纯文本快照（复制到剪贴板 / 崩溃报告）。</summary>
    public static string RecentText(int count = 200)
    {
        var lines = RecentLines(count);
        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
    }

    // ── 装配细节 ────────────────────────────────────────────────────

    private static bool IsOff()
        => string.Equals(Environment.GetEnvironmentVariable(OffVariable)?.Trim(), "1", StringComparison.Ordinal);

    private static bool ConsoleEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(ConsoleVariable)?.Trim(), "1", StringComparison.Ordinal);

    private static LogLevel ParseLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LogLevel.Debug;
        try
        {
            var level = LogLevel.FromString(text.Trim());
            return level ?? LogLevel.Debug;
        }
        catch (ArgumentException)
        {
            return LogLevel.Debug;
        }
    }

    /// <summary>日志目录：环境变量 → 程序目录/logs → %TEMP%（逐级探测可写）。</summary>
    private static string ResolveDirectory(string baseDirectory, out string? failure)
    {
        failure = null;
        var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        var preferred = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured.Trim())
            : Path.Combine(Path.GetFullPath(root), "logs");

        if (TryProbe(preferred, out var reason)) return preferred;

        var fallback = Path.Combine(Path.GetTempPath(), "LimbusModEditor-logs");
        if (TryProbe(fallback, out var fallbackReason))
        {
            failure = $"日志目录不可写；已退到临时目录（首选 {preferred}：{reason}）";
            return fallback;
        }

        failure = $"日志目录不可写：首选 {preferred}（{reason}）；临时目录 {fallback}（{fallbackReason}）";
        return preferred;
    }

    private static bool TryProbe(string directory, out string reason)
    {
        reason = string.Empty;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".lme-write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>加载 <c>nlog.config</c>：程序目录的同名文件 → 内嵌副本（同时还原到程序目录）→ 最小内置配置。</summary>
    private static LoggingConfiguration LoadConfiguration(out string? failure, out string? note)
    {
        failure = null;
        note = null;

        var path = EnsureConfigFile(out var restoreNote);
        note = restoreNote;
        if (path is not null)
        {
            try
            {
                return new XmlLoggingConfiguration(path);
            }
            catch (Exception ex)
            {
                failure = $"{ConfigFileName} 解析失败，已使用内置副本：{ex.Message}";
            }
        }

        try
        {
            using var stream = OpenEmbeddedConfig();
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return new XmlLoggingConfiguration(reader);
            }
            failure ??= $"未找到 {ConfigFileName}，且程序集内没有内嵌副本（只保留内存日志）";
        }
        catch (Exception ex)
        {
            failure = $"{failure ?? string.Empty}；内嵌副本同样不可用：{ex.Message}";
        }

        return CreateMinimalConfiguration();
    }

    /// <summary>确保程序目录有 nlog.config（缺失时从内嵌副本还原，便于用户随时改级别）。</summary>
    private static string? EnsureConfigFile(out string? note)
    {
        note = null;
        var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (File.Exists(path)) return path;

        try
        {
            using var stream = OpenEmbeddedConfig();
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            File.WriteAllText(path, reader.ReadToEnd());
            note = $"程序目录缺少 {ConfigFileName}，已从内嵌副本还原（下次可自行编辑，autoReload 生效）";
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            note = $"无法把 {ConfigFileName} 还原到程序目录（{ex.Message}），改用内嵌副本（无热加载）";
            return null;
        }
    }

    private static Stream? OpenEmbeddedConfig()
    {
        var assembly = typeof(NLogBootstrap).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(ConfigFileName, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }

    /// <summary>最小可用配置：只在内存里留 2000 条（任何情况下日志系统都不会让宿主崩）。</summary>
    private static LoggingConfiguration CreateMinimalConfiguration()
    {
        var configuration = new LoggingConfiguration();
        var memory = new MemoryTarget { Name = MemoryTargetName, MaxLogsCount = 2000 };
        configuration.AddTarget(memory);
        configuration.AddRuleForAllLevels(memory, "*");
        return configuration;
    }

    private static void TrimTarget(LoggingConfiguration configuration, string targetName)
    {
        var target = configuration.FindTargetByName(targetName);
        if (target is null) return;
        foreach (var rule in configuration.LoggingRules) rule.Targets.Remove(target);
        for (var i = configuration.LoggingRules.Count - 1; i >= 0; i--)
        {
            if (configuration.LoggingRules[i].Targets.Count == 0) configuration.LoggingRules.RemoveAt(i);
        }
    }

    private static void WriteSessionHeader(LoggingStatus status, LogHostInfo? host, string baseDirectory)
    {
        Host.Info("================ 会话开始 ================");
        Host.Info("程序：{0} {1}", host?.ApplicationName ?? "LimbusModEditor", host?.Version ?? "(未知版本)");
        Host.Info("运行时：.NET {0} / {1} / {2} / {3} 位进程",
            Environment.Version,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.Is64BitProcess ? 64 : 32);
        Host.Info("进程：{0}；程序目录：{1}", Environment.ProcessId, baseDirectory);
        Host.Info("工作目录：{0}", Environment.CurrentDirectory);
        Host.Info(status.FileLoggingEnabled
            ? "日志文件：{0}（错误汇总：{1}）"
            : "日志落盘：关闭（{2}）",
            status.CurrentFile ?? "-", status.ErrorFile ?? "-", status.Failure ?? "已关闭");
        Host.Info("日志级别：{0}（环境变量 {1} 可调：trace|debug|info|warn|error；也可直接改 nlog.config，运行中生效）",
            status.MinimumLevel, LevelVariable);

        if (host?.Context is { Count: > 0 } context)
        {
            foreach (var pair in context)
            {
                Host.Info("  · {0} = {1}", pair.Key, pair.Value);
            }
        }
    }
}
