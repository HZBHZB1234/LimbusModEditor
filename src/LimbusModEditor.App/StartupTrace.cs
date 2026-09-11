using System.IO;

namespace LimbusModEditor.App;

/// <summary>
/// 启动诊断打点（临时脚手架）：把启动关键路径的时间戳追加到
/// <c>%TEMP%\lme-startup-trace.log</c>。用途只有一个 —— 定位「双击启动后过一会儿才弹窗」
/// 到底是哪一段慢（窗口构造 / 项目加载 / 自动配置 / 回灌 / 模态弹出）。
/// 用 <c>LME_TRACE_STARTUP=1</c> 环境变量开关；不设置时整条链路零开销。
/// </summary>
internal static class StartupTrace
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("LME_TRACE_STARTUP"), "1", StringComparison.Ordinal);

    private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
    private static readonly object Gate = new();

    /// <summary>打一个点（毫秒级相对时间 + 线程 + 文案）。</summary>
    public static void Mark(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(Path.GetTempPath(), "lme-startup-trace.log");
                File.AppendAllText(path, $"{Watch.ElapsedMilliseconds,7} ms  t{Environment.CurrentManagedThreadId,2}  {message}\n");
            }
        }
        catch (Exception) { /* 打点失败绝不影响启动 */ }
    }

    /// <summary>开始一段计时（返回 IDisposable，Dispose 时写入「前 → 耗时」）。</summary>
    public static IDisposable Span(string label) => new Scope(label);

    private sealed class Scope(string label) : IDisposable
    {
        private readonly long _start = Watch.ElapsedMilliseconds;
        public void Dispose() => Mark($"{label} ← {Watch.ElapsedMilliseconds - _start} ms");
    }
}
