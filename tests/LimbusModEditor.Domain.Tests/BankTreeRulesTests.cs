using LimbusModEditor.Application.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-14.2 / 14.3：bank 树的纯规则。
///
/// 「默认不显示事件 bank」是<b>一档筛选与一个勾选框组合</b>出来的真值表，
/// 写在页面的 <c>RebuildTree</c> 里就只能靠人工点界面验证，所以抽成
/// <see cref="BankTreeRules"/> 在这里逐格钉死（含「仅事件 bank 必须强制显示」这条
/// 容易写错、写错了会显示一棵空树的边界）。
/// </summary>
public sealed class BankTreeRulesTests
{
    private static BankIndexEntry Entry(BankKind kind, int fsbCount = 1)
        => new($"C:/banks/{kind}.bank", $"{kind}.bank", 1024, 0, kind, null, fsbCount, null, []);

    // ── 14.2：bank 树默认隐藏事件 bank ───────────────────────────────

    [Fact]
    public void Default_filter_hides_event_banks()
    {
        Assert.False(BankTreeRules.ShouldShowBank(BankKind.Event, BankTreeKindFilter.All, showEventBanks: false));
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Audio, BankTreeKindFilter.All, showEventBanks: false));
        // 加密 / 无法识别：可解析失败但仍是「能看到的 bank」，默认照旧列出（用户才知道有个坏包）。
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Encrypted, BankTreeKindFilter.All, showEventBanks: false));
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Unknown, BankTreeKindFilter.All, showEventBanks: false));
    }

    [Fact]
    public void Checkbox_shows_event_banks_without_changing_other_kinds()
    {
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Event, BankTreeKindFilter.All, showEventBanks: true));
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Audio, BankTreeKindFilter.All, showEventBanks: true));
    }

    [Fact]
    public void Audio_only_and_unrecognized_filters_ignore_the_checkbox()
    {
        // 显式选「仅音频 bank」时，勾选框不该把事件 bank 塞回来。
        Assert.False(BankTreeRules.ShouldShowBank(BankKind.Event, BankTreeKindFilter.AudioOnly, showEventBanks: true));
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Audio, BankTreeKindFilter.AudioOnly, showEventBanks: false));

        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Encrypted, BankTreeKindFilter.Unrecognized, showEventBanks: false));
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Unknown, BankTreeKindFilter.Unrecognized, showEventBanks: false));
        Assert.False(BankTreeRules.ShouldShowBank(BankKind.Audio, BankTreeKindFilter.Unrecognized, showEventBanks: true));
        Assert.False(BankTreeRules.ShouldShowBank(BankKind.Event, BankTreeKindFilter.Unrecognized, showEventBanks: true));
    }

    [Fact]
    public void Event_only_filter_forces_event_banks_even_when_the_checkbox_is_off()
    {
        // 关键边界：选「仅事件 bank」却没勾「显示事件 bank」时，必须仍然显示事件 bank，
        // 否则筛选与勾选框自相矛盾，用户看到空树会以为索引坏了。
        Assert.True(BankTreeRules.ShouldShowBank(BankKind.Event, BankTreeKindFilter.EventOnly, showEventBanks: false));
        Assert.False(BankTreeRules.ShouldShowBank(BankKind.Audio, BankTreeKindFilter.EventOnly, showEventBanks: true));
    }

    [Fact]
    public void Hidden_event_bank_count_only_counts_the_default_filter()
    {
        var entries = new[]
        {
            Entry(BankKind.Audio, 2),
            Entry(BankKind.Event),
            Entry(BankKind.Event),
            Entry(BankKind.Encrypted),
        };

        // 默认档 + 未勾选：隐藏的就是全部事件 bank（数量要如实报告，不静默丢数据）。
        Assert.Equal(2, BankTreeRules.CountHiddenEventBanks(entries, BankTreeKindFilter.All, showEventBanks: false));
        // 勾上后没有「隐藏」，其它档位同理（都是用户主动选的类型）。
        Assert.Equal(0, BankTreeRules.CountHiddenEventBanks(entries, BankTreeKindFilter.All, showEventBanks: true));
        Assert.Equal(0, BankTreeRules.CountHiddenEventBanks(entries, BankTreeKindFilter.AudioOnly, showEventBanks: false));
        Assert.Equal(0, BankTreeRules.CountHiddenEventBanks(entries, BankTreeKindFilter.EventOnly, showEventBanks: false));

        Assert.Null(BankTreeRules.DescribeHiddenEventBanks(0));
        var note = BankTreeRules.DescribeHiddenEventBanks(2);
        Assert.NotNull(note);
        Assert.Contains("2", note);
        Assert.Contains("显示事件 bank", note);
    }

    // ── 14.3：单 FSB 的 bank 默认展开到样本 ──────────────────────────

    [Fact]
    public void Only_single_fsb_banks_auto_expand()
    {
        Assert.True(BankTreeRules.ShouldAutoExpandSingleFsb(1));
        Assert.False(BankTreeRules.ShouldAutoExpandSingleFsb(0)); // 事件 bank：没有 FSB 可展开
        Assert.False(BankTreeRules.ShouldAutoExpandSingleFsb(2));
        Assert.False(BankTreeRules.ShouldAutoExpandSingleFsb(64)); // 多 FSB 仍只展开到 FSB 层（惰性，不物化上千样本行）
    }
}
