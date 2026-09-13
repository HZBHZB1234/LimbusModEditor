namespace LimbusModEditor.App;

/// <summary>
/// 「可以按关键词跳过来搜索」的工作台页面（plan-11）。
///
/// <para>预设卡片流 / 资源预览的「关联资源」都需要把用户直接送到「那个资源所在的工作台并过滤好」，
/// 否则用户拿到一个人格 id 或样本名还得自己切页、自己粘贴。约定：实现者把关键词填进自己的搜索框
/// 并触发一次搜索（沿用各页既有的防抖 / 后台线程口径），不做别的副作用。</para>
/// </summary>
public interface ISearchableWorkbench
{
    /// <summary>把关键词填进搜索框并触发搜索（空关键词 = 清空过滤）。</summary>
    void ApplySearchKeyword(string keyword);
}
