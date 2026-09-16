namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 维基页面权威引擎：协调各来源提供者，产出带来源 + 置信度 + 可写出处的事实。
///
/// <para><b>设计原则</b>：
/// - 权威优先：只使用权威来源（<see cref="ConfidenceLevel.Authoritative"/>）作为主路径
/// - 来源可追溯：每条事实都带有来源类型与详情
/// - 可写出处：每条事实都带有「可写出处」字段，用于将来导出为模组
/// - 按需生成：只为请求的对象生成事实，不全量读入内存</para>
/// </summary>
public sealed class WikiPageAuthorityEngine
{
    private readonly Dictionary<string, IAuthorityProvider> _providers = new(StringComparer.Ordinal);

    public WikiPageAuthorityEngine()
    {
        // 注册全部 6 实体类别提供者
        Register(new PersonaAuthorityProvider());
        Register(new EnemyAuthorityProvider());
        Register(new AnnouncerAuthorityProvider());
        Register(new EgoAuthorityProvider());
        Register(new EgoGiftAuthorityProvider());
        Register(new AbnormalityAuthorityProvider());

        // 注册剧情页提供者
        Register(new StoryDataAuthorityProvider());
    }

    /// <summary>注册一个来源提供者。</summary>
    public void Register(IAuthorityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Category] = provider;
    }

    /// <summary>
    /// 为指定对象抽取事实。
    /// <param name="subjectId">对象 id（格式 <c>&lt;category&gt;:&lt;key&gt;</c>）。</param>
    /// <param name="context">抽取上下文。</param>
    /// <returns>事实集合。如果类别无提供者，返回空集合。</returns>
    /// </summary>
    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var category = SubjectIds.CategoryOf(subjectId);
        if (!_providers.TryGetValue(category, out var provider))
            return new AuthorityFacts(subjectId, Array.Empty<AuthorityFact>(), Array.Empty<UnavailableType>());

        return provider.Extract(subjectId, context);
    }

    /// <summary>
    /// 批量抽取事实。
    /// <param name="subjectIds">对象 id 集合。</param>
    /// <param name="context">抽取上下文。</param>
    /// <returns>每个对象的事实集合。</returns>
    /// </summary>
    public IReadOnlyDictionary<string, AuthorityFacts> ExtractBatch(
        IEnumerable<string> subjectIds, AuthorityExtractionContext context)
    {
        var results = new Dictionary<string, AuthorityFacts>(StringComparer.Ordinal);
        foreach (var id in subjectIds)
        {
            var facts = Extract(id, context);
            if (facts.Facts.Count > 0)
                results[id] = facts;
        }
        return results;
    }

    /// <summary>获取指定类别的来源提供者。</summary>
    public IAuthorityProvider? GetProvider(string category)
        => _providers.GetValueOrDefault(category);

    /// <summary>获取所有已注册的类别。</summary>
    public IReadOnlyList<string> RegisteredCategories => _providers.Keys.ToList();
}
