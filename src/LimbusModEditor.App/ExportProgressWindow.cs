using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>
/// 长操作进度窗口：显示阶段消息 + 不确定进度条，操作结束后由调用方关闭。
/// 用于一键导出等没有逐条进度但有阶段消息的流程，替代只更新状态栏的静默等待。
/// </summary>
public sealed class ExportProgressWindow : Window
{
    private readonly TextBlock _message;
    private readonly ProgressBar _bar;
    private readonly TextBlock _hint;

    public ExportProgressWindow(string title, string hint)
    {
        Title = title;
        Width = 520;
        MinHeight = 150;
        MaxHeight = 260;
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
        Content = panel;
    }

    /// <summary>线程安全：Progress&lt;T&gt;.Post 会把回调投递回 UI 线程。</summary>
    public void Report(string message) => _message.Text = message;
}
