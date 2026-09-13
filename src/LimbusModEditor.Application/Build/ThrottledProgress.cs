using System.Diagnostics;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Application.Build;

/// <summary>
/// 节流进度上报（<see cref="IProgress{T}"/> 装饰器）。
///
/// <para><b>为什么需要</b>：导出的逐资源 / 逐对象循环动辄十万次
/// <c>Report</c>，而 UI 侧的 <c>Progress&lt;string&gt;</c> 会把每一次都
/// <c>Post</c> 到 WPF Dispatcher 队列。队列只进不出时，等 UI 线程回到消息循环
/// 要先把积压的几十万条 <c>TextBlock.Text</c> 更新（每条还触发布局失效）排空 ——
/// 这就是「导出跑久了界面像死了一样」的放大器之一。</para>
///
/// <para><b>节流口径</b>：按时间（默认 200ms）放行；<b>首条与末条一定放行</b>
/// （否则用户看不到开始与结束），跨过时间窗的那条也放行。</para>
/// </summary>
public sealed class ThrottledProgress : IProgress<string>
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IProgress<string>? _inner;
    private readonly long _intervalMs;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _lastReportMs = -1;
    private long _passedCount;
    private string? _pending;

    /// <param name="inner">真正的接收者（UI 的 <c>Progress&lt;string&gt;</c>）；为 null 时本对象是空实现。</param>
    /// <param name="intervalMs">最小上报间隔（毫秒）。</param>
    public ThrottledProgress(IProgress<string>? inner, int intervalMs = 200)
    {
        _inner = inner;
        _intervalMs = Math.Max(0, intervalMs);
    }

    /// <summary>已经因为节流被丢弃的上报条数（诊断用）。</summary>
    public int SuppressedCount { get; private set; }

    public void Report(string value)
    {
        if (_inner is null) return;
        var now = _watch.ElapsedMilliseconds;
        if (_lastReportMs >= 0 && now - _lastReportMs < _intervalMs)
        {
            _pending = value;   // 记住最后一条，收尾时补发
            SuppressedCount++;
            // 热路径：仅在 Debug 开启时按 5000 条采样（Report 动辄十万次，禁止逐条记录）
            if (Log.IsDebugEnabled)
            {
                Log.Every(SuppressedCount, 5000, LogLevel.Debug,
                    () => $"进度上报被节流丢弃：累计 {SuppressedCount} 条（放行 {_passedCount} 条，间隔 {_intervalMs} ms）");
            }
            return;
        }
        _lastReportMs = now;
        _passedCount++;
        // 热路径：仅在 Debug 开启时按 100 条采样「跨时间窗放行」的条数
        if (Log.IsDebugEnabled)
        {
            Log.Every(_passedCount, 100, LogLevel.Debug,
                () => $"进度上报放行：第 {_passedCount} 条（累计丢弃 {SuppressedCount} 条，耗时 {now} ms）");
        }
        _pending = null;
        _inner.Report(value);
    }

    /// <summary>补发最后一条被节流掉的消息（循环结束时调用一次）。</summary>
    public void Flush()
    {
        if (_inner is null || _pending is null) return;
        var last = _pending;
        _pending = null;
        _lastReportMs = _watch.ElapsedMilliseconds;
        if (Log.IsDebugEnabled)
        {
            Log.Debug("进度上报补发末条：{0}（累计放行 {1} 条，节流丢弃 {2} 条，耗时 {3} ms）",
                last, _passedCount, SuppressedCount, _watch.ElapsedMilliseconds);
        }
        _inner.Report(last);
    }
}
