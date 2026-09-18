using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// 按 <c>refKey</c>（容器路径）缓存 Spine 取数结果。
///
/// <para><b>为什么需要它</b>：解一套 Spine 要<b>整读并解包一个大 bundle</b>（实测单个 prefab
/// 平均 2.1 s，主因是 93 MB 那个 bundle），而同一套素材会被<b>反复问到</b>：
/// 维基一页的四条绑定配额、用户来回切页、同一条绑定既被「页面绑定」口径问又被「全库浏览」
/// 口径问。没有缓存时这些重复提问就是重复解包。</para>
///
/// <para><b>缓存的粒度是「结果」不是「字节」</b>：命中与未命中都缓存
/// （<see cref="TryGet"/> 区分两者）—— 未命中的那条同样付了几秒的解包钱，
/// 把它丢掉等于让「取不到」的场景反复付费。</para>
///
/// <para><b>缓存的键带上「素材是否还在」</b>：键是 <c>refKey</c>，但<b>失败结果额外记录
/// 当时 bundle 文件的存在性与最后写入时间</b>；bundle 之前不在、现在出现了（用户跑了一次游戏、
/// Unity 缓存被补回来）时，那条失败记录立即失效并重试 —— 否则「补上缓存后仍显示取不到」
/// 会变成一个要点重启才能自愈的幽灵。</para>
///
/// <para><b>内存封顶</b>：每条结果是骨架字节 + 图集文本 + 预解码 PNG（单套可达几 MB），
/// 因此用 LRU 上限封顶，<b>不</b>让「浏览全库 700 套」把进程撑爆。
/// 超限时按「最久未用」淘汰。</para>
/// </summary>
internal sealed class SpineResultCache
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>最多缓存几条结果。700 套全浏览时也不会把内存吃穿。</summary>
    private const int MaxEntries = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    /// <summary>缓存命中次数（诊断/报告用）。</summary>
    public long Hits { get; private set; }

    /// <summary>缓存未命中次数（诊断/报告用）。</summary>
    public long Misses { get; private set; }

    /// <summary>因 bundle 状态变化而作废的失败记录条数（诊断/报告用）。</summary>
    public long Invalidated { get; private set; }

    /// <summary>
    /// 取缓存。<b>不需要调用方先算 bundle 凭据</b>：失败记录的凭据（写入时 bundle 在不在）
    /// 已经记在里面，这里用当下的文件存在性比对即可 —— 所以成功命中路径上<b>零 IO</b>。
    /// 返回 true 表示有可用缓存（<paramref name="data"/> 与 <paramref name="error"/>
    /// 恰有一个非空，与首次取数时的返回一致）。
    /// </summary>
    public bool TryGet(string refKey, out SpineRawData? data, out string? error)
    {
        data = null;
        error = null;
        if (string.IsNullOrWhiteSpace(refKey)) return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(refKey, out var entry))
            {
                Misses++;
                return false;
            }

            // 失败记录要看「当时不在的 bundle 现在在了吗」——在了就作废重试。
            if (entry.Data is null && entry.ProbePath is not null
                && File.Exists(entry.ProbePath) != entry.ProbeExisted)
            {
                Remove(refKey, entry);
                Invalidated++;
                Misses++;
                return false;
            }

            Touch(refKey);
            Hits++;
            data = entry.Data;
            error = entry.Error;
            return true;
        }
    }

    /// <summary>写入一条结果（命中与未命中都写）。<paramref name="probePath"/> 同时充当
    /// 「这条失败记录当时依赖的 bundle 在不在」的凭据 —— 写入时就把存在性<b>固化</b>下来，
    /// 之后查询不必再问一次调用方。</summary>
    public void Set(string refKey, string? probePath, SpineRawData? data, string? error)
    {
        if (string.IsNullOrWhiteSpace(refKey)) return;
        lock (_gate)
        {
            if (_entries.TryGetValue(refKey, out var existing)) Remove(refKey, existing);
            _entries[refKey] = new Entry(data, error, probePath, ProbeExists(probePath));
            _order.AddLast(refKey);
            while (_entries.Count > MaxEntries) EvictOldest();
        }
    }

    private static bool ProbeExists(string? probePath)
    {
        if (string.IsNullOrWhiteSpace(probePath)) return false;
        try { return File.Exists(probePath); }
        catch (Exception) { return false; }
    }

    /// <summary>清空缓存（bundle 被重新扫描后由调用方按需调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    /// <summary>一句话诊断（日志/报告用）：条数 + 命中/未命中/作废。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return $"缓存 {_entries.Count}/{MaxEntries} 条 · 命中 {Hits} · 未命中 {Misses} · bundle 变化作废 {Invalidated}";
        }
    }

    private void Touch(string refKey)
    {
        _order.Remove(refKey);
        _order.AddLast(refKey);
    }

    private void Remove(string refKey, Entry entry)
    {
        _entries.Remove(refKey);
        _order.Remove(refKey);
        _ = entry;
    }

    private void EvictOldest()
    {
        var oldest = _order.First;
        if (oldest is null) return;
        var key = oldest.Value;
        _order.RemoveFirst();
        _entries.Remove(key);
        Log.Debug("Spine 结果缓存超上限，淘汰最久未用的一条：{0}", key);
    }

    private sealed record Entry(SpineRawData? Data, string? Error, string? ProbePath, bool ProbeExisted);
}
