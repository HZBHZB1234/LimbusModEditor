namespace LimbusModEditor.Application.Texts;

/// <summary>
/// plan-16 S3：<b>文本编辑集会话</b>——把 lang 的「改了哪些文本表」从页面对象搬到<b>宿主</b>。
///
/// <para><b>为什么必须搬</b>：plan-16 之后导出与调试统一由侧边栏两个按钮发起，它们要能看到
/// 「当前的全部修改」。编辑集原先长在 <c>TextWorkbenchPage</c> 的
/// <see cref="LangTextWorkbenchService"/> 实例里（页面没打开过就一定是空的），宿主看不见；
/// 会话对象由宿主持有、页面注入使用，两边看的是同一份内存状态。</para>
///
/// <para><b>口径</b>：键 = 相对<b>活动语言目录</b>的路径（如 <c>StoryData/S1.json</c>，
/// plan-16 §5），与索引库、界面、搜索命中完全一致；导出补丁时的加载器口径键由
/// <see cref="LangTextWorkbenchService.ToPatchKey"/> 在写出时补回语言目录那一层。</para>
///
/// <para><b>仍然只存在内存里</b>：关窗即丢（与原行为一致），lang 目录从不被本会话改动。
/// <see cref="Revision"/> 供导出报告与调试流程记录「本次导出对应的是哪一版编辑集」。</para>
/// </summary>
public sealed class LangEditSession
{
    private readonly LangTextWorkbenchService _service;
    private readonly object _gate = new();

    /// <param name="service">本会话唯一的 lang 服务实例（枚举 / 搜索 / 差分也走它，
    /// 避免出现两份互不可见的服务状态）。</param>
    public LangEditSession(LangTextWorkbenchService? service = null) => _service = service ?? new LangTextWorkbenchService();

    /// <summary>底层服务（页面用它做枚举 / 搜索 / 读取；导出与调试用它做差分与键换算）。</summary>
    public LangTextWorkbenchService Service => _service;

    /// <summary>编辑集发生任何变化时触发（页面据此刷新列表与树上的「已修改」标记）。</summary>
    public event EventHandler? Changed;

    /// <summary>编辑集版本号：每次新增/修改/还原/清空都 +1（导出报告与调试据此对账）。</summary>
    public int Revision { get; private set; }

    /// <summary>当前 lang 根（未定位为 null）。</summary>
    public string? LangRoot => _service.CurrentLangRoot;

    /// <summary>当前活动语言目录（未定位为 null）。</summary>
    public string? LanguageDirectory => _service.CurrentLanguageDirectory;

    /// <summary>条目口径 → 加载器口径的补丁键（见 <see cref="LangTextWorkbenchService.ToPatchKey"/>）。</summary>
    public string ToPatchKey(string relativePath) => _service.ToPatchKey(relativePath);

    /// <summary>定位 lang 根（同时解析活动语言目录；幂等）。</summary>
    public void AttachLangRoot(string langRoot) => _service.AttachLangRoot(langRoot);

    /// <summary>编辑集里的条目（相对活动语言目录，字典序）。</summary>
    public IReadOnlyList<string> EditedFiles => _service.EditedFiles;

    /// <summary>编辑集条目数。</summary>
    public int EditedFileCount => _service.EditedFiles.Count;

    /// <summary>该条目是否在编辑集中。</summary>
    public bool IsModified(string relativePath) => _service.IsModified(relativePath);

    /// <summary>vanilla 快照原文；不在编辑集中返回 null。</summary>
    public string? TryGetVanillaText(string relativePath) => _service.TryGetVanillaText(relativePath);

    /// <summary>当前修改文本；不在编辑集中返回 null。</summary>
    public string? TryGetModifiedText(string relativePath) => _service.TryGetModifiedText(relativePath);

    /// <summary>开始编辑（读原文进编辑集）；返回当前应展示的文本。</summary>
    public string BeginEdit(string relativePath) => _service.BeginEdit(relativePath);

    /// <summary>写入修改（非法 JSON 抛 <see cref="InvalidDataException"/>，编辑集保持原状）。</summary>
    public void SetModified(string relativePath, string newJsonText)
    {
        _service.SetModified(relativePath, newJsonText);
        Bump();
    }

    /// <summary>把某条目移出编辑集；返回它此前是否在编辑集中。</summary>
    public bool Revert(string relativePath)
    {
        var removed = _service.Revert(relativePath);
        if (removed) Bump();
        return removed;
    }

    /// <summary>清空编辑集（页面「清理入口」用）。</summary>
    public void ClearEdits()
    {
        if (_service.EditedFiles.Count == 0) return;
        _service.ClearEdits();
        Bump();
    }

    /// <summary>
    /// 编辑集快照（导出与调试的输入）：只包含<b>确有差异</b>的条目——
    /// vanilla 与 modified 逐字节相同的条目会被剔除，调用方不必再判一次。
    /// 顺序按条目路径字典序（稳定，便于报告对账）。
    /// </summary>
    public IReadOnlyList<LangEditEntry> Snapshot()
    {
        var entries = new List<LangEditEntry>();
        foreach (var relativePath in _service.EditedFiles)
        {
            var vanilla = _service.TryGetVanillaText(relativePath);
            var modified = _service.TryGetModifiedText(relativePath);
            if (vanilla is null || modified is null) continue;
            if (string.Equals(vanilla, modified, StringComparison.Ordinal)) continue;
            entries.Add(new LangEditEntry(relativePath, _service.ToPatchKey(relativePath), vanilla, modified));
        }
        return entries;
    }

    private void Bump()
    {
        lock (_gate) Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// 编辑集里的一条改动（导出/调试的输入单元）。
/// </summary>
/// <param name="RelativePath">条目口径（相对活动语言目录，如 <c>StoryData/S1.json</c>）。</param>
/// <param name="PatchKey">加载器口径（相对 lang 根，如 <c>LLc-CN-LCTA/StoryData/S1.json</c>）。</param>
/// <param name="VanillaText">官方原文（差分基线）。</param>
/// <param name="ModifiedText">修改后文本。</param>
public sealed record LangEditEntry(string RelativePath, string PatchKey, string VanillaText, string ModifiedText);
