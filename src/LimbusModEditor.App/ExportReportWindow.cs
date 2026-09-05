using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.App;

/// <summary>P3.2 export report: per-asset outcome (applied / skipped /
/// preserved) plus package diagnostics after a completed export.</summary>
public sealed class ExportReportWindow : Window
{
    public ExportReportWindow(ModExportResult result)
    {
        Title = $"导出报告 — {result.Format}";
        Width = 720;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = $"输出：{result.OutputPath}\n应用替换 {result.AppliedReplacements} 个；逐资源状态 {result.AssetStatuses.Count} 条；诊断 {result.Diagnostics.Count} 条。"
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var list = new StackPanel();
        foreach (var status in result.AssetStatuses)
        {
            var brush = status.Status switch
            {
                ExportAssetStatus.Applied or ExportAssetStatus.Converted => Brushes.Green,
                ExportAssetStatus.Preserved => Brushes.DarkOrange,
                _ => Brushes.Firebrick
            };
            list.Children.Add(new TextBlock
            {
                Text = $"[{status.Status}] {status.LogicalPath}" + (string.IsNullOrEmpty(status.Note) ? string.Empty : $" — {status.Note}"),
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
        }
        foreach (var diagnostic in result.Diagnostics)
            list.Children.Add(new TextBlock
            {
                Text = $"[诊断] {diagnostic}",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
        if (result.AssetStatuses.Count == 0 && result.Diagnostics.Count == 0)
            list.Children.Add(new TextBlock { Text = "（本次导出没有逐资源替换记录。）", Foreground = Brushes.Gray });

        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }
}
