using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>
/// 工作台页面的宿主契约（plan-02）：页面通过构造函数注入本接口访问项目、
/// 共享环境与全局能力，避免反向依赖 MainWindow 具体类型。MainWindow 实现之。
/// </summary>
public interface IWorkbenchHost
{
    /// <summary>当前项目（未打开时为 null）。页面在无项目时应保持可用并给引导文案。</summary>
    ModProject? Project { get; }

    /// <summary>当前项目文件路径（未保存过的临时项目为 null）。</summary>
    string? ProjectFile { get; }

    /// <summary>全局共享环境（目录配置、最近项目等）。</summary>
    AppEnvironment Env { get; }

    /// <summary>在主窗口状态栏写一条状态。</summary>
    void SetStatus(string message);

    /// <summary>项目状态刷新（页面据此重载资源列表/计数；status 非空时同时写入状态栏）。</summary>
    void RefreshProjectState(string? status = null);

    /// <summary>共享目录等设置变更后的刷新（重算目录定位缓存并更新侧边栏状态）。</summary>
    void RefreshDirectorySettings();

    /// <summary>保存当前项目（封装 SaveProjectInternalAsync）。无项目时返回 false。</summary>
    Task<bool> SaveProjectAsync();

    /// <summary>页面间跳转（如「去扫描」→ assets）。</summary>
    void ShowPage(string key);
}
