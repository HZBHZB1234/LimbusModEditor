using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.App;

/// <summary>多格式项目「导出全部」的逐来源结果报告。</summary>
public sealed class MultiExportReportWindow : Window
{
    public MultiExportReportWindow(MultiFormatExportResult result, string outputDirectory)
    {
        Title = "导出全部 — 结果报告";
        Width = 680;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"共 {result.Items.Count} 个来源：成功 {result.SucceededCount}，失败 {result.FailedCount}。输出目录：{outputDirectory}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var list = new ListBox { Height = 320 };
        foreach (var item in result.Items)
        {
            var detail = item.Succeeded
                ? $"✅ {item.SourceName}（{item.Format}）→ {item.OutputPath}（应用 {item.Result?.AppliedReplacements} 个替换）"
                : $"⛔ {item.SourceName}（{item.Format}）：{item.Error}";
            list.Items.Add(new ListBoxItem { Content = detail });
        }
        panel.Children.Add(list);
        var openDir = new Button { Content = "打开输出目录", Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        openDir.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", outputDirectory) { UseShellExecute = true }); } catch { } };
        panel.Children.Add(openDir);
        Content = panel;
    }
}
