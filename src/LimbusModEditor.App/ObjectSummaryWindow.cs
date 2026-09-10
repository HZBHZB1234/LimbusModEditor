using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.App;

/// <summary>P1.5 read-only object summary (Mesh / AnimationClip / Font):
/// shows the type-tree-derived lines with a copy action and an optional JSON
/// export. The summary never invents values for fields the type tree lacks.</summary>
public sealed class ObjectSummaryWindow : Window
{
    private readonly UnityObjectSummary _summary;

    public ObjectSummaryWindow(UnityObjectSummary summary)
    {
        _summary = summary;
        Title = $"对象摘要（只读）— {summary.TypeName}（Path {summary.PathId}）";
        Width = 560;
        Height = 480;
        MinWidth = 460;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var exportButton = new Button { Content = "导出 JSON…", Padding = new Thickness(10, 5, 10, 5) };
        exportButton.Click += Export_Click;
        var copyButton = new Button { Content = "复制文本", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(_summary.Describe()); }
            catch (Exception) { /* clipboard can be locked by another process */ }
        };
        var closeButton = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        buttons.Children.Add(exportButton);
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);

        var text = new TextBox
        {
            Text = _summary.Describe(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13
        };
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON summary (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{_summary.TypeName}_Path{_summary.PathId}.json",
            Title = "导出对象摘要 JSON"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await LimbusModEditor.Application.Build.AtomicOutput.WriteAsync(dialog.FileName, System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "对象摘要导出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
