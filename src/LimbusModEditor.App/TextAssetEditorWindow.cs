using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Assets;

namespace LimbusModEditor.App;

/// <summary>文本 / JSON 资源的内置编辑器（P3.10）：选中文本类资源双击即打开，
/// 直接在编辑器里改内容，保存走 <see cref="TextAssetEditService"/>（与替换文件
/// 同一条可撤销 / 可导出的编辑管道）。JSON 保存前会校验并格式化，语法错误
/// 留在窗口里报中文原因，不写坏项目。
/// <para>文本资产（Unity TextAsset）当前不支持写回，这类资源会在此窗口里
/// 明确说明；导入的 .txt/.json/.csv 资源可以直接改。</para></summary>
public sealed class TextAssetEditorWindow : Window
{
    private readonly TextAssetDocument _document;
    private readonly TextBox _editor;
    private readonly TextBlock _status;

    public TextAssetEditorWindow(TextAssetDocument document, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        Title = $"编辑文本 — {displayPath}";
        Width = 860;
        Height = 620;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(12) };

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "保存并登记替换", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold };
        save.Click += (_, _) => TrySave();
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };
        footer.Children.Add(save);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xBA)),
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"编码 {document.Encoding.WebName.ToUpperInvariant()}"
                 + (document.IsJson ? " · JSON（保存时校验并格式化）" : string.Empty)
                 + " · 保存后修改只记在项目里，导出时才写入模组",
        };
        DockPanel.SetDock(_status, Dock.Top);
        panel.Children.Add(_status);

        _editor = new TextBox
        {
            Text = document.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
        };
        panel.Children.Add(_editor);

        Content = panel;
        Loaded += (_, _) => { _editor.Focus(); _editor.CaretIndex = 0; };
    }

    /// <summary>用户确认后的文档（含编辑后的正文）；取消时为 null。</summary>
    public TextAssetDocument? Result { get; private set; }

    private void TrySave()
    {
        var text = _editor.Text;
        if (_document.IsJson)
        {
            try
            {
                using var _ = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x5A));
                _status.Text = $"JSON 语法错误，未保存：{ex.Message}";
                return;
            }
        }
        Result = _document with { Text = text };
        DialogResult = true;
    }
}
