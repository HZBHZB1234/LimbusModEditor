using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>P3.2 export wizard: source-format detection, the source→target
/// compatibility matrix with reasons for disabled targets, asset/replacement
/// counts and an output path preview before anything is written.</summary>
public sealed class ExportWizardWindow : Window
{
    /// <summary>Chosen target and output path; null when cancelled.</summary>
    public (ModFormatKind Target, string OutputPath)? Result { get; private set; }

    private readonly ModFormatKind _source;
    private readonly ModProject _project;
    private readonly string? _sourcePath;
    private readonly Dictionary<ModFormatKind, RadioButton> _targetChoices = [];
    private readonly Dictionary<ModFormatKind, TextBlock> _reasonTexts = [];
    private readonly TextBox _outputBox = new() { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 320 };

    public ExportWizardWindow(ModProject project, string? sourcePath)
    {
        _project = project;
        _sourcePath = sourcePath;
        Title = "导出向导";
        Width = 700;
        Height = 560;
        MinWidth = 560;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;
        _source = sourcePath is null ? ModFormatKind.Unknown : ExportMatrix.GuessSourceKind(sourcePath);

        var panel = new DockPanel { Margin = new Thickness(14) };

        var sourceHeader = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = $"源格式：{(_source == ModFormatKind.Unknown ? "未指定（将使用项目最近导入的源）" : _source.ToString())}"
        };
        DockPanel.SetDock(sourceHeader, Dock.Top);
        panel.Children.Add(sourceHeader);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var browse = new Button { Content = "浏览输出…", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        browse.Click += (_, _) => PickOutput();
        var export = new Button { Content = "开始导出", Padding = new Thickness(18, 5, 18, 5), IsDefault = true };
        export.Click += (_, _) => Confirm();
        buttons.Children.Add(browse);
        buttons.Children.Add(export);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        var outputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        outputRow.Children.Add(new TextBlock { Text = "输出文件:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        outputRow.Children.Add(_outputBox);
        DockPanel.SetDock(outputRow, Dock.Bottom);
        panel.Children.Add(outputRow);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();
        foreach (var compatibility in ExportMatrix.MatrixFor(_source))
        {
            var row = new RadioButton
            {
                GroupName = "target",
                Content = $"{compatibility.Target}（{ExtensionFor(compatibility.Target)}）",
                IsEnabled = compatibility.Supported,
                Margin = new Thickness(0, 6, 0, 2),
                FontWeight = FontWeights.SemiBold
            };
            row.Checked += (_, _) => SuggestOutputExtension(compatibility.Target);
            _targetChoices[compatibility.Target] = row;
            var reason = new TextBlock
            {
                Text = (compatibility.Supported ? "✓ " : "✗ ") + compatibility.Reason,
                Margin = new Thickness(20, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                Foreground = compatibility.Supported ? Brushes.Gainsboro : new SolidColorBrush(Color.FromRgb(178, 34, 34))
            };
            _reasonTexts[compatibility.Target] = reason;
            list.Children.Add(row);
            list.Children.Add(reason);
        }
        list.Children.Add(BuildSummary());
        scroll.Content = list;
        panel.Children.Add(scroll);
        Content = panel;

        var firstEnabled = _targetChoices.Values.FirstOrDefault(x => x.IsEnabled);
        if (firstEnabled is not null) firstEnabled.IsChecked = true;
        if (_sourcePath is not null) _outputBox.Text = SuggestedOutput(_source);
    }

    private TextBlock BuildSummary()
    {
        var replacements = _project.Assets.Count(x => x.Metadata.ContainsKey("replacementPath"));
        var unityEdits = _project.Assets.Count(x => x.Metadata.ContainsKey("unityFieldEdits"));
        var sprites = _project.Assets.Count(x => x.Metadata.ContainsKey("spriteMetadata"));
        var lines = $"项目概况：资源 {_project.Assets.Count} 个，替换素材 {replacements} 个，Unity 字段编辑 {unityEdits} 个，" +
                    $"Sprite 元数据 {sprites} 个。\n" +
                    "导出前会先做字段预校验与（Unity 包）重打包后引用完整性检查；构建过程中任何破坏引用的改动都会中止导出。";
        return new TextBlock
        {
            Text = lines,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(31, 78, 121))
        };
    }

    private void SuggestOutputExtension(ModFormatKind target)
    {
        if (string.IsNullOrWhiteSpace(_outputBox.Text)) return;
        _outputBox.Text = Path.ChangeExtension(_outputBox.Text, ExtensionFor(target));
    }

    private static string ExtensionFor(ModFormatKind kind) => kind switch
    {
        ModFormatKind.Carra => ".carra",
        ModFormatKind.Carra2 => ".carra2",
        ModFormatKind.Rebank => ".rebank",
        ModFormatKind.Bank => ".bank",
        ModFormatKind.Lunartique => ".zip",
        _ => ".bin"
    };

    private static string SuggestedOutput(ModFormatKind source) => $"export{ExtensionFor(source == ModFormatKind.Unknown ? ModFormatKind.Carra2 : source)}";

    private void PickOutput()
    {
        var chosen = _targetChoices.FirstOrDefault(x => x.Value.IsChecked == true).Key;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = $"{chosen} 文件 (*{ExtensionFor(chosen)})|*{ExtensionFor(chosen)}|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(_outputBox.Text is { Length: > 0 } ? _outputBox.Text : SuggestedOutput(chosen))
        };
        if (dialog.ShowDialog() == true) _outputBox.Text = dialog.FileName;
    }

    private void Confirm()
    {
        var chosen = _targetChoices.FirstOrDefault(x => x.Value.IsChecked == true).Key;
        if (chosen == ModFormatKind.Unknown)
        {
            MessageBox.Show(this, "请选择一个支持的输出格式。", "导出向导", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_outputBox.Text))
        {
            MessageBox.Show(this, "请指定输出文件路径。", "导出向导", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var compatibility = ExportMatrix.Evaluate(_source, chosen);
        if (!compatibility.Supported)
        {
            MessageBox.Show(this, compatibility.Reason, "该目标格式不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = (chosen, _outputBox.Text.Trim());
        DialogResult = true;
    }
}
