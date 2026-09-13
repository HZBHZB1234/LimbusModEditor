using NLog;

namespace LimbusModEditor.Domain.Diagnostics;

/// <summary>
/// 全仓库共用的 NLog 调用约定（放在 Domain 是为了让 Formats.*/Editing 这些底层项目也能直接用）。
///
/// <para>每个要打日志的类只加两行：</para>
/// <code>
/// using NLog;
/// ...
/// private static readonly Logger Log = LogManager.GetCurrentClassLogger();
/// </code>
/// 然后直接写 <c>Log.Info($"…")</c> / <c>Log.Error(ex, "…")</c>。文件/方法/行号由 NLog 的
/// <c>${callsite}</c> 自动带上，不需要手写。
///
/// <para>这里只放第三方库**不提供**的四个便利方法（详见 <c>docs/LOGGING.md</c>）：</para>
/// <list type="bullet">
/// <item><see cref="Scope"/>：计时作用域，慢操作自动升级成 Warn。</item>
/// <item><see cref="Guard(Logger, string, Action)"/> 等：异常拦截 + 完整栈记录（「报错收集」主力，
/// 用于 UI 事件处理、后台任务这类"绝不能让异常逃出去"的位置）。</item>
/// <item><see cref="Every"/>：百万级循环的采样日志。</item>
/// </list>
/// </summary>
public static class LoggerExtensions
{
    /// <summary>默认「慢操作」阈值（毫秒）：<see cref="Scope"/> 超过它就把完成日志升级为 Warn。</summary>
    public const int DefaultSlowOperationMs = 5_000;

    /// <summary>
    /// 计时作用域：进入打 Trace、离开打 Debug（含耗时），超过 <paramref name="slowMs"/> 打 Warn。
    /// 用法：<c>using var scope = Log.Scope("导出 lang");</c>
    /// </summary>
    public static IDisposable Scope(this Logger logger, string name, int slowMs = DefaultSlowOperationMs)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsTraceEnabled) logger.Trace("▶ {0}", name);
        return new LogScope(logger, name, slowMs);
    }

    /// <summary>执行并拦截异常：出错记 Error + 完整栈；取消（<see cref="OperationCanceledException"/>）
    /// 记 Info 且不视为失败。返回是否成功。</summary>
    public static bool Guard(this Logger logger, string context, Action action)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (action is null) return true;
        try
        {
            action();
            return true;
        }
        catch (OperationCanceledException ex)
        {
            logger.Info("已取消：{0}（{1}）", context, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "异常被拦截：{0}", context);
            return false;
        }
    }

    /// <summary>执行并拦截异常，失败时返回 <paramref name="fallback"/>。</summary>
    public static T? Guard<T>(this Logger logger, string context, Func<T?> func, T? fallback = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (func is null) return fallback;
        try
        {
            return func();
        }
        catch (OperationCanceledException ex)
        {
            logger.Info("已取消：{0}（{1}）", context, ex.Message);
            return fallback;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "异常被拦截：{0}", context);
            return fallback;
        }
    }

    /// <summary>异步版 <see cref="Guard(Logger, string, Action)"/>。</summary>
    public static async Task<bool> GuardAsync(this Logger logger, string context, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (action is null) return true;
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            logger.Info("已取消：{0}（{1}）", context, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "异常被拦截：{0}", context);
            return false;
        }
    }

    /// <summary>
    /// 采样日志：第 1 次、以及之后每 <paramref name="every"/> 次记一条。用于逐资源 / 逐条目
    /// 这种十万级以上的循环 —— 既有进度感，又不会写出 GB 级日志。消息用工厂延迟构造。
    /// </summary>
    public static void Every(this Logger logger, long counter, long every, LogLevel level, Func<string> messageFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(messageFactory);
        if (every < 1) every = 1;
        if (counter != 1 && counter % every != 0) return;
        if (!logger.IsEnabled(level)) return;
        logger.Log(level, messageFactory());
    }

    private sealed class LogScope(Logger logger, string name, int slowMs) : IDisposable
    {
        private readonly long _start = Environment.TickCount64;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            var elapsed = Environment.TickCount64 - _start;
            if (elapsed >= slowMs)
            {
                logger.Warn("⏱ 慢操作：{0} 耗时 {1} ms", name, elapsed);
            }
            else if (logger.IsDebugEnabled)
            {
                logger.Debug("⏹ {0} 完成（{1} ms）", name, elapsed);
            }
        }
    }
}
