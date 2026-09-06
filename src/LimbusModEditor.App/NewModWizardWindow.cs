using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Formats;

namespace LimbusModEditor.App;

/// <summary>P3.1 new-mod wizard: collects mod identity, picks a template
/// format, scaffolds the .lmeproj project and generates a minimal,
/// handler-validated package template next to it. Formats without an honest
/// empty template are disabled with the reason shown.</summary>
public sealed class NewModWizardWindow : Window
{
    private readonly NewModTemplateService _service;
    private readonly List<(ModFormatKind Kind, string DisplayName, string Extension, string? Reason)> _templates;
    private NewModTemplateResult? _result;

    private readonly TextBox _nameBox;
    private readonly TextBox _versionBox;
    private readonly TextBox _authorBox;
    private readonly TextBox _descriptionBox;
    private readonly ComboBox _formatBox;
    private readonly TextBox _baseBankBox;
    private readonly TextBlock _baseBankLabel;
    private readonly TextBox _directoryBox;
    private readonly TextBlock _note;
    private readonly Button _createButton;

    public NewModWizardWindow(NewModTemplateService service)
    {
        _service = service;
        _templates = [.. NewModTemplateService.SupportedTemplates()];

        Title = "新建模组向导";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock { Text = "模组名称 *", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 0, 2) });
        _nameBox = new TextBox { Text = "MyMod", Height = 26 };
        panel.Children.Add(_nameBox);

        var identity = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var versionStack = new StackPanel();
        versionStack.Children.Add(new TextBlock { Text = "版本", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 0, 2) });
        _versionBox = new TextBox { Text = "1.0.0", Height = 26 };
        versionStack.Children.Add(_versionBox);
        var authorStack = new StackPanel();
        authorStack.Children.Add(new TextBlock { Text = "作者", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 0, 2) });
        _authorBox = new TextBox { Height = 26 };
        authorStack.Children.Add(_authorBox);
        Grid.SetColumn(versionStack, 0);
        Grid.SetColumn(authorStack, 2);
        identity.Children.Add(versionStack);
        identity.Children.Add(authorStack);
        panel.Children.Add(identity);

        panel.Children.Add(new TextBlock { Text = "描述", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 8, 0, 2) });
        _descriptionBox = new TextBox { AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(_descriptionBox);

        panel.Children.Add(new TextBlock { Text = "模板格式", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 8, 0, 2) });
        _formatBox = new ComboBox { Height = 26 };
        foreach (var (kind, displayName, extension, reason) in _templates)
        {
            var item = new ComboBoxItem
            {
                Content = reason is null ? $"{displayName}（{extension}）" : $"{displayName}（不可用：{reason}）",
                IsEnabled = reason is null,
                Tag = kind
            };
            _formatBox.Items.Add(item);
        }
        _formatBox.SelectedIndex = 0;
        _formatBox.SelectionChanged += (_, _) => UpdateBaseBankVisibility();
        panel.Children.Add(_formatBox);

        _baseBankLabel = new TextBlock { Text = "base_bank（目标游戏 .bank 文件名，如 common.bank）*", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 8, 0, 2), Visibility = Visibility.Collapsed };
        panel.Children.Add(_baseBankLabel);
        _baseBankBox = new TextBox { Height = 26, Visibility = Visibility.Collapsed };
        panel.Children.Add(_baseBankBox);

        panel.Children.Add(new TextBlock { Text = "项目目录 *（在此目录创建 .lmeproj 与模板文件）", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 8, 0, 2) });
        var directoryRow = new Grid();
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _directoryBox = new TextBox { Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        var browse = new Button { Content = "浏览…", Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += Browse_Click;
        Grid.SetColumn(_directoryBox, 0);
        Grid.SetColumn(browse, 1);
        directoryRow.Children.Add(_directoryBox);
        directoryRow.Children.Add(browse);
        panel.Children.Add(directoryRow);

        // 傻瓜化：默认在程序目录 projects/ 下按模组名建目录，用户零操作即可创建。
        _directoryBox.Text = DefaultDirectory(_nameBox.Text);
        _nameBox.TextChanged += (_, _) =>
        {
            if (_directoryBox.Text.StartsWith(_environment.ProjectsDirectory, StringComparison.OrdinalIgnoreCase))
                _directoryBox.Text = DefaultDirectory(_nameBox.Text);
        };

        _note = new TextBlock
        {
            Text = "说明：向导会创建项目结构（sources/、builds/ 等）并生成经过格式校验的空白模板，\n" +
                   "然后在主窗口打开新项目并自动扫描游戏资源 —— 扫描完成后即可直接编辑。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(_note);

        _createButton = new Button { Content = "创建项目与模板", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 14, 0, 0), FontWeight = FontWeights.SemiBold };
        _createButton.Click += Create_Click;
        panel.Children.Add(_createButton);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static readonly LimbusModEditor.Application.AppConfig.AppEnvironment _environment =
        LimbusModEditor.Application.AppConfig.AppEnvironment.Current;

    private static string DefaultDirectory(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "MyMod";
        return System.IO.Path.Combine(_environment.ProjectsDirectory, safe);
    }

    public NewModTemplateResult? Result => _result;

    private void UpdateBaseBankVisibility()
    {
        var kind = SelectedKind();
        var isRebank = kind is ModFormatKind.Rebank;
        _baseBankLabel.Visibility = isRebank ? Visibility.Visible : Visibility.Collapsed;
        _baseBankBox.Visibility = isRebank ? Visibility.Visible : Visibility.Collapsed;
    }

    private ModFormatKind SelectedKind()
        => _formatBox.SelectedItem is ComboBoxItem { Tag: ModFormatKind kind } ? kind : ModFormatKind.Unknown;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LME 项目 (*.lmeproj)|*.lmeproj",
            FileName = "MyMod.lmeproj",
            Title = "选择新项目 .lmeproj 的位置（目录将作为项目根）"
        };
        if (dialog.ShowDialog() == true)
            _directoryBox.Text = System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text.Trim();
        var directory = _directoryBox.Text.Trim();
        if (name.Length == 0) { _note.Text = "请输入模组名称。"; return; }
        if (directory.Length == 0) { _note.Text = "请选择项目目录。"; return; }
        var request = new NewModTemplateRequest(
            name, _versionBox.Text, _authorBox.Text, _descriptionBox.Text,
            SelectedKind(), _baseBankBox.Text);
        _createButton.IsEnabled = false;
        _note.Text = "正在创建项目与模板…";
        try
        {
            _result = await _service.CreateAsync(request, directory);
            _note.Text = $"已创建：\n{_result.ProjectFile}\n{_result.TemplateFile}" +
                (_result.Diagnostics.Count > 0 ? "\n\n模板校验提示：\n" + string.Join("\n", _result.Diagnostics) : string.Empty);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _note.Text = $"创建失败：{ex.Message}";
        }
        finally
        {
            _createButton.IsEnabled = true;
        }
    }
}
