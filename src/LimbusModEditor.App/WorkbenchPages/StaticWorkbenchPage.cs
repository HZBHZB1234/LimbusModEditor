using System.Windows;
using System.Windows.Controls;

namespace LimbusModEditor.App;

/// <summary>
/// 静态数据工作台页面（plan-02 过渡态）：暂以内嵌现有 <see cref="StaticModControl"/>
/// 承载，plan-08 会重写为完整页面（StaticBundleLocator 枚举 / TextAsset·JSON 编辑 /
/// diff / 导出 .staticmod）。
/// </summary>
public sealed class StaticWorkbenchPage : UserControl
{
    private readonly ContentControl _slot = new();
    private bool _initialized;

    public StaticWorkbenchPage(IWorkbenchHost host)
    {
        Content = _slot;
        Loaded += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            _slot.Content = new StaticModControl(Window.GetWindow(this));
            host.SetStatus("静态数据模组工作台已打开（.staticmod 放进模组目录后由加载器应用）。");
        };
    }
}
