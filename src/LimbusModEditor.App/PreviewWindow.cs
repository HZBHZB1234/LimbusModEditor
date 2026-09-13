using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>
/// plan-13：「放大预览」独立窗口。
///
/// <para><b>为什么需要它</b>：右栏预览再能拖高，也受「同一窗口里还要放浏览列表与创作状态」
/// 的限制（用户反馈「预览占用空间太小、难查看难编辑」）。这里给预览一块自己的、
/// 可自由缩放的大画布 —— 窗口可最大化、可拖边框、可移动到第二块屏幕。</para>
///
/// <para><b>同一份预览，不复制构建逻辑</b>：页面把已经构建好的预览元素直接放进
/// <see cref="Surface"/>（切选资源时由页面的代际守卫流程刷新），因此文本 / JSON /
/// 图像 / 音频 / 十六进制各形态在大窗里的行为与小窗完全一致。</para>
/// </summary>
public sealed class PreviewWindow : Window
{
    /// <summary>预览内容的宿主：页面直接往这里塞预览元素。</summary>
    public ContentControl Surface { get; }

    /// <summary>预览信息行（大小 / 截断提示等）。</summary>
    public TextBlock InfoText { get; }

    public PreviewWindow(Window? owner, string assetLabel)
    {
        Title = $"预览 — {assetLabel}";
        Width = 1000;
        Height = 720;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;
        if (owner is not null && !ReferenceEquals(owner, this)) Owner = owner;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = assetLabel,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("WbCodeForegroundBrush", Brushes.Gainsboro),
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        Surface = new ContentControl { MinHeight = 200 };
        Grid.SetRow(Surface, 1);
        grid.Children.Add(Surface);

        InfoText = new TextBlock
        {
            Text = "无预览",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("WbTextMutedBrush", Brushes.Silver),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(InfoText, 2);
        grid.Children.Add(InfoText);

        Content = grid;
    }

    /// <summary>取主题画刷（窗口未接入可视化树时回退字面色，保证窗口仍可用）。</summary>
    private Brush Brush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;
}
