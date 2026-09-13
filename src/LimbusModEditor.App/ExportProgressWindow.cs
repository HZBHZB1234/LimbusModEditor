using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>
/// 长操作进度窗口：显示阶段消息 + 不确定进度条，操作结束后由调用方关闭。
/// 用于一键导出等没有逐条进度但有阶段消息的流程，替代只更新状态栏的静默等待。
///
/// <para><b>取消能力（修复「导出跑久了未响应且无法中断」）</b>：导出链上的重活
/// （Unity 重打包、逐对象 XZ、bank 重组）都是同步 CPU/IO，跑在后台线程上；本窗口
/// 提供 <see cref="Token"/> 交给导出链，并在「取消」按钮与窗口关闭时置位。
/// 取消是**协作式**的：导出在每个对象 / 每个槽位之间检查 token，因此按下取消后
/// 会停在下一个检查点（不会留下半成品：产物走 AtomicOutput，临时文件在 finally 清理）。</para>
///
/// <para><b>为什么窗口可以被关掉却仍要记录</b>：调用方是 <c>Show()</c>（非模态），
/// 用户可能在导出中途关掉本窗口。此时导出仍在继续，宿主必须知道「反馈窗口已不存在」，
/// 以免出现「窗口没了、主窗还 disabled、看起来永不恢复」的观感 —— 见
/// <see cref="ClosedByUser"/>。</para>
/// </summary>
public sealed class ExportProgressWindow : Window
{
    private readonly TextBlock _message;
    private readonly ProgressBar _bar;
    private readonly TextBlock _hint;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    public ExportProgressWindow(string title, string hint)
    {
        Title = title;
        Width = 520;
        MinHeight = 150;
        MaxHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11151A"));

        var panel = new StackPanel { Margin = new Thickness(24) };
        _message = new TextBlock
        {
            Text = "正在准备…",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 14)
        };
        panel.Children.Add(_message);
        _bar = new ProgressBar { Height = 8, IsIndeterminate = true };
        panel.Children.Add(_bar);
        _hint = new TextBlock
        {
            Text = hint,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7F93A4")),
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(_hint);
        _cancelButton = new Button
        {
            Content = "取消导出",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = false,
        };
        _cancelButton.Click += (_, _) => RequestCancel();
        panel.Children.Add(_cancelButton);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // 关窗 = 取消：否则用户关掉本窗口后，导出仍在后台跑、主窗还被 disabled，
        // 观感就是「软件未响应且不会恢复」。
        Closed += (_, _) =>
        {
            ClosedByUser = !_finished;
            if (!_finished) RequestCancel();
        };
    }

    /// <summary>导出链的取消令牌（取消按钮 / 关窗时置位）。</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>窗口是在导出**未完成**时被用户关掉的（宿主据此恢复主窗并说明）。</summary>
    public bool ClosedByUser { get; private set; }

    /// <summary>进度上报（可由后台线程调用：<c>Progress&lt;T&gt;</c> 会 Post 回 UI 线程）。</summary>
    public void Report(string message) => _message.Text = message;

    /// <summary>进入「可取消」阶段后由调用方开启取消按钮（前奏很短，不必可取消）。</summary>
    public void EnableCancel() => _cancelButton.IsEnabled = true;

    /// <summary>追加一行提示（例如「已请求取消，正在停在下一个检查点…」）。</summary>
    public void AppendHint(string text) => _hint.Text = string.IsNullOrWhiteSpace(_hint.Text) ? text : $"{_hint.Text}\n{text}";

    private void RequestCancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* 已收尾 */ }
        _cancelButton.IsEnabled = false;
        Report("已请求取消：正在停在下一个检查点（已写出的产物会保留，临时文件会清理）…");
    }

    /// <summary>调用方收尾：标记「正常结束/已处理完」，此后关窗不再算「用户中途关闭」。</summary>
    public void MarkFinished()
    {
        _finished = true;
        try { _cancellation.Dispose(); }
        catch (ObjectDisposedException) { /* 已收尾 */ }
    }
}
