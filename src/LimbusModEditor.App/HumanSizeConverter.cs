using System.Globalization;
using System.Windows.Data;

namespace LimbusModEditor.App;

/// <summary>字节数人性化显示：1234567 → 1.18 MB。列表直接绑定原始 long
/// 会显示一长串不友好的数字；未知/负值显示 —。</summary>
public sealed class HumanSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes < 0) return "—";
        return bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
            _ => $"{bytes} B"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("HumanSizeConverter 只用于单向显示。");
}
