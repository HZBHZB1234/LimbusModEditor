using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.AppConfig;

namespace LimbusModEditor.App;

public enum WelcomeChoice
{
    None,
    CreateNew,
    OpenFile,
    Recent
}

public sealed record WelcomeResult(WelcomeChoice Choice, string? RecentProjectFile);

/// <summary>
/// 启动引导（傻瓜化第一步）：软件进入后没有可恢复的项目时弹出，引导用户
/// 「新建模组项目 / 打开项目 / 从最近项目继续」，并说明三步流程。
/// </summary>
public sealed class WelcomeDialog : Window
{
    public WelcomeResult? Result { get; private set; }

    public WelcomeDialog(IReadOnlyList<RecentProject> recents)
    {
        Title = "欢迎使用 Limbus Mod Editor";
        Width = 560;
        Height = 520;
        MinWidth = 480;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#11151A"));

        var panel = new StackPanel { Margin = new Thickness(32, 28, 32, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "LIMBUS MOD EDITOR",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        });
        panel.Children.Add(new TextBlock
        {
            Text = "模组创作工作台 —— 三步出模组",
            FontSize = 13,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8FA6BA")),
            Margin = new Thickness(0, 4, 0, 14)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "① 新建（或打开）模组项目\n" +
                   "② 自动扫描游戏资源（不复制文件，只建索引）\n" +
                   "③ 替换图片 / 编辑字段 → 一键导出到模组目录",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.White,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var create = MakeBigButton("新建模组项目（推荐）", "填写模组名即可，目录、游戏路径全自动配置");
        create.Click += (_, _) => Finish(new WelcomeResult(WelcomeChoice.CreateNew, null));
        panel.Children.Add(create);

        var open = MakeBigButton("打开已有项目 (.lmeproj)…", "继续上次未完成的模组");
        open.Click += (_, _) => Finish(new WelcomeResult(WelcomeChoice.OpenFile, null));
        panel.Children.Add(open);

        if (recents.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "最近项目",
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 14, 0, 4)
            });
            var list = new ListBox { Height = Math.Min(150, recents.Count * 30 + 6), Background = System.Windows.Media.Brushes.Transparent };
            foreach (var recent in recents.Take(5))
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{recent.Name}   —   {recent.Path}",
                    ToolTip = recent.Path,
                    Padding = new Thickness(6, 4, 6, 4)
                });
            list.MouseDoubleClick += (_, _) =>
            {
                if (list.SelectedItem is ListBoxItem { ToolTip: string path })
                    Finish(new WelcomeResult(WelcomeChoice.Recent, path));
            };
            panel.Children.Add(list);
            panel.Children.Add(new TextBlock
            {
                Text = "（双击打开）",
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        var skip = new Button
        {
            Content = "暂不创建，先浏览界面",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14, 5, 14, 5)
        };
        skip.Click += (_, _) => Finish(new WelcomeResult(WelcomeChoice.None, null));
        panel.Children.Add(skip);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Button MakeBigButton(string title, string subtitle)
    {
        var stack = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B9C8D6"))
        });
        return new Button
        {
            Content = stack,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
    }

    private void Finish(WelcomeResult result)
    {
        Result = result;
        DialogResult = true;
        Close();
    }
}
