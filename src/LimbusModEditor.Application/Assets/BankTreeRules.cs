namespace LimbusModEditor.Application.Assets;

/// <summary>
/// bank 树（bank → FSB → 样本）的「哪些节点该出现」筛选口径。
///
/// <para><b>为什么要抽成纯规则类</b>：音频工作台的筛选行是「类型下拉 + 若干勾选框」两个维度
/// 组合出的一小张真值表，写进页面的 <c>RebuildTree</c> 里就只能靠人工点界面验证。
/// 抽到这里后它无 WPF 依赖，可以被单测逐格钉死（<c>BankTreeRulesTests</c>）。</para>
///
/// <para><b>只影响树</b>：「全部音频」总表本来就只列样本行，事件 bank 一个样本都没有，
/// 因此这张真值表不必、也不该参与总表的过滤。</para>
/// </summary>
public enum BankTreeKindFilter
{
    /// <summary>全部类型（筛选行首项；事件 bank 默认被 <see cref="BankTreeRules.ShouldShowBank"/> 隐藏）。</summary>
    All,
    /// <summary>仅音频 bank。</summary>
    AudioOnly,
    /// <summary>仅事件 bank。</summary>
    EventOnly,
    /// <summary>加密 bank + 无法识别 bank（二者都不可解析，UI 上归为一档）。</summary>
    Unrecognized,
}

/// <summary>
/// plan-14.2 / 14.3：bank 树的纯规则（无 WPF 依赖，可单测）。
/// </summary>
public static class BankTreeRules
{
    /// <summary>
    /// 一个 bank 节点是否出现在树里。
    ///
    /// <para><b>默认隐藏事件 bank</b>：本机 1531 个 bank 里事件 bank 占多数，它们 SNDH 表为空、
    /// 没有任何可试听/替换的样本，却把音频 bank 淹掉（用户原话「bank 树板块默认不显示事件 bank」）。</para>
    ///
    /// <para><b>「仅事件 bank」必须强制显示</b>：这一档与「显示事件 bank」勾选框是<b>或</b>关系。
    /// 若仍按勾选框判定，用户选「仅事件 bank」又没勾勾选框时会看到一棵空树——
    /// 那是筛选本身自相矛盾，不是数据为空。</para>
    /// </summary>
    /// <param name="kind">bank 类型判定（<see cref="BankDirectoryService"/> 的口径）。</param>
    /// <param name="filter">类型筛选档位。</param>
    /// <param name="showEventBanks">勾选框「显示事件 bank」是否勾上。</param>
    public static bool ShouldShowBank(BankKind kind, BankTreeKindFilter filter, bool showEventBanks) => filter switch
    {
        BankTreeKindFilter.AudioOnly => kind == BankKind.Audio,
        BankTreeKindFilter.EventOnly => kind == BankKind.Event,
        BankTreeKindFilter.Unrecognized => kind is BankKind.Encrypted or BankKind.Unknown,
        // 全部类型：事件 bank 只在勾选「显示事件 bank」时出现
        _ => kind != BankKind.Event || showEventBanks,
    };

    /// <summary>
    /// 该 FSB 是否应当自动展开（plan-14.3：展开 bank 后若只有 1 个 FSB，直接展开到样本层）。
    ///
    /// <para><b>为什么只在 1 个时自动展开</b>：样本行是<b>惰性物化</b>的（见
    /// <c>BankWorkbenchPage.TreeItem_Expanded</c>），目的是不让一个 bank 的上千样本行
    /// 在展开 bank 的瞬间全部建出来。单 FSB 的 bank（如 <c>1D101A.assets.bank</c>，7 个样本）
    /// 不存在这个量级，多一层必须手点的 FSB 节点纯属多余；多 FSB 的 bank 行为保持不变。</para>
    /// </summary>
    public static bool ShouldAutoExpandSingleFsb(int fsbCount) => fsbCount == 1;

    /// <summary>
    /// 被「默认隐藏事件 bank」挡掉的 bank 数（供状态条/空态文案用）。
    /// 只在<b>默认档</b>（<see cref="BankTreeKindFilter.All"/> + 未勾选）有意义：
    /// 其余档位要么本来就只选事件 bank，要么是用户主动选的类型。
    /// </summary>
    /// <param name="entries">当前索引里的全部 bank（过滤前）。</param>
    /// <param name="filter">类型筛选档位。</param>
    /// <param name="showEventBanks">勾选框「显示事件 bank」是否勾上。</param>
    public static int CountHiddenEventBanks(
        IEnumerable<BankIndexEntry> entries, BankTreeKindFilter filter, bool showEventBanks)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (filter != BankTreeKindFilter.All || showEventBanks) return 0;
        return entries.Count(x => x.Kind == BankKind.Event);
    }

    /// <summary>
    /// 「有 N 个事件 bank 默认隐藏」的中文说明；没有隐藏时为 null（调用方据此不写这条文案）。
    /// <b>不静默丢数据</b>：树里少了哪些、怎么让它们出现，必须由状态条/空态明确告知。
    /// </summary>
    public static string? DescribeHiddenEventBanks(int hiddenCount)
        => hiddenCount <= 0
            ? null
            : $"有 {hiddenCount} 个事件 bank 默认隐藏（事件 bank 的 SNDH 表为空，没有可试听/替换的样本）；" +
              "勾选筛选行的「显示事件 bank」即可看到。";
}
