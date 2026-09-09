using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>
/// 音频工作台页面（plan-02 占位）：plan-06 会实现完整能力——BankDirectoryService
/// 扫描样本表、NativeFmodAudioCodec 试听、WAV 替换实体化进项目、导出整包
/// .bank/.rebank（只写模组目录，绝不写游戏目录）。当前先给出占位说明。
/// </summary>
public sealed class BankWorkbenchPage : UserControl
{
    public BankWorkbenchPage(IWorkbenchHost host)
    {
        var panel = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(new TextBlock
        {
            Text = "🏦 音频工作台",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "开发中。",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xDA)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "这个页面将用于按 FMOD Bank 浏览音频样本：扫描样本表、试听、用 WAV 替换，\n" +
                   "并把整包导出为 .bank / .rebank（只写入模组目录，绝不直接写游戏目录）。\n\n" +
                   "在此之前，Bank 音频可以照旧在「📦 资源工作台」里找到：搜索 fsb/ 开头的音频资源，\n" +
                   "选中即可试听，或点「高级操作 → 导出当前音频 WAV」。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xBA))
        });
    }
}
