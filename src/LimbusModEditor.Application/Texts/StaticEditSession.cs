using LimbusModEditor.Application.StaticMods;

namespace LimbusModEditor.Application.Texts;

/// <summary>
/// plan-16 S3：<b>静态数据表编辑集会话</b>——把「改了哪些静态表」从
/// <c>StaticWorkbenchPage</c> 的页面字段搬到<b>宿主</b>，与 <see cref="LangEditSession"/> 同构。
///
/// <para><b>为什么必须搬</b>：导出计划（<c>ModExportPlanService</c>）与调试要能看到全部修改，
/// 而编辑集原先长在页面里（页面没打开过就一定是空的）。</para>
///
/// <para><b>键</b>：用 <see cref="StaticTableEntry.Key"/>（容器路径优先，空容器退回 name|pathId），
/// 与页面列表/树的选中口径一致；导出时由 <c>StaticModService.CreateJsonPatchPackage</c>
/// 按 <c>dataClass/file/container</c> 还原成加载器认的 manifest 条目。</para>
///
/// <para><b>只存在内存里</b>：关窗即丢；静态 bundle / catalog 从不被本会话改动。</para>
/// </summary>
public sealed class StaticEditSession
{
    private readonly Dictionary<string, StaticEditEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>编辑集发生任何变化时触发（页面据此刷新「已修改」标记与按钮可用性）。</summary>
    public event EventHandler? Changed;

    /// <summary>编辑集版本号：每次登记/还原都 +1。</summary>
    public int Revision { get; private set; }

    /// <summary>编辑集条目数。</summary>
    public int EntryCount => _entries.Count;

    /// <summary>登记一条修改（覆盖同键的旧修改）。<paramref name="officialText"/> 是官方基线，
    /// 用于生成 RFC6902 差分；缺基线时导出会明确报错而不是猜。</summary>
    public void Set(string key, StaticTableEntry entry, string officialText, string modifiedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(officialText);
        ArgumentNullException.ThrowIfNull(modifiedText);
        _entries[key] = new StaticEditEntry(key, entry, officialText, modifiedText);
        Bump();
    }

    /// <summary>该键是否在编辑集中。</summary>
    public bool IsModified(string key) => _entries.ContainsKey(key);

    /// <summary>该键的官方基线正文（不在编辑集中返回 null）。</summary>
    public string? TryGetOfficialText(string key)
        => _entries.TryGetValue(key, out var entry) ? entry.OfficialText : null;

    /// <summary>该键的修改后正文（不在编辑集中返回 null）。</summary>
    public string? TryGetModifiedText(string key)
        => _entries.TryGetValue(key, out var entry) ? entry.ModifiedText : null;

    /// <summary>编辑集里的键（字典序）。</summary>
    public IReadOnlyList<string> Keys => _entries.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>移除一条（还原）；返回它此前是否存在。</summary>
    public bool Remove(string key)
    {
        var removed = _entries.Remove(key);
        if (removed) Bump();
        return removed;
    }

    /// <summary>清空编辑集。</summary>
    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Bump();
    }

    /// <summary>
    /// 编辑集快照（导出输入）：只含<b>确有差异</b>的条目（vanilla == modified 的剔除），
    /// 按键字典序，调用方不必再判一次。
    /// </summary>
    public IReadOnlyList<StaticEditEntry> Snapshot()
        => _entries.Values
            .Where(x => !string.Equals(x.OfficialText, x.ModifiedText, StringComparison.Ordinal))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();

    private void Bump()
    {
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>静态表编辑集里的一条改动。</summary>
/// <param name="Key">编辑集键（<see cref="StaticTableEntry.Key"/>）。</param>
/// <param name="Entry">表的元数据（dataClass / fileName / container）。</param>
/// <param name="OfficialText">官方基线正文。</param>
/// <param name="ModifiedText">修改后正文。</param>
public sealed record StaticEditEntry(string Key, StaticTableEntry Entry, string OfficialText, string ModifiedText);
