using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.App;

/// <summary>P3.2 export report: per-asset outcome (applied / skipped /
/// preserved) plus package diagnostics after a completed export. The report
/// can be saved as JSON for feedback and post-mortem (P0.3).</summary>
public sealed class ExportReportWindow : Window
{
    private readonly ModExportResult _result;

    public ExportReportWindow(ModExportResult result)
    {
        _result = result;
        Title = $"导出报告 — {result.Format}";
        Width = 720;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var save = new Button { Content = "保存为 JSON…", Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += async (_, _) => await SaveAsJsonAsync();
        var reveal = new Button { Content = "打开输出位置", Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Top };
        reveal.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_result.OutputPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, $"打开资源管理器失败：{ex.Message}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(reveal, Dock.Right);
        headerRow.Children.Add(save);
        headerRow.Children.Add(reveal);
        headerRow.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Text = $"输出：{result.OutputPath}\n应用替换 {result.AppliedReplacements} 个；逐资源状态 {result.AssetStatuses.Count} 条；诊断 {result.Diagnostics.Count} 条。"
        });
        DockPanel.SetDock(headerRow, Dock.Top);
        panel.Children.Add(headerRow);

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
                Foreground = Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
        if (result.AssetStatuses.Count == 0 && result.Diagnostics.Count == 0)
            list.Children.Add(new TextBlock { Text = "（本次导出没有逐资源替换记录。）", Foreground = Brushes.Silver });

        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }

    /// <summary>P0.3: writes the structured report via the atomic-output
    /// helper so a partial JSON never lands on disk.</summary>
    private async Task SaveAsJsonAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 报告 (*.json)|*.json|文本报告 (*.txt)|*.txt",
            FileName = $"export-report-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                format = _result.Format.ToString(),
                outputPath = _result.OutputPath,
                appliedReplacements = _result.AppliedReplacements,
                diagnostics = _result.Diagnostics,
                assets = _result.AssetStatuses.Select(x => new { path = x.LogicalPath, status = x.Status, note = x.Note })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await LimbusModEditor.Application.Build.AtomicOutput.WriteAsync(dialog.FileName, Encoding.UTF8.GetBytes(json));
            MessageBox.Show(this, $"报告已保存：{dialog.FileName}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存报告失败：{ex.Message}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
