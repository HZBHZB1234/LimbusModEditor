using LimbusModEditor.Application.SpineData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <see cref="SpineResultCache"/> 的规则回归：解一套实测平均 2.1 s（主因是整读大 bundle），
/// 缓存是「浏览全库」这条需求能成立的前提。这里锁住三条关键性质：
/// ① 命中与未命中<b>都</b>缓存；② 失败记录会在 bundle 出现后自愈；
/// ③ 内存有上限（不能让浏览 700 套把进程撑爆）。
/// </summary>
public sealed class SpineResultCacheTests
{
    private static SpineRawData Sample(string label = "sample") => new(
        SkeletonBytes: [1, 2, 3],
        SkeletonFormat: "json",
        AtlasText: "page.png\nsize:64,64\n",
        PageBytes: new Dictionary<string, byte[]> { ["page.png"] = [9, 9] },
        AvailablePages: ["page.png"],
        Label: label);

    [Fact]
    public void Miss_on_an_empty_cache_is_reported_as_a_miss()
    {
        var cache = new SpineResultCache();
        Assert.False(cache.TryGet("a.prefab", out var data, out var error));
        Assert.Null(data);
        Assert.Null(error);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void Successful_result_is_cached_and_returned_unchanged()
    {
        var cache = new SpineResultCache();
        var sample = Sample();
        cache.Set("a.prefab", null, sample, null);

        Assert.True(cache.TryGet("a.prefab", out var data, out var error));
        Assert.Same(sample, data);
        Assert.Null(error);
        Assert.Equal(1, cache.Hits);
    }

    /// <summary>
    /// <b>未命中也要缓存</b>：取不到的那条同样付了几秒解包钱，
    /// 丢掉等于让「取不到」的场景反复付费。
    /// </summary>
    [Fact]
    public void Failure_result_is_also_cached_with_its_chinese_reason()
    {
        var cache = new SpineResultCache();
        cache.Set("a.prefab", null, null, "同目录里找不到骨架 JSON。");

        Assert.True(cache.TryGet("a.prefab", out var data, out var error));
        Assert.Null(data);
        Assert.Equal("同目录里找不到骨架 JSON。", error);
    }

    /// <summary>
    /// <b>失败记录要在 bundle 出现后自愈</b>：之前 bundle 不在、现在在了
    /// （用户跑了一次游戏 / Unity 缓存被补回来），必须作废那条失败记录并重试，
    /// 否则「补上缓存后仍显示取不到」会变成一个只能靠重启自愈的幽灵。
    /// </summary>
    [Fact]
    public void Failure_record_is_invalidated_once_the_bundle_appears()
    {
        var cache = new SpineResultCache();
        var bundle = Path.Combine(Path.GetTempPath(), $"lme-bundle-{Guid.NewGuid():N}.tmp");

        // 当时 bundle 不在本机：写入时把「不在」这个事实固化下来。
        cache.Set("a.prefab", bundle, null, "它所在的 bundle 不在本机。");
        Assert.True(cache.TryGet("a.prefab", out _, out _), "bundle 还没出现时应当命中那条失败记录");

        // bundle 现在出现了 → 失败记录作废，下次会真的重试。
        File.WriteAllBytes(bundle, [1]);
        try
        {
            Assert.False(cache.TryGet("a.prefab", out _, out _));
            Assert.Equal(1, cache.Invalidated);
        }
        finally
        {
            File.Delete(bundle);
        }
    }

    /// <summary>
    /// 成功结果不因 bundle 凭据变化而作废（它已经证明了素材在那里）。
    /// 顺带锁住「成功命中路径零 IO」：这条路径上不该再碰文件系统。
    /// </summary>
    [Fact]
    public void Successful_result_is_never_invalidated()
    {
        var cache = new SpineResultCache();
        cache.Set("a.prefab", @"Z:\definitely\not\a\real\bundle", Sample(), null);
        Assert.True(cache.TryGet("a.prefab", out var data, out _));
        Assert.NotNull(data);
        Assert.Equal(0, cache.Invalidated);

        Assert.True(cache.TryGet("a.prefab", out var again, out _));
        Assert.NotNull(again);
        Assert.Equal(0, cache.Invalidated);
    }

    /// <summary>缓存有上限：浏览全库 700 套时内存不会被撑爆。</summary>
    [Fact]
    public void Cache_is_bounded()
    {
        var cache = new SpineResultCache();
        for (var i = 0; i < 500; i++) cache.Set($"entry-{i}.prefab", null, Sample($"s{i}"), null);

        // 最早写的那些应当已被淘汰，最近写的还在。
        Assert.False(cache.TryGet("entry-0.prefab", out _, out _));
        Assert.True(cache.TryGet("entry-499.prefab", out var recent, out _));
        Assert.Equal("s499", recent!.Label);
    }

    /// <summary>键不区分大小写（容器路径的大小写不该造成重复解包）。</summary>
    [Fact]
    public void Keys_are_case_insensitive()
    {
        var cache = new SpineResultCache();
        cache.Set("Assets/Prefab/SD/A.prefab", null, Sample(), null);
        Assert.True(cache.TryGet("assets/prefab/sd/a.PREFAB", out _, out _));
    }

    [Fact]
    public void Blank_keys_are_ignored()
    {
        var cache = new SpineResultCache();
        cache.Set("  ", null, Sample(), null);
        Assert.False(cache.TryGet("  ", out _, out _));
    }

    [Fact]
    public void Clear_empties_the_cache()
    {
        var cache = new SpineResultCache();
        cache.Set("a.prefab", null, Sample(), null);
        cache.Clear();
        Assert.False(cache.TryGet("a.prefab", out _, out _));
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Fact]
    public void Payload_budget_evicts_the_least_recently_used_entry_including_mixed_case_hits()
    {
        var cache = new SpineResultCache(2500);
        var sample = Sample() with { SkeletonBytes = new byte[1000] };
        cache.Set("A", null, sample, null);
        cache.Set("B", null, sample, null);
        Assert.True(cache.TryGet("a", out _, out _));
        cache.Set("C", null, sample, null);
        Assert.True(cache.TryGet("A", out _, out _));
        Assert.False(cache.TryGet("B", out _, out _));
        Assert.True(cache.TryGet("C", out _, out _));
        Assert.InRange(cache.RetainedBytes, 2000, 2500);
    }

    [Fact]
    public void Oversized_results_are_not_retained_and_replacements_release_the_old_payload()
    {
        var cache = new SpineResultCache(2500);
        cache.Set("A", null, Sample(), null);
        var before = cache.RetainedBytes;
        cache.Set("B", null, Sample() with { PageBytes = new Dictionary<string, byte[]> { ["large"] = new byte[3000] } }, null);
        Assert.Equal(before, cache.RetainedBytes);
        Assert.True(cache.TryGet("A", out _, out _));
        Assert.False(cache.TryGet("B", out _, out _));
        cache.Set("a", null, Sample() with { SkeletonBytes = new byte[3000] }, null);
        Assert.Equal(0, cache.RetainedBytes);
        Assert.False(cache.TryGet("A", out _, out _));
    }

    [Fact]
    public void Describe_reports_the_diagnostic_counters()
    {
        var cache = new SpineResultCache();
        cache.Set("a.prefab", null, Sample(), null);
        Assert.True(cache.TryGet("a.prefab", out _, out _));
        Assert.False(cache.TryGet("b.prefab", out _, out _));

        var described = cache.Describe();
        Assert.Contains("命中 1", described);
        Assert.Contains("未命中 1", described);
    }
}
