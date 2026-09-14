namespace LimbusModEditor.App;

/// <summary>
/// 「可以按<b>精确载荷</b>定位到某一行」的工作台页面（plan-11 深化）。
///
/// <para><b>为什么不是 <see cref="ISearchableWorkbench"/></b>：关键词过滤只能把用户送到
/// 「这一屏里有这个名字」，40 万行资源 / 5 万条样本里他还得自己再翻一遍。精确载荷
/// （见 <c>LimbusModEditor.Application.Relations.RelationDeepLink</c>）带的是子定位信息
/// ——「哪个文件的哪个键」「哪张表的哪条记录」「哪个 bank 的哪个样本」——目标页据此
/// <b>选中并滚到那一行</b>，用户看到的就是他点的那条关联。</para>
///
/// <para><b>允许失败</b>：载荷可能因为「该资源已随游戏热修改名 / 不在当前语言目录 /
/// 被当前筛选隐藏」而定位不到。此时返回 false，宿主会退化为
/// <see cref="ISearchableWorkbench.ApplySearchKeyword"/>（至少把关键词填好），
/// 而不是静默什么都不做。实现者<b>不得抛异常</b>（关联图是旁路，缺了不该崩页面）。</para>
/// </summary>
public interface IReferenceRevealable
{
    /// <summary>
    /// 按精确载荷定位并选中目标行。
    /// </summary>
    /// <param name="payload">
    /// 精确载荷（<c>'\0'</c> 分隔的若干段，口径由 <c>RelationDeepLink</c> 定义）。
    /// 也接受「只有一个容器路径」的退化载荷（资源级定位）。
    /// </param>
    /// <returns>定位成功返回 true；定位不到返回 false（宿主将退化为关键词过滤）。</returns>
    bool Reveal(string payload);
}
