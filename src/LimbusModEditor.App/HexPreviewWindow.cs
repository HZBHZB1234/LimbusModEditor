using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.App;

/// <summary>P3.4: read-only hex dump of a binary asset's first bytes plus its
/// original/modified hashes, so users can identify unknown payloads before
/// deciding what to do with them.</summary>
public sealed class HexPreviewWindow : Window
{
    private const int MaxBytes = 4096;

    public HexPreviewWindow(AssetRecord asset)
    {
        Title = $"十六进制预览 — {asset.LogicalPath}";
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var source = ResolveSource(asset);
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brushes.DimGray,
            Text = source is null
                ? $"资源没有可用的源文件（LogicalPath={asset.LogicalPath}）。"
                : $"文件：{source}\n大小 {new FileInfo(source).Length:N0} 字节（预览前 {MaxBytes} 字节）" +
                  $"\n原始哈希：{asset.OriginalHash ?? "无"}\n当前哈希：{asset.ModifiedHash ?? asset.OriginalHash ?? "无"}"
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var text = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = source is null ? "（无文件）" : Dump(File.ReadAllBytes(source))
        };
        panel.Children.Add(text);
        Content = panel;
    }

    private static string? ResolveSource(AssetRecord asset)
    {
        if (asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement)) return replacement;
        return !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) ? asset.SourcePath : null;
    }

    /// <summary>Classic 16-byte-per-row dump: offset, hex pairs, ASCII gutter.</summary>
    private static string Dump(byte[] data)
    {
        var text = new StringBuilder();
        var shown = Math.Min(data.Length, MaxBytes);
        for (var offset = 0; offset < shown; offset += 16)
        {
            text.Append(offset.ToString("X8"));
            text.Append("  ");
            var rowEnd = Math.Min(offset + 16, shown);
            for (var i = offset; i < offset + 16; i++)
            {
                if (i < rowEnd) text.Append(data[i].ToString("X2")).Append(' ');
                else text.Append("   ");
                if ((i - offset + 1) % 8 == 0) text.Append(' ');
            }
            text.Append(' ');
            var ascii = new StringBuilder();
            for (var i = offset; i < rowEnd; i++)
            {
                var b = data[i];
                ascii.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }
            text.AppendLine(ascii.ToString());
        }
        if (data.Length > MaxBytes) text.AppendLine($"\n… 已截断，共 {data.Length:N0} 字节（只读预览，显示前 {MaxBytes} 字节）。");
        return text.ToString();
    }
}
