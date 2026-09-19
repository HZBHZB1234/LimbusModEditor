namespace LimbusModEditor.Application.Common;

/// <summary>
/// 直通进度上报：<see cref="Report"/> 就在<b>调用线程</b>上执行回调，不做任何跨线程投递。
///
/// <para><b>为什么不用 <c>Progress&lt;T&gt;</c></b>：<c>Progress&lt;T&gt;</c> 在构造时捕获
/// <c>SynchronizationContext</c>，之后每次 <c>Report</c> 都 <c>Post</c> 到那个线程。
/// 这些服务大多是从 UI 线程被调用的，于是「后台线程上报 → 投递到 UI → 回调里再上报
/// → 又投递到 UI」形成两次跨线程往返；扫描期的上报量足以把 Dispatcher 队列堆起来，
/// 表现为界面卡住数秒（日志里的 <c>UI 线程被阻塞约 3329 ms / 2922 ms</c>）。
/// 而且异步投递会让「最后一条进度」排在调用返回之后才执行，收尾补发反而比中间态更旧。</para>
///
/// <para><b>切线程是宿主的责任</b>：真正的跨线程只应在事件出口
/// （App 层把消息投给 WebView2）做一次。调用方需要保证
/// <paramref name="onReport"/> 自身线程安全，或保证只在单线程上报。</para>
/// </summary>
/// <typeparam name="T">进度载荷类型。</typeparam>
public sealed class DirectProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;

    public DirectProgress(Action<T> onReport)
        => _onReport = onReport ?? throw new ArgumentNullException(nameof(onReport));

    public void Report(T value) => _onReport(value);
}
