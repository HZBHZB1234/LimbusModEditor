using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>P2.2 compatibility report for the user-provided FMOD DLL directory:
/// machine type, version, export snapshot and which required symbols are
/// missing. The report is read-only and never loads the DLLs.</summary>
public sealed class FmodReportWindow : Window
{
    public FmodReportWindow(string directory, FmodProbeCache cache)
    {
        Title = $"FMOD DLL 兼容性检测 — {directory}";
        Width = 720;
        Height = 440;
        MinWidth = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brushes.Gainsboro,
            Text = $"探测时间：{cache.ProbedAt:yyyy-MM-dd HH:mm:ss}（结果按 DLL 大小/时间戳缓存，目录变化后自动重新探测）\n" +
                   "本工具不附带、不加载、不修改任何 FMOD 二进制文件；此报告只读取文件头与导出表。"
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var list = new StackPanel();
        foreach (var report in cache.Reports)
        {
            var card = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 216, 222)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var title = new TextBlock { Text = report.Summary(), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var stack = new StackPanel { Children = { title } };
            if (report.Error is not null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"错误：{report.Error}",
                    Foreground = Brushes.Firebrick,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            else if (report.Present)
            {
                if (report.MissingDecodeExports.Count > 0)
                    stack.Children.Add(new TextBlock
                    {
                        Text = "缺少解码符号：" + string.Join(", ", report.MissingDecodeExports),
                        Foreground = new SolidColorBrush(Color.FromRgb(184, 134, 11)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                if (report.MissingEncodeExports.Count > 0)
                    stack.Children.Add(new TextBlock
                    {
                        Text = "缺少 FSB 编码符号：" + string.Join(", ", report.MissingEncodeExports),
                        Foreground = new SolidColorBrush(Color.FromRgb(184, 134, 11)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                if (report.Machine == "x86")
                    stack.Children.Add(new TextBlock
                    {
                        Text = "⚠ 该 DLL 是 32 位（x86），本编辑器为 64 位进程，无法加载它。",
                        Foreground = Brushes.Firebrick,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
            }
            card.Child = stack;
            list.Children.Add(card);
        }
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }
}
