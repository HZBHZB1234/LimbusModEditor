using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.App;

public sealed class UnityFieldEditorWindow : Window
{
    private sealed class Row
    {
        public string Path { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Original { get; init; } = string.Empty;
        public bool Editable { get; init; }
    }

    private readonly ObservableCollection<Row> _rows = [];
    private readonly DataGrid _grid;
    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public UnityFieldEditorWindow(IReadOnlyList<UnityFieldNode> roots, IReadOnlyDictionary<string, string>? initial = null)
    {
        Title = "Unity SerializedObject 字段编辑";
        Width = 900; Height = 620; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var root in roots) AddRows(root, initial);
        var panel = new DockPanel { Margin = new Thickness(12) };
        var hint = new TextBlock { Text = "仅修改叶子基础字段；数组、PPtr 和二进制字段请使用专用编辑器。", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        _grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = _rows, IsReadOnly = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        _grid.Columns.Add(new DataGridTextColumn { Header = "字段路径", Binding = new Binding(nameof(Row.Path)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding(nameof(Row.Type)), IsReadOnly = true, Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "值", Binding = new Binding(nameof(Row.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 260 });
        panel.Children.Add(_grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) }; cancel.Click += (_, _) => { DialogResult = false; };
        var save = new Button { Content = "保存字段修改", Width = 130, Padding = new Thickness(8, 4, 8, 4) }; save.Click += Save_Click;
        buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        Content = panel;
    }

    private void AddRows(UnityFieldNode node, IReadOnlyDictionary<string, string>? initial)
    {
        var editable = node.Children.Count == 0 && node.Value is not null && !node.Value.StartsWith("<", StringComparison.Ordinal);
        if (editable)
        {
            var value = node.Value!;
            if (initial is not null)
            {
                if (initial.TryGetValue(node.Path, out var stored)) value = stored;
                else if (node.Path.IndexOf('.') >= 0 && initial.TryGetValue(node.Path[(node.Path.IndexOf('.') + 1)..], out stored)) value = stored;
            }
            _rows.Add(new Row { Path = node.Path, Type = node.Type, Value = value, Original = node.Value!, Editable = true });
        }
        foreach (var child in node.Children) AddRows(child, initial);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        Result = _rows.Where(x => x.Editable && !string.Equals(x.Value, x.Original, StringComparison.Ordinal))
            .ToDictionary(x => x.Path, x => x.Value, StringComparer.Ordinal);
        DialogResult = true;
    }
}
