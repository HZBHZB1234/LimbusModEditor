using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>代码构建窗口共用的主题画刷（与 Themes/Theme.xaml 色板同源）。
/// 每个窗口构造时显式设置 <see cref="WindowBackground"/>，保证任何时刻
/// 打开窗口都不会先以系统默认白色背景闪现（白屏），也不依赖样式解析时机。</summary>
internal static class AppTheme
{
    private static Brush Create(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>窗口底色 #11151A。</summary>
    public static readonly Brush WindowBackground = Create("#11151A");

    /// <summary>面板底色 #171D24。</summary>
    public static readonly Brush PanelBackground = Create("#171D24");

    /// <summary>边框线 #2A3540。</summary>
    public static readonly Brush BorderLine = Create("#2A3540");

    /// <summary>主文字 #E8EDF2。</summary>
    public static readonly Brush TextPrimary = Create("#E8EDF2");

    /// <summary>次级文字 #9FB0BF。</summary>
    public static readonly Brush TextSecondary = Create("#9FB0BF");

    /// <summary>弱化文字 #7F93A4。</summary>
    public static readonly Brush TextMuted = Create("#7F93A4");

    /// <summary>强调色 #4C8DDA。</summary>
    public static readonly Brush Accent = Create("#4C8DDA");
}
