using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.App;

/// <summary>导出思路窗口（P3.10）：列出针对当前项目自动分析出的出口建议
/// （推荐项排最前），用户点「用这个」即走对应通道。不可用的建议给出中文原因，
/// 不让用户对着灰按钮猜。</summary>
public sealed class ExportAdvisorWindow : Window
{
    public ExportAdvisorWindow(IReadOnlyList<ExportIdea> ideas)
    {
        Title = "导出思路（自动分析）";
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)),
            Text = "下面是按当前项目内容自动挑出的导出出口。推荐项排在最前面，"
                 + "选一个直接用即可；想看细节也可以先取消，用左栏的对应按钮自己来。",
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => { DialogResult = false; };
        DockPanel.SetDock(cancel, Dock.Bottom);
        panel.Children.Add(cancel);

        var list = new StackPanel();
        foreach (var idea in ideas) list.Children.Add(BuildRow(idea));
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

        Content = panel;
    }

    /// <summary>用户选中的出口；未选择（取消）时为 null。</summary>
    public ExportIdea? Result { get; private set; }

    private UIElement BuildRow(ExportIdea idea)
    {
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(idea.Recommended ? Color.FromRgb(0x4C, 0x8D, 0xDA) : Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(idea.Recommended ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x24)),
        };

        var grid = new DockPanel();
        var button = new Button
        {
            Content = "用这个",
            Padding = new Thickness(14, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = idea.Enabled,
        };
        button.Click += (_, _) => { Result = idea; DialogResult = true; };
        DockPanel.SetDock(button, Dock.Right);
        grid.Children.Add(button);

        var text = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = idea.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        if (idea.Recommended)
            title.Children.Add(new TextBlock
            {
                Text = "  推荐",
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xDA)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = idea.Summary,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = idea.Enabled ? idea.Detail : $"⚠ {idea.BlockedReason}",
            Foreground = new SolidColorBrush(idea.Enabled ? Color.FromRgb(0x8F, 0xA6, 0xBA) : Color.FromRgb(0xE0, 0x8A, 0x5A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11.5,
        });
        grid.Children.Add(text);

        card.Child = grid;
        return card;
    }
}
