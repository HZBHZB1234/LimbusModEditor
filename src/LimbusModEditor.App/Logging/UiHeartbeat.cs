using System.Windows.Threading;
using NLog;

namespace LimbusModEditor.App.Logging;

/// <summary>
/// UI 线程心跳：每秒一跳，专门用来抓「界面未响应」这类问题 ——
/// UI 线程被长任务堵住时，DispatcherTimer 的 tick 会被推迟，间隔直接暴露阻塞时长
/// （≥2 秒立刻记一条 Warn）。正常时每 10 跳记一条 Debug，日志量可控。
///
/// <para>排查「未响应」时的读法：日志里最后一次心跳之后如果只剩工作线程的记录，
/// 说明 UI 线程从那一刻起被堵住了；恢复后第一条心跳的「间隔」就是阻塞时长。</para>
///
/// <para><see cref="Log"/> 用属性而非静态字段：静态字段会在类型初始化时碰 <c>LogManager</c>，
/// 从而触发 NLog 自动加载配置。</para>
/// </summary>
internal sealed class UiHeartbeat : IDisposable
{
    private const int BlockedThresholdMs = 2_000;
    private const int ReportEveryBeats = 10;

    private static Logger? _logger;
    private static Logger Log => _logger ??= LogManager.GetLogger("LimbusModEditor.UI");

    private readonly DispatcherTimer _timer;
    private readonly int _uiThreadId;
    private long _lastTick;
    private long _beats;
    private long _windowMaxGap;
    private bool _disposed;

    public UiHeartbeat(Dispatcher dispatcher)
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        _lastTick = Environment.TickCount64;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>开始心跳（在 UI 线程上调用）。</summary>
    public void Start()
    {
        _timer.Start();
        Log.Debug("UI 心跳启动（UI 线程 t{0}，每秒 1 跳，阻塞 ≥{1} ms 记 Warn）", _uiThreadId, BlockedThresholdMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        catch (Exception)
        {
            // 退出路径，忽略。
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var gap = now - _lastTick;
        _lastTick = now;
        _beats++;

        if (gap >= BlockedThresholdMs)
        {
            Log.Warn("UI 线程被阻塞约 {0} ms（心跳 #{1}）—— 这期间界面不会响应任何操作", gap, _beats);
        }
        else if (_beats % ReportEveryBeats == 0)
        {
            var windowMax = Math.Max(_windowMaxGap, gap);
            Log.Debug("UI 心跳 #{0}（近 {1} 秒最大间隔 {2} ms）", _beats, ReportEveryBeats, windowMax);
        }

        _windowMaxGap = gap;
    }
}
