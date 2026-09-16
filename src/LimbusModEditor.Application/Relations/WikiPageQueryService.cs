namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基化页面树的<b>查询门面</b>：把 <see cref="WikiPageStore"/> 的原始行整理成 UI 能直接绑定的结果。
///
/// <para><b>性能保证</b>：全部查询按 page_id / sub_page_id / entry_id 精确走索引，
/// <b>不</b>一次性读全部内容进内存再筛。每个方法只取当前层的数据，
/// 由 UI 逐层展开时再取下一层（惰性加载）。</para>
///
/// <para>库不存在或还没建时返回空结果 + 中文原因（<b>绝不抛异常</b>）：
/// 维基页是用户主动创建的内容，缺了只是「没有页面」，不该把应用搞崩。</para>
/// </summary>
public sealed class WikiPageQueryService
{
    private readonly WikiPageStore _store;

    /// <param name="store">维基页库。</param>
    public WikiPageQueryService(WikiPageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>是否有任何页面。</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                return _store.Exists && _store.ReadPageCount() > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>全部主对象页（按 sort_key 排序）。</summary>
    public IReadOnlyList<WikiPage> Pages() => _store.ReadPages();

    /// <summary>单个主对象页（按 page_id）。不存在返回 null。</summary>
    public WikiPage? GetPage(string pageId) => _store.ReadPage(pageId);

    /// <summary>某个主对象页下的全部二级页面（按 sort_order 排序）。</summary>
    public IReadOnlyList<WikiSubPage> SubPages(string pageId) => _store.ReadSubPages(pageId);

    /// <summary>某个二级页面下的全部条目（按 sort_order 排序）。</summary>
    public IReadOnlyList<WikiEntry> Entries(string subPageId) => _store.ReadEntries(subPageId);

    /// <summary>某个条目的全部资源绑定（按 sort_order 排序）。</summary>
    public IReadOnlyList<WikiResourceBinding> Bindings(string entryId) => _store.ReadBindings(entryId);

    /// <summary>
    /// 一次性加载某个主对象页的完整子树（二级页面 → 条目 → 资源绑定）。
    /// <b>注意</b>：这个方法会读该页下的全部数据，但<b>只读该页</b>，不会把其他页读进内存。
    /// 真实规模（1,313 对象 / 每对象约 5 个二级页 / 每二级页约 10 条目 / 每条目约 3 绑定）下，
    /// 单页数据量级 ≈ 5 × 10 × 3 = 150 行，可忽略。
    /// </summary>
    public WikiPageDetail? GetPageDetail(string pageId)
    {
        var page = _store.ReadPage(pageId);
        if (page is null) return null;

        var subPages = _store.ReadSubPages(pageId);
        var subDetails = new List<WikiSubPageDetail>(subPages.Count);
        foreach (var sub in subPages)
        {
            var entries = _store.ReadEntries(sub.SubPageId);
            var entryDetails = new List<WikiEntryDetail>(entries.Count);
            foreach (var entry in entries)
            {
                var bindings = _store.ReadBindings(entry.EntryId);
                entryDetails.Add(new WikiEntryDetail(entry, bindings));
            }
            subDetails.Add(new WikiSubPageDetail(sub, entryDetails));
        }
        return new WikiPageDetail(page, subDetails);
    }

    /// <summary>
    /// 搜索结果：标题或正文里包含 <paramref name="keyword"/> 的条目（分页）。
    /// <b>不</b>跨页读全部条目再筛——走 SQL LIKE + LIMIT/OFFSET。
    /// 返回 (总命中数, 当前页条目)。
    /// </summary>
    public (int Total, IReadOnlyList<WikiEntry> Entries) SearchEntries(string keyword, int offset, int limit)
        => _store.SearchEntries(keyword, offset, limit);

    /// <summary>某类别下的主对象页数。</summary>
    public int CountByCategory(string category)
        => _store.CountByCategory(category);

    /// <summary>来源统计：人工编纂数 / 候选导入数。</summary>
    public (int HumanEdited, int Candidate) SourceStatistics()
        => _store.SourceStatistics();
}
