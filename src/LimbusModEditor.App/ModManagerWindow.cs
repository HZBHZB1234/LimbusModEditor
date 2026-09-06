using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.App;

/// <summary>管理真实加载器模组目录（%APPDATA%\LimbusCompanyMods）：
/// 列出已安装模组并按加载器自身的 "_disable" 后缀约定切换启用/禁用。
/// 只做重命名，不修改任何文件内容。</summary>
public sealed class ModManagerWindow : Window
{
    private readonly string _modsDirectory;
    private readonly ListBox _list;
    private readonly TextBlock _status;

    public ModManagerWindow(string modsDirectory)
    {
        _modsDirectory = modsDirectory;
        Title = $"模组目录管理 — {modsDirectory}";
        Width = 620;
        Height = 460;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "真实加载器（LCTA launcher）通过 \"_disable\" 后缀切换模组。此处只重命名条目，不改文件内容。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray
        });
        _list = new ListBox { Height = 280, Margin = new Thickness(0, 10, 0, 10) };
        _list.MouseDoubleClick += async (_, _) => await ToggleAsync();
        panel.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = new Button { Content = "切换 启用/禁用 (Ctrl+T)", Padding = new Thickness(12, 6, 12, 6) };
        toggle.Click += async (_, _) => await ToggleAsync();
        var refresh = new Button { Content = "刷新", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        refresh.Click += (_, _) => Refresh();
        buttons.Children.Add(toggle);
        buttons.Children.Add(refresh);
        panel.Children.Add(buttons);
        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray };
        panel.Children.Add(_status);
        Content = panel;
        Refresh();
    }

    private void Refresh()
    {
        _list.Items.Clear();
        try
        {
            foreach (var entry in ModInstallService.ListInstalled(_modsDirectory))
            {
                var item = new ListBoxItem { Tag = entry, Content = Describe(entry) };
                _list.Items.Add(item);
            }
            _status.Text = $"共 {_list.Items.Count} 个条目。";
        }
        catch (Exception ex)
        {
            _status.Text = $"读取失败: {ex.Message}";
        }
    }

    private async Task ToggleAsync()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: InstalledModEntry entry }) return;
        try
        {
            var updated = await Task.Run(() => ModInstallService.SetEnabled(_modsDirectory, entry, entry.IsDisabled));
            _status.Text = $"已{(entry.IsDisabled ? "启用" : "禁用")}: {updated.Name}";
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = $"切换失败: {ex.Message}";
        }
    }

    private static string Describe(InstalledModEntry entry)
        => $"{(entry.IsDisabled ? "⛔" : "✅")} {entry.Name}{(entry.IsDirectory ? "（目录）" : string.Empty)}";
}
