using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.App;

/// <summary>
/// plan-16 S6：<b>槽位导出报告</b>——列出「种类 → 格式」两层目录里写出了什么、
/// 哪些槽位被跳过以及为什么。用户可以保存成 JSON 留档（与旧导出报告同一能力）。
/// </summary>
public sealed class ModExportReportWindow : Window
{
    private readonly ModPackExportResult _result;

    public ModExportReportWindow(ModPackExportResult result)
    {
        _result = result;
        Title = $"导出报告 — {result.ModName}";
        Width = 860;
        Height = 600;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var save = new Button { Content = "保存为 JSON…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += async (_, _) => await SaveAsJsonAsync();
        var reveal = new Button { Content = "打开输出目录", Padding = new Thickness(10, 4, 10, 4) };
        reveal.Click += (_, _) => Reveal(_result.RootDirectory);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(reveal, Dock.Right);
        headerRow.Children.Add(save);
        headerRow.Children.Add(reveal);
        headerRow.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _result.WrittenSlotCount == 0
                ? $"没有任何产物写出。\n目标目录：{_result.RootDirectory}"
                : $"已写出 {_result.WrittenSlotCount} 个格式槽位 / {_result.WrittenFileCount} 个产物。\n" +
                  $"目标目录：{_result.RootDirectory}\n" +
                  $"目录：{string.Join("、", _result.WrittenGroupFolders)}",
        });
        DockPanel.SetDock(headerRow, Dock.Top);
        panel.Children.Add(headerRow);

        var list = new StackPanel();
        foreach (var slot in _result.Slots)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{slot.Descriptor.Group}-{slot.Descriptor.FolderName}　{(slot.Written ? "✔" : "✖")}　{slot.Describe()}",
                Foreground = slot.Written ? Brushes.LightGreen : Brushes.Silver,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 2),
            });
            foreach (var path in slot.OutputPaths)
                list.Children.Add(new TextBlock
                {
                    Text = $"    · {Path.GetFileName(path)}",
                    Foreground = Brushes.Gainsboro,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 1),
                });
            foreach (var diagnostic in slot.Diagnostics)
                list.Children.Add(new TextBlock
                {
                    Text = $"    ! {diagnostic}",
                    Foreground = Brushes.Khaki,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 1),
                });
            foreach (var status in slot.Statuses.Where(x => x.Status != Application.Build.ExportAssetStatus.Applied))
                list.Children.Add(new TextBlock
                {
                    Text = $"    ! [{status.Status}] {status.LogicalPath}" + (string.IsNullOrWhiteSpace(status.Note) ? string.Empty : $" — {status.Note}"),
                    Foreground = Brushes.Firebrick,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 1),
                });
        }
        list.Children.Add(new TextBlock
        {
            Text = "\n提示：产物是「种类/格式」两层的交付形态。要放进游戏，把需要的格式文件夹里的文件复制到模组目录（或直接用「使用当前修改启动游戏进行调试」）。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }

    private static void Reveal(string path)
    {
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(target)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开资源管理器失败：{ex.Message}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAsJsonAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 报告 (*.json)|*.json|文本报告 (*.txt)|*.txt",
            FileName = $"export-report-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                modName = _result.ModName,
                rootDirectory = _result.RootDirectory,
                writtenSlots = _result.WrittenSlotCount,
                writtenFiles = _result.WrittenFileCount,
                slots = _result.Slots.Select(x => new
                {
                    slot = x.Descriptor.Slot.ToString(),
                    group = x.Descriptor.Group.ToString(),
                    folder = x.Descriptor.FolderName,
                    written = x.Written,
                    count = x.ArtifactCount,
                    outputs = x.OutputPaths,
                    diagnostics = x.Diagnostics,
                    assets = x.Statuses.Select(s => new { path = s.LogicalPath, status = s.Status, note = s.Note }),
                }),
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await LimbusModEditor.Application.Build.AtomicOutput.WriteAsync(dialog.FileName, Encoding.UTF8.GetBytes(json));
            MessageBox.Show(this, $"报告已保存：{dialog.FileName}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存报告失败：{ex.Message}", "导出报告", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
