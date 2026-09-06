using System.IO;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 设置（二级窗口）：所有目录与项目元数据集中在此修改。目录由
/// 无感自动化（打开/新建项目时自动填充，左栏显示状态）获取，需要手动
/// 调整时在这里改；FMOD DLL 检测也在此触发。
/// </summary>
public sealed class ProjectSettingsWindow : Window
{
    private readonly MainWindow _ownerMain;
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
        Title = $"项目设置 — {_ownerMain.Project?.Name}";
        Width = 640;
        Height = 620;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(MakeSectionTitle("目录（自动获取在打开/新建项目时进行，手动修改在这里）"));
        (_gameBox, panel) = AddDirectoryRow(panel, "游戏目录", ("自动获取", AutoGame));
        (_cacheBox, panel) = AddDirectoryRow(panel, "Unity 缓存目录", ("自动获取", AutoCache));
        (_modsBox, panel) = AddDirectoryRow(panel, "模组目录", ("自动获取", AutoMods));
        (_fmodBox, panel) = AddDirectoryRow(panel, "FMOD DLL 目录（从不自动获取，请手动选择合法获得的 DLL）",
            ("浏览…", AutoFmod), ("检测兼容性", ProbeFmod));

        panel.Children.Add(MakeSectionTitle("模组元数据"));
        (_nameBox, panel) = AddTextBoxRow(panel, "名称");
        (_versionBox, panel) = AddTextBoxRow(panel, "版本");
        (_authorBox, panel) = AddTextBoxRow(panel, "作者");
        (_descriptionBox, panel) = AddTextBoxRow(panel, "描述");

        panel.Children.Add(MakeSectionTitle("调试行为"));
        _restoreCheck = new CheckBox { Content = "关闭编辑器时恢复被覆盖的游戏文件", Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(_restoreCheck);

        var save = new Button { Content = "保存设置", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        save.Click += (_, _) => Save();
        panel.Children.Add(save);
        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        LoadFromProject();
    }

    private void LoadFromProject()
    {
        var project = _ownerMain.Project;
        _gameBox.Text = project?.GameDirectory ?? string.Empty;
        _cacheBox.Text = project?.UnityCacheDirectory ?? string.Empty;
        _modsBox.Text = project?.ModDirectory ?? string.Empty;
        _fmodBox.Text = project?.FmodLibraryDirectory ?? string.Empty;
        _nameBox.Text = project?.Name ?? string.Empty;
        _versionBox.Text = project?.Version ?? string.Empty;
        _authorBox.Text = project?.Author ?? string.Empty;
        _descriptionBox.Text = project?.Description ?? string.Empty;
        _restoreCheck.IsChecked = project?.RestoreDebugFilesOnClose ?? true;
    }

    private void Save()
    {
        var project = _ownerMain.Project;
        if (project is null) { _status.Text = "没有打开的项目。"; return; }
        project.Name = _nameBox.Text.Trim();
        project.Version = _versionBox.Text.Trim();
        project.Author = _authorBox.Text.Trim();
        project.Description = _descriptionBox.Text.Trim();
        project.GameDirectory = _gameBox.Text.Trim();
        project.UnityCacheDirectory = _cacheBox.Text.Trim();
        project.ModDirectory = _modsBox.Text.Trim();
        project.FmodLibraryDirectory = _fmodBox.Text.Trim();
        project.RestoreDebugFilesOnClose = _restoreCheck.IsChecked == true;
        if (_saveProject(null)) _status.Text = "设置已保存。";
        else _status.Text = "保存失败，请重试。";
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

    private async void AutoFmod()
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
        await Task.CompletedTask;
    }

    /// <summary>P2.2：PE 指纹探测（从不加载 DLL），报告与本编辑器的兼容性。
    /// 从主窗口「检测 FMOD DLL」按钮迁移至此，紧跟目录配置。</summary>
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
            var projectFile = _ownerMain.ProjectFile;
            var cacheFile = string.IsNullOrWhiteSpace(projectFile)
                ? Path.Combine(Path.GetTempPath(), "LimbusModEditor", "fmod-probe.json")
                : Path.Combine(Path.GetDirectoryName(projectFile)!, "logs", "fmod-probe.json");
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
        Foreground = System.Windows.Media.Brushes.LightSteelBlue
    };
}