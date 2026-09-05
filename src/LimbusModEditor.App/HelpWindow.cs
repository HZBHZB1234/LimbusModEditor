using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LimbusModEditor.App;

/// <summary>In-app tutorial: the same guidance as docs/USAGE.md condensed into
/// scrollable sections so new users can follow the workflow without leaving the
/// editor.</summary>
public sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        Title = "使用教程";
        Width = 720;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
        var panel = new StackPanel();

        panel.Children.Add(MakeTitle("工作流程总览"));
        panel.Children.Add(MakeBody(
            "1. 新建项目（.lmeproj）并配置游戏目录、Unity 缓存目录、模组目录；\n" +
            "2. 导入资源：.carra/.carra2/.rebank/.bank/Lunartique ZIP/Unity .bundle/.assets 或资源目录；\n" +
            "3. 在索引中检索资源，检查元数据，进行字段/图片/文本编辑；\n" +
            "4. 导出模组（来源格式或 Carra/Carra2/Rebank/Lunartique）；\n" +
            "5. 构建调试覆盖层 → 应用并启动调试（自动备份，可恢复）。"));

        panel.Children.Add(MakeTitle("Unity 字段编辑"));
        panel.Children.Add(MakeBody(
            "选中 Unity 资源后点击「Unity 字段编辑」：\n" +
            "• 字段树显示真实序列化类型，勾选「显示全部字段」可查看数组、PPtr、字节数组；\n" +
            "• 编辑值并勾选「应用」列，保存时按类型、路径、指针语义三层校验；\n" +
            "• 校验失败的行会标红并说明原因，窗口不会丢失你已输入的内容；\n" +
            "• 保存的是版本化编辑记录（原值/哈希/作者/修订号），构建时才真正写入。"));

        panel.Children.Add(MakeTitle("指针（PPtr）与依赖检查"));
        panel.Children.Add(MakeBody(
            "指针由 m_FileID + m_PathID 组成：\n" +
            "• file 0 = 本文件内对象（按 Path ID 查找）；file >0 = 外部引用表中的其他文件；\n" +
            "• 字段编辑窗口底部蓝色信息栏显示每个指针的解析结果（同文件/外部文件/空引用/悬空）；\n" +
            "• 选中 m_PathID 或 m_FileID 行后，右下角下拉框列出同文件中类型匹配的对象，\n" +
            "  点击「自动填充所选 PPtr」即可自动写入，无需手抄 Path ID；\n" +
            "• 资源面板的「查找谁引用了此对象」会扫描同文件与 bundle 内跨文件的全部指针；\n" +
            "• 构建前后会做引用完整性对比：任何“原本可解析 → 悬空”的回退都会中止构建。"));

        panel.Children.Add(MakeTitle("图片、图集与音频"));
        panel.Children.Add(MakeBody(
            "• Texture2D 支持 PNG 预览与替换（RGB24/RGBA32/BGRA32/DXT1/DXT5）；\n" +
            "• Sprite 支持 rect/pivot/border 元数据编辑；图集可拆分与恢复；\n" +
            "• Bank 音频需要你提供合法的 fmod64.dll / fsbank64.dll 才能解码导出 WAV；\n" +
            "  本工具不附带、不伪造任何 FMOD 二进制文件。"));

        panel.Children.Add(MakeTitle("FMOD DLL 检测"));
        panel.Children.Add(MakeBody(
            "• 配置 FMOD DLL 目录后，点击「检测 FMOD DLL」查看每个 DLL 的：\n" +
            "  位数（x64/x86）、文件版本、导出符号数量，以及解码/FSB 编码接口是否齐全；\n" +
            "• 检测只读取文件头与导出表，不会加载或执行任何 DLL 代码；\n" +
            "• 结果按 DLL 大小/时间戳缓存，目录变化后自动重新检测；\n" +
            "• 32 位（x86）DLL 无法被本 64 位编辑器加载，报告中会给出红色警告。"));

        panel.Children.Add(MakeTitle("导出向导与导出报告"));
        panel.Children.Add(MakeBody(
            "• 「导出模组」会先识别源格式，然后显示源→目标兼容性矩阵：\n" +
            "  可用目标带 ✓ 说明；不可用目标置灰并解释原因；\n" +
            "• 窗口同时显示项目概况（资源数/替换数/字段编辑数/Sprite 元数据数）；\n" +
            "• 选择目标格式后输出路径自动切换扩展名；\n" +
            "• 导出完成后弹出逐资源报告：已应用 / 已跳过 / 保留未知 + 诊断，\n" +
            "  「已跳过」说明该替换没有写入包（常见原因是替换路径与包内对象不匹配）。"));

        panel.Children.Add(MakeTitle("资源筛选"));
        panel.Children.Add(MakeBody(
            "• 资源列表上方筛选行支持：文本搜索、类型、编辑状态、Path ID、Type ID、\n" +
            "  大小区间（KB）与「仅已替换」开关；\n" +
            "• 「清除筛选」一键还原；筛选生效时右上角计数显示 “筛选后 / 总数”。"));

        panel.Children.Add(MakeTitle("自动定位与预览"));
        panel.Children.Add(MakeBody(
            "• 「自动定位游戏目录」：扫描已知 Steam 库，找到含 LimbusCompany.exe 的安装目录；\n" +
            "• 「自动建议 Unity 缓存目录」：从游戏目录与 LocalLow 推导候选，\n" +
            "  只列出验证过含 .bundle 的目录并显示 bundle 数量，由你确认；\n" +
            "• 「十六进制预览」：查看任意资源前 4 KiB 的 HEX 转储与 ASCII 边栏，\n" +
            "  并显示文件大小与原始/当前哈希，便于判断未知数据再决定是否替换。"));

        panel.Children.Add(MakeTitle("安全边界"));
        panel.Children.Add(MakeBody(
            "• 原始资源永远保留在 sources/ 中，所有修改先记录、构建时才应用；\n" +
            "• 应用调试覆盖层前会自动备份游戏文件，编辑器关闭时可恢复；\n" +
            "• 不支持的格式会明确报告，不会静默丢弃或盲目复制；\n" +
            "• 未经真实游戏样本验证的兼容性不会被宣称。"));

        panel.Children.Add(MakeTitle("遇到问题？"));
        panel.Children.Add(MakeBody(
            "• 详见仓库中的 docs/USAGE.md（含故障排查表）；\n" +
            "• CLI 探测：LimbusModEditor.Cli.exe probe <package>；\n" +
            "• 字段校验/引用检查失败时，请优先按提示回退最近的修改。"));

        scroll.Content = panel;
        Content = scroll;
    }

    private static TextBlock MakeTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 6),
        Foreground = new SolidColorBrush(Color.FromRgb(31, 78, 121))
    };

    private static TextBlock MakeBody(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 20,
        Foreground = Brushes.DimGray
    };
}
