using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LimbusModEditor.App;

/// <summary>
/// 文本工作台页面（plan-02 过渡态）：暂以内嵌现有 <see cref="LangTextModControl"/>
/// 承载，plan-07 会用 LangTextWorkbenchService 重写为完整页面（枚举 / 搜索 /
/// 键值编辑 / 导出补丁）。构造传参沿用原标签页逻辑：游戏目录缺失时先无感
/// 自动获取一次，再拼 <c>LimbusCompany_Data/lang</c>。
/// </summary>
public sealed class TextWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    private readonly ContentControl _slot = new();
    private bool _initialized;

    public TextWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        Content = _slot;
        Loaded += async (_, _) => await EnsureInitializedAsync();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (_host.Project is not null && _host.ProjectFile is not null &&
                string.IsNullOrWhiteSpace(_host.Env.EffectiveGameDirectory(_host.Project)))
            {
                await Task.Run(() => _host.Env.ApplyAutoConfigure(_host.Project));
                await _host.SaveProjectAsync();
                _host.RefreshDirectorySettings();
            }
        }
        catch (Exception ex) { _host.SetStatus($"自动获取目录失败：{ex.Message}"); }

        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        var defaultLangRoot = string.IsNullOrWhiteSpace(gameDirectory)
            ? string.Empty
            : Path.Combine(gameDirectory, "LimbusCompany_Data", "lang");
        _slot.Content = new LangTextModControl(defaultLangRoot, _host.Env.EffectiveModDirectory(_host.Project), Window.GetWindow(this));
        _host.SetStatus("文本模组工作台已打开（补丁文件放进模组目录后由加载器应用）。");
    }
}
