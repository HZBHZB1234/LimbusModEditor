using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.App;

/// <summary>Lets the user choose between verified Unity-cache candidate
/// directories (each already checked to contain .bundle files).</summary>
public sealed class UnityCachePickerWindow : Window
{
    public UnityCacheCandidate? Selected { get; private set; }

    public UnityCachePickerWindow(IReadOnlyList<UnityCacheCandidate> candidates)
    {
        Title = "自动建议 Unity 缓存目录";
        Width = 640;
        Height = 380;
        MinWidth = 480;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock
        {
            Text = "以下候选目录经验证实际包含 Unity bundle 文件。选择一个作为项目的 Unity 缓存目录：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var list = new ListBox { Background = Brushes.Transparent };
        foreach (var candidate in candidates)
            list.Items.Add(new ListBoxItem
            {
                Content = $"{candidate.BundleCount,4} 个 bundle — {candidate.Path}",
                Tag = candidate
            });
        var ok = new Button { Content = "使用所选目录", Padding = new Thickness(14, 5, 14, 5), IsDefault = true, IsEnabled = false };
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: UnityCacheCandidate chosen }) { Selected = chosen; DialogResult = true; }
        };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is not null;
        panel.Children.Add(list);
        Content = panel;
    }
}
