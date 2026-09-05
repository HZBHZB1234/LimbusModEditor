using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.App;

/// <summary>
/// Field editor for Unity serialized objects. Shows the field tree with the
/// real serialized types, validates new values against them, lets the user
/// select which modified fields to apply, and can revert rows to their
/// original values before saving.
/// </summary>
public sealed class UnityFieldEditorWindow : Window
{
    private sealed class Row : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        private string _state = string.Empty;
        private bool _selected;

        public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }
        public string Path { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Original { get; init; } = string.Empty;
        public bool Editable { get; init; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                Raise(nameof(Value));
                if (State.StartsWith("错误", StringComparison.Ordinal)) State = string.Empty;
                else State = Modified ? "已修改" : string.Empty;
            }
        }

        public string State
        {
            get => _state;
            set { if (_state != value) { _state = value; Raise(nameof(State)); Raise(nameof(HasError)); } }
        }

        public bool Modified => !string.Equals(_value, Original, StringComparison.Ordinal);
        public bool HasError => _state.StartsWith("错误", StringComparison.Ordinal);
        public UnityFieldNode Node { get; init; } = null!;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<Row> _rows = [];
    private readonly ICollectionView _view;
    private readonly DataGrid _grid;
    private readonly TextBlock _errorText;
    private bool _showAll;

    /// <summary>The selected, modified and valid drafts; null when cancelled.</summary>
    public IReadOnlyList<UnityFieldEditDraft>? Result { get; private set; }

    public UnityFieldEditorWindow(IReadOnlyList<UnityFieldNode> roots, UnityFieldEditSet? initial,
        UnityScriptInfo? scriptInfo = null)
    {
        Title = "Unity SerializedObject 字段编辑";
        Width = 1060; Height = 680; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(12) };

        if (scriptInfo is not null)
        {
            var scriptHeader = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            var segments = new List<string>();
            if (!string.IsNullOrWhiteSpace(scriptInfo.TypeTreeMissingReason))
                segments.Add($"⚠ {scriptInfo.TypeTreeMissingReason}");
            if (scriptInfo.ScriptPathId != 0 || scriptInfo.ScriptFileId != 0)
            {
                var scriptText = $"m_Script: file {scriptInfo.ScriptFileId}, path {scriptInfo.ScriptPathId}";
                if (scriptInfo.ClassName is not null)
                {
                    var fullName = string.IsNullOrEmpty(scriptInfo.Namespace)
                        ? scriptInfo.ClassName
                        : $"{scriptInfo.Namespace}.{scriptInfo.ClassName}";
                    scriptText += $" → {fullName}";
                    if (!string.IsNullOrWhiteSpace(scriptInfo.AssemblyName)) scriptText += $"（{scriptInfo.AssemblyName}）";
                }
                else if (scriptInfo.ExternalPath is not null)
                {
                    scriptText += $" → 外部文件 {scriptInfo.ExternalPath}";
                    if (!string.IsNullOrWhiteSpace(scriptInfo.ExternalGuid)) scriptText += $"（GUID {scriptInfo.ExternalGuid}）";
                }
                segments.Add(scriptText);
            }
            scriptHeader.Text = string.Join("  ｜  ", segments);
            scriptHeader.Foreground = scriptInfo.TypeTreeMissingReason is null
                ? Brushes.DimGray
                : new SolidColorBrush(Color.FromRgb(184, 134, 11));
            DockPanel.SetDock(scriptHeader, Dock.Top);
            panel.Children.Add(scriptHeader);
        }

        var hint = new TextBlock
        {
            Text = "勾选要应用的字段；仅勾选且修改过的行会保存。数组、PPtr 和字节数组只显示信息。",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);

        foreach (var root in roots) AddRows(root, initial);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = x => _showAll || ((Row)x).Editable;

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            ItemsSource = _rows,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        _grid.BeginningEdit += (_, e) => { if (e.Row.Item is Row { Editable: false }) e.Cancel = true; };
        var rowStyle = new Style(typeof(DataGridRow));
        var errorTrigger = new DataTrigger { Binding = new Binding(nameof(Row.HasError)), Value = true };
        errorTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(255, 232, 232))));
        rowStyle.Triggers.Add(errorTrigger);
        _grid.RowStyle = rowStyle;

        var selectedFactory = new FrameworkElementFactory(typeof(CheckBox));
        selectedFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        selectedFactory.SetBinding(CheckBox.IsCheckedProperty,
            new Binding(nameof(Row.Selected))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
        selectedFactory.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(Row.Editable)));
        var selectedColumn = new DataGridTemplateColumn
        {
            Header = "应用",
            Width = new DataGridLength(48),
            CellTemplate = new DataTemplate { VisualTree = selectedFactory }
        };
        _grid.Columns.Add(selectedColumn);
        _grid.Columns.Add(new DataGridTextColumn { Header = "字段路径", Binding = new Binding(nameof(Row.Path)), IsReadOnly = true, Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding(nameof(Row.Type)), IsReadOnly = true, Width = 170 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "原值", Binding = new Binding(nameof(Row.Original)), IsReadOnly = true, Width = new DataGridLength(0.8, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "新值",
            Binding = new Binding(nameof(Row.Value))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            },
            Width = new DataGridLength(0.8, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(Row.State)), IsReadOnly = true, Width = 110 });
        panel.Children.Add(_grid);

        _errorText = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        DockPanel.SetDock(_errorText, Dock.Bottom);
        panel.Children.Add(_errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var showAll = new CheckBox { Content = "显示全部字段", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        showAll.Click += (_, _) => { _showAll = showAll.IsChecked == true; _view.Refresh(); };
        buttons.Children.Add(showAll);
        buttons.Children.Add(MakeButton("全选已修改", SelectModified_Click));
        buttons.Children.Add(MakeButton("恢复选中为原值", RevertSelected_Click));
        var cancel = MakeButton("取消", (_, _) => DialogResult = false, width: 90);
        buttons.Children.Add(cancel);
        var save = new Button { Content = "保存字段修改", Width = 130, Padding = new Thickness(8, 4, 8, 4), IsDefault = true };
        save.Click += Save_Click;
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static Button MakeButton(string content, RoutedEventHandler onClick, int width = 120)
        => new() { Content = content, Width = width, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };

    private void AddRows(UnityFieldNode node, UnityFieldEditSet? initial)
    {
        var displayValue = node.IsPPtr
            ? $"→ {node.PPtrTargetType ?? "对象"}（file {node.PPtrFileId}, path {node.PPtrPathId}）"
            : node.ValueType switch
            {
                "byteArray" => $"<{node.ByteArrayLength} 字节> {node.ByteArrayPreviewHex}".TrimEnd(),
                "array" => $"<{node.ArraySize} 项>",
                _ => node.Value ?? string.Empty
            };
        var typeName = node.IsEnum ? $"{node.Type} [枚举]" : node.Type;
        var value = displayValue;
        if (node.Editable && initial is not null)
        {
            var stored = FindStoredValue(initial, node.Path);
            if (stored is not null) value = stored;
        }
        _rows.Add(new Row
        {
            Path = node.Path,
            Type = typeName,
            Original = displayValue,
            Value = value,
            Editable = node.Editable,
            Node = node
        });
        foreach (var child in node.Children) AddRows(child, initial);
    }

    private static string? FindStoredValue(UnityFieldEditSet initial, string path)
    {
        var direct = initial.Edits.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal));
        if (direct is not null) return direct.NewValue;
        if (path.IndexOf('.') < 0) return null;
        var trimmed = path[(path.IndexOf('.') + 1)..];
        var legacy = initial.Edits.FirstOrDefault(x => string.Equals(x.Path, trimmed, StringComparison.Ordinal));
        return legacy?.NewValue;
    }

    private void SelectModified_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in _rows.Where(x => x.Editable && x.Modified)) row.Selected = true;
    }

    private void RevertSelected_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in _rows.Where(x => x.Selected && x.Editable))
        {
            row.Value = row.Original;
            row.State = string.Empty;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var errors = new List<string>();
        var drafts = new List<UnityFieldEditDraft>();
        foreach (var row in _rows.Where(x => x.Selected && x.Editable))
        {
            if (!row.Modified) continue;
            if (!UnityFieldValueParser.TryValidate(row.Node.ValueType, row.Value, out var error))
            {
                row.State = $"错误：{error}";
                errors.Add($"{row.Path}: {error}");
                continue;
            }
            row.State = "已修改";
            drafts.Add(new UnityFieldEditDraft(row.Path, row.Value));
        }
        if (errors.Count > 0)
        {
            _errorText.Text = string.Join(Environment.NewLine, errors.Take(8));
            _errorText.Visibility = Visibility.Visible;
            MessageBox.Show(this,
                $"有 {errors.Count} 个字段值不符合其序列化类型，已标红显示。", "字段校验失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _errorText.Visibility = Visibility.Collapsed;
        Result = drafts;
        DialogResult = true;
    }
}
