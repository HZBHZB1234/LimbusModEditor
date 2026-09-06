using System.IO;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 设置（二级窗口）：目录部分是<b>全局共享设置</b>（保存在程序目录
/// config/shared-config.json，所有项目共用，自动获取 + 手动微调都在这里）；
/// 模组元数据与调试行为属于当前项目。FMOD DLL 目录留空时自动使用随包
/// DLL（程序目录 fmod/）或游戏自带运行库，手动指定永远优先。
/// </summary>
public sealed class ProjectSettingsWindow : Window
{
    private readonly MainWindow _ownerMain;
    private readonly AppEnvironment _env;
    private readonly Func<string?, bool> _saveProject;
    private readonly TextBox _gameBox;
    private readonly TextBox _cacheBox;
    private readonly TextBox _modsBox;
    private readonly TextBox _fmodBox;
    private readonly TextBox _nameBox;
    private readonly TextBox _versionBox;
    private readonly TextBox _authorBox;
    private readonly TextBox _descriptionBox;
    private readonly CheckBox _restoreCheck;
    private readonly TextBlock _status;

    public ProjectSettingsWindow(MainWindow owner, Func<string?, bool> saveProject)
    {
        _ownerMain = owner;
        _saveProject = saveProject;
        _env = AppEnvironment.Current;
        Title = "设置 — 共享目录与项目元数据";
        Width = 660;
        Height = 640;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(MakeSectionTitle(
            "共享目录（保存在程序目录，所有项目共用；自动获取失败时在这里手动填写）"));
        (_gameBox, panel) = AddDirectoryRow(panel, "游戏目录", ("自动获取", AutoGame));
        (_cacheBox, panel) = AddDirectoryRow(panel, "Unity 缓存目录", ("自动获取", AutoCache));
        (_modsBox, panel) = AddDirectoryRow(panel, "模组目录", ("自动获取", AutoMods));
        (_fmodBox, panel) = AddDirectoryRow(panel, "FMOD DLL 目录（留空 = 自动使用随包/游戏自带 DLL）",
            ("浏览…", AutoFmod), ("恢复自动发现", ClearFmod), ("检测兼容性", ProbeFmod));
        var openBase = new Button
        {
            Content = "打开程序目录（共享配置 / 缓存所在位置）",
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openBase.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", _env.BaseDirectory);
        panel.Children.Add(openBase);

        panel.Children.Add(MakeSectionTitle("当前项目元数据"));
        (_nameBox, panel) = AddTextBoxRow(panel, "名称");
        (_versionBox, panel) = AddTextBoxRow(panel, "版本");
        (_authorBox, panel) = AddTextBoxRow(panel, "作者");
        (_descriptionBox, panel) = AddTextBoxRow(panel, "描述");

        panel.Children.Add(MakeSectionTitle("调试行为（当前项目）"));
        _restoreCheck = new CheckBox { Content = "关闭编辑器时恢复被覆盖的游戏文件", Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(_restoreCheck);

        var save = new Button { Content = "保存设置", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        save.Click += (_, _) => Save();
        panel.Children.Add(save);
        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        LoadValues();
    }

    private void LoadValues()
    {
        var project = _ownerMain.Project;
        // 显示生效值：共享配置优先，回退旧项目字段（保存时会写入共享配置）。
        _gameBox.Text = _env.EffectiveGameDirectory(project) ?? string.Empty;
        _cacheBox.Text = _env.EffectiveUnityCacheDirectory(project) ?? string.Empty;
        _modsBox.Text = _env.EffectiveModDirectory(project) ?? string.Empty;
        _fmodBox.Text = _env.EffectiveFmodLibraryDirectory(project) ?? string.Empty;
        _nameBox.Text = project?.Name ?? string.Empty;
        _versionBox.Text = project?.Version ?? string.Empty;
        _authorBox.Text = project?.Author ?? string.Empty;
        _descriptionBox.Text = project?.Description ?? string.Empty;
        _restoreCheck.IsChecked = project?.RestoreDebugFilesOnClose ?? true;
    }

    private void Save()
    {
        var project = _ownerMain.Project;
        // 共享目录写进共享配置（程序目录），所有项目立即生效。
        _env.Config.GameDirectory = _gameBox.Text.Trim();
        _env.Config.UnityCacheDirectory = _cacheBox.Text.Trim();
        _env.Config.ModDirectory = _modsBox.Text.Trim();
        _env.Config.FmodLibraryDirectory = _fmodBox.Text.Trim();
        _env.Save();
        if (project is not null)
        {
            project.Name = _nameBox.Text.Trim();
            project.Version = _versionBox.Text.Trim();
            project.Author = _authorBox.Text.Trim();
            project.Description = _descriptionBox.Text.Trim();
            project.RestoreDebugFilesOnClose = _restoreCheck.IsChecked == true;
        }
        if (_saveProject(null)) _status.Text = "设置已保存（共享目录 → 程序目录；元数据 → 项目）。";
        else _status.Text = "共享目录已保存；项目元数据保存失败，请重试。";
        _ownerMain.RefreshDirectoryLabels();
    }

    private void AutoGame()
    {
        var lookup = GameDirectoryLocator.Scan(GameDirectoryLocator.DefaultCandidateRoots());
        if (lookup.Found) { _gameBox.Text = lookup.GameDirectory!; _status.Text = lookup.Method; }
        else _status.Text = "未在已知 Steam 库中找到 LimbusCompany.exe。";
    }

    private void AutoCache()
    {
        var candidates = UnityCacheLocator.SuggestCandidates(_gameBox.Text.Trim());
        if (candidates.Count == 0) { _status.Text = "未找到包含缓存条目的目录。"; return; }
        if (candidates.Count == 1)
        {
            _cacheBox.Text = candidates[0].Path;
            _status.Text = $"已获取缓存目录（{candidates[0].EntryCount} 个条目）。";
            return;
        }
        // 多个候选时弹出选择器（与无感自动化同一套已验证候选列表）。
        var picker = new UnityCachePickerWindow(candidates) { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is not null)
        {
            _cacheBox.Text = picker.Selected.Path;
            _status.Text = $"已获取缓存目录（{picker.Selected.EntryCount} 个条目）。";
        }
    }

    private void AutoMods()
    {
        var candidates = ModDirectoryLocator.SuggestCandidates();
        if (candidates.Count == 0) { _status.Text = "未找到真实加载器模组目录（%APPDATA%\\LimbusCompanyMods）。"; return; }
        _modsBox.Text = candidates[0];
        _status.Text = $"已建议模组目录（{ModDirectoryLocator.CountEntries(candidates[0])} 个条目）。";
    }

    private void AutoFmod()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择含 fmod64.dll / fsbank64.dll 的目录",
            FileName = "选择此文件夹",
            CheckFileExists = false,
            ValidateNames = false
        };
        if (dialog.ShowDialog() != true) return;
        var dir = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(dir)) _fmodBox.Text = dir;
    }

    private void ClearFmod()
    {
        _fmodBox.Text = string.Empty;
        _status.Text = "已清除手动指定：将自动使用随包 DLL（程序目录 fmod/）或游戏自带运行库。";
    }

    /// <summary>P2.2：PE 指纹探测（从不加载 DLL），报告与本编辑器的兼容性。
    /// 探测缓存保存在程序目录 cache/ 下，按 DLL 指纹去重。</summary>
    private void ProbeFmod()
    {
        var dir = _fmodBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _status.Text = "请先填写存在的 FMOD DLL 目录（含 fmod64.dll / fsbank64.dll）。";
            return;
        }
        try
        {
            Directory.CreateDirectory(_env.CacheDirectory);
            var cacheFile = Path.Combine(_env.CacheDirectory, "fmod-probe.json");
            var cache = new FmodCompatibilityService().Probe(dir, cacheFile);
            new FmodReportWindow(dir, cache) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { _status.Text = $"FMOD 检测失败：{ex.Message}"; }
    }

    private static (TextBox, StackPanel) AddDirectoryRow(StackPanel panel, string label, params (string Text, Action Click)[] buttons)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.Gray });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        var box = new TextBox { Width = 380, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        foreach (var (text, click) in buttons)
        {
            var btn = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            btn.Click += (_, _) => click();
            row.Children.Add(btn);
        }
        panel.Children.Add(row);
        return (box, panel);
    }

    private static (TextBox, StackPanel) AddTextBoxRow(StackPanel panel, string label)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.Gray });
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(box);
        return (box, panel);
    }

    private static TextBlock MakeSectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 12, 0, 4),
        Foreground = System.Windows.Media.Brushes.LightSteelBlue,
        TextWrapping = TextWrapping.Wrap
    };
}
