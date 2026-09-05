using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Formats.Unity;
using LimbusModEditor.Application.Assets;

namespace LimbusModEditor.App;

internal sealed class SpriteMetadataWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    public UnitySpriteMetadata? Result { get; private set; }

    public SpriteMetadataWindow(UnitySpriteMetadata initial)
    {
        Title = "Sprite metadata editor"; Width = 430; Height = 430; MinWidth = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(23, 29, 36));
        Foreground = new SolidColorBrush(Color.FromRgb(232, 237, 242));
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Save metadata", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0) };
        save.Click += Save_Click;
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddGroup(grid, "Rect X", "rectX", initial.Rect.X); AddGroup(grid, "Rect Y", "rectY", initial.Rect.Y); AddGroup(grid, "Rect Width", "rectWidth", initial.Rect.Width); AddGroup(grid, "Rect Height", "rectHeight", initial.Rect.Height);
        AddGroup(grid, "Pivot X", "pivotX", initial.Pivot.X); AddGroup(grid, "Pivot Y", "pivotY", initial.Pivot.Y);
        AddGroup(grid, "Border Left", "borderLeft", initial.Border.Left); AddGroup(grid, "Border Bottom", "borderBottom", initial.Border.Bottom); AddGroup(grid, "Border Right", "borderRight", initial.Border.Right); AddGroup(grid, "Border Top", "borderTop", initial.Border.Top);
        AddGroup(grid, "Pixels / Unit", "ppu", initial.PixelsToUnits);
        scroll.Content = grid; root.Children.Add(scroll); Content = root;
    }

    private void AddGroup(Grid grid, string label, string key, float value)
    {
        var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(184, 199, 212)) };
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(4) };
        Grid.SetRow(text, row); Grid.SetColumn(text, 0); Grid.SetRow(box, row); Grid.SetColumn(box, 1); grid.Children.Add(text); grid.Children.Add(box); _fields[key] = box;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var keys = new[] { "rectX", "rectY", "rectWidth", "rectHeight", "pivotX", "pivotY", "borderLeft", "borderBottom", "borderRight", "borderTop", "ppu" };
        var values = new float[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            if (!TryRead(keys[i], out values[i])) { MessageBox.Show(this, "All fields must be valid numbers.", "Input error", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (values[2] < 0 || values[3] < 0 || values[10] <= 0) { MessageBox.Show(this, "Rect width/height cannot be negative and Pixels/Unit must be positive.", "Input error", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Result = new UnitySpriteMetadata(new UnitySpriteRect(values[0], values[1], values[2], values[3]), new UnitySpriteVector2(values[4], values[5]), new UnitySpriteBorder(values[6], values[7], values[8], values[9]), values[10]);
        DialogResult = true;
    }

    private bool TryRead(string key, out float value) => float.TryParse(_fields[key].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.TryParse(_fields[key].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
