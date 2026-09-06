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

        panel.Children.Add(MakeTitle("工作流程总览（三步出模组）"));
        panel.Children.Add(MakeBody(
            "1. 启动即引导：新建（或打开）模组项目 —— 只需填模组名，\n" +
            "   游戏目录 / Unity 缓存 / 模组目录自动配置（共享设置保存在程序目录）；\n" +
            "2. 自动扫描游戏资源：引用模式，不复制文件，只建索引；扫描完成后\n" +
            "   在列表中搜索，选中即可替换图片 / 编辑字段；\n" +
            "3. 一键导出模组：把全部修改按真实加载器的 Carra2 布局打包，\n" +
            "   输出到模组目录（%APPDATA%\\LimbusCompanyMods）即可被游戏加载。\n\n" +
            "顶部的提示条会随时告诉你「下一步该做什么」；也可导入现有模组\n" +
            "（.carra/.carra2/.rebank/.bank/Lunartique ZIP/Unity .bundle/.assets）；\n" +
            "调试覆盖层 → 应用并启动调试（自动备份，可恢复）仍可用于本地验证。"));

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
            "• Texture2D 支持 PNG 预览与替换（RGB24/RGBA32/ARGB32/BGRA32/DXT1/DXT5）；\n" +
            "• Sprite 支持 rect/pivot/border 元数据编辑；图集可拆分与恢复；\n" +
            "• Bank 音频解码/编码使用随包提供的 fmod64.dll / fsbank64.dll\n" +
            "  （发布包 fmod/ 目录，启动时自动发现，无需配置）；未随包提供时\n" +
            "  可在「设置…」指定自己合法获得的 DLL 目录，或使用游戏自带运行库。"));

        panel.Children.Add(MakeTitle("对象摘要（Mesh / 动画 / 字体）"));
        panel.Children.Add(MakeBody(
            "• 选中 Mesh、AnimationClip 或 Font 类型资源后，点「查看对象摘要」：\n" +
            "  Mesh 显示子网格数、顶点数、顶点/索引数据大小；动画显示采样率、类型；\n" +
            "  字体显示字号、字符表条目数、内嵌数据大小与默认材质指向；\n" +
            "• 数值全部来自该文件自己的类型树，某版本缺字段时明确标注「未包含」，\n" +
            "  绝不用默认值补齐；可复制文本或导出 JSON 供存档。"));

        panel.Children.Add(MakeTitle("FMOD DLL 检测"));
        panel.Children.Add(MakeBody(
            "• 配置 FMOD DLL 目录后，点击「检测 FMOD DLL」查看每个 DLL 的：\n" +
            "  位数（x64/x86）、文件版本、导出符号数量，以及解码/FSB 编码接口是否齐全；\n" +
            "• 检测只读取文件头与导出表，不会加载或执行任何 DLL 代码；\n" +
            "• 结果按 DLL 大小/时间戳/内容指纹缓存，目录变化后自动重新检测；\n" +
            "• 32 位（x86）DLL 无法被本 64 位编辑器加载，报告中会给出红色警告。"));

        panel.Children.Add(MakeTitle("新建模组向导"));
        panel.Children.Add(MakeBody(
            "• 「新建模组向导…」一步创建项目结构与空白模板：\n" +
            "  Carra/Carra2 生成空对象包；模板先经对应格式处理器校验再写出" +
            "（事务写，失败不落盘）；\n" +
            "• Rebank 与 Lunartique 没有空白模板（真实加载器会把 wav 数为 0 的\n" +
            "  Rebank 判错并回滚安装；Lunartique 需要现有模组作基底），向导中置灰并说明原因；\n" +
            "• 空白模板不含资源 —— 创建后请继续「导入模组」登记源包并替换资源。"));

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
            "  真实缓存根为 LocalLow/Unity/ProjectMoon_LimbusCompany（可能是 junction），\n" +
            "  按缓存条目（外层键/内层键/__data）验证并显示数量，由你确认；\n" +
            "• 「自动建议模组目录」：真实加载器默认使用 %APPDATA%/LimbusCompanyMods；\n" +
            "• 「管理已安装模组」：按加载器自身的 _disable 后缀约定切换启用/禁用\n" +
            "  （只重命名，不改文件内容）；\n" +
            "• 「十六进制预览」：查看任意资源前 4 KiB 的 HEX 转储与 ASCII 边栏，\n" +
            "  并显示文件大小与原始/当前哈希，便于判断未知数据再决定是否替换。"));

        panel.Children.Add(MakeTitle("缓存对齐（导出诊断）"));
        panel.Children.Add(MakeBody(
            "• 真实加载器按 <缓存根>/<外层键>/<内层键>/__data 匹配 Carra 对象；\n" +
            "  游戏更新会更换缓存外层键，旧键模组会被静默跳过；\n" +
            "• 导出 Carra/Carra2 时若已配置 Unity 缓存目录，编辑器逐外层键核对\n" +
            "  缓存中是否仍有对应 bundle，失配以「缓存对齐：…」写入导出报告。"));

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
