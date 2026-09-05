using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>P2.1 read-only Bank structure inspector: shows the selected FSB's
/// FSB5 header, per-sample entries (rate, channels, codec, sizes, offsets) and
/// diagnostics. Unknown or encrypted payloads are explained, never guessed.</summary>
public sealed class BankInspectorWindow : Window
{
    private sealed class SampleRow
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Format { get; init; } = string.Empty;
        public string SampleRate { get; init; } = string.Empty;
        public string Channels { get; init; } = string.Empty;
        public string DataSize { get; init; } = string.Empty;
        public string DataOffset { get; init; } = string.Empty;
        public string EntrySize { get; init; } = string.Empty;
    }

    public BankInspectorWindow(AssetRecord asset, BankFsbInspection inspection)
    {
        Title = $"FSB 结构检查 — {asset.LogicalPath}";
        Width = 780;
        Height = 560;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(12) };

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 13
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ItemsSource = BuildRows(inspection.Fsb)
        };
        foreach (var (header, binding, width) in new[]
        {
            ("#", nameof(SampleRow.Index), 40),
            ("名称", nameof(SampleRow.Name), 150),
            ("编码", nameof(SampleRow.Format), 90),
            ("采样率", nameof(SampleRow.SampleRate), 70),
            ("声道", nameof(SampleRow.Channels), 140),
            ("数据大小", nameof(SampleRow.DataSize), 90),
            ("数据偏移", nameof(SampleRow.DataOffset), 110),
            ("条目长度", nameof(SampleRow.EntrySize), 80)
        })
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(binding),
                Width = width
            });
        }
        panel.Children.Add(grid);

        var diagnostics = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(184, 134, 11)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(diagnostics, Dock.Bottom);
        var diagnosticText = inspection.UnresolvedReason is not null
            ? inspection.UnresolvedReason
            : inspection.Fsb is { Diagnostics.Count: > 0 }
                ? string.Join(Environment.NewLine, inspection.Fsb.Diagnostics)
                : string.Empty;
        diagnostics.Text = diagnosticText;
        diagnostics.Visibility = string.IsNullOrEmpty(diagnosticText) ? Visibility.Collapsed : Visibility.Visible;
        panel.Children.Add(diagnostics);

        var note = new TextBlock
        {
            Text = "此视图为只读结构检查；解码/替换音频仍需配置合法的 FMOD/FSBANK DLL。",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(note, Dock.Bottom);
        panel.Children.Add(note);

        if (inspection.UnresolvedReason is not null)
        {
            summary.Text = $"FSB 大小: {inspection.FsbSize:N0} 字节\n{inspection.UnresolvedReason}";
            summary.Foreground = Brushes.Firebrick;
            grid.Visibility = Visibility.Collapsed;
        }
        else if (inspection.Fsb is { } fsb)
        {
            var names = fsb.Samples.Count(s => s.Name is not null);
            var sizeWarning = fsb.SizeConsistent
                ? string.Empty
                : Environment.NewLine + "⚠ " + (fsb.Diagnostics.FirstOrDefault(d => d.Contains("长度不一致")) ?? "头/条目/名称/数据四区长度之和与文件大小不符。");
            summary.Text = $"FSB5 版本 {fsb.Version}（基头 0x{fsb.BaseHeaderSize:X}）｜ 样本 {fsb.SampleCount} ｜ 条目区 0x{fsb.SampleHeaderSize:X} 字节 ｜ " +
                           $"名称 {names}/{fsb.Samples.Count} ｜ 编码 {fsb.CodecName} ｜ 数据区 0x{fsb.DataStartOffset:X} 起共 0x{fsb.DataSize:X} 字节" + sizeWarning;
            summary.Foreground = Brushes.DimGray;
        }

        Content = panel;
    }

    private static SampleRow[] BuildRows(Fsb5Info? fsb)
    {
        if (fsb is null) return [];
        return fsb.Samples.Select(s => new SampleRow
        {
            Index = s.Index,
            Name = s.Name ?? "—",
            Format = fsb.CodecName,
            SampleRate = $"{s.SampleRate} Hz",
            Channels = s.Channels.ToString(),
            DataSize = s.DataSize is { } size ? $"{size:N0}" : "不可知",
            DataOffset = $"0x{s.DataOffset:X}",
            EntrySize = $"0x{s.EntrySize:X}"
        }).ToArray();
    }
}
