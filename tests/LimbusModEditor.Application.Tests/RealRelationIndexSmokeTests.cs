using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 真实数据门控（手动）冒烟：在真实发布目录上跑一次<b>人格关联分析</b>，验证
/// 「四个索引库 → 关联图」这条派生链路在真实规模下真的产出结果，并打印
/// 人格数 / 各类关联条数 / 耗时——这些数字就是「关联资源板块与卡片流页面能不能用」的直接证据。
///
/// <para>默认跳过（常规套件保持快）：需要 <c>LME_RELATION_SMOKE=1</c> 与 <c>LME_APP_DIR</c>
/// （发布版目录，里面要有 <c>cache/</c> 下已建好的四个索引库）。</para>
/// </summary>
public class RealRelationIndexSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealRelationIndexSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Relation_analysis_over_the_real_index_databases_produces_personas()
    {
        if (Environment.GetEnvironmentVariable("LME_RELATION_SMOKE") != "1") return;
        var appDirectory = Environment.GetEnvironmentVariable("LME_APP_DIR");
        if (string.IsNullOrWhiteSpace(appDirectory) || !Directory.Exists(appDirectory))
        {
            _output.WriteLine("LME_APP_DIR 未指向现有目录：跳过。");
            return;
        }

        var env = new AppEnvironment(appDirectory);
        _output.WriteLine($"程序目录：{env.BaseDirectory}");
        _output.WriteLine($"游戏目录：{env.EffectiveGameDirectory(null)}");

        var service = new PersonaRelationIndexService(env);
        var watch = Stopwatch.StartNew();
        var first = service.Refresh(null);
        watch.Stop();
        _output.WriteLine($"首次：{first.Describe()}（总 {watch.Elapsed.TotalSeconds:0.0} 秒）");

        var store = service.CreateStore();
        var subjects = store.ReadSubjects();
        foreach (var subject in subjects.Take(20))
            _output.WriteLine($"  {subject.SubjectId} · {subject.DisplayName}（{subject.Character}）");

        var byKind = new Dictionary<RelationKind, int>();
        foreach (var subject in subjects)
        foreach (var link in store.ReadLinks(subject.SubjectId))
            byKind[link.Kind] = byKind.GetValueOrDefault(link.Kind) + 1;
        foreach (var (kind, count) in byKind.OrderByDescending(x => x.Value))
            _output.WriteLine($"  {RelationDisplayRules.KindLabel(kind)}：{count} 条");

        var byCategory = subjects.GroupBy(x => x.SubjectKind)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{RelationCategories.Label(x.Key)} {x.Count()}");
        _output.WriteLine($"类别：{string.Join(" · ", byCategory)}");
        _output.WriteLine($"跨资源边：{store.ReadXrefCount():N0} 条");

        // 真实数据下的期望：**按上游源实际有没有数据**分别断言。
        // 发布目录里 bank-index.db / static-tables.db 是有可能还没建好的（本机实测就是 0 行），
        // 那时「没有音频关联」是正确结论、不是缺陷；拿它当失败会把「夹具不全」误报成「代码坏了」。
        // 反过来，只要上游有数据，对应的关联就必须出得来——这才是这条冒烟的真正价值。
        Assert.True(subjects.Count > 0, "真实数据下必须解析出对象");

        var assetRows = new UnityCacheSqliteIndexStore(
            Path.Combine(env.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName))
            .ReadContainerRows().Count;
        var audioRows = new BankIndexStore(env.CacheDirectory).ReadAllSamples().Count;
        _output.WriteLine($"上游规模：资源行 {assetRows:N0} · 音频样本 {audioRows:N0}");

        Assert.True(assetRows > 0, "资源索引应当有带容器路径的行（本机实测约 5 万行）");
        foreach (var kind in new[] { RelationKind.Image, RelationKind.Prefab })
            Assert.True(byKind.GetValueOrDefault(kind) > 0,
                $"资源索引有数据时「{RelationDisplayRules.KindLabel(kind)}」关联必须出得来");
        Assert.True(byKind.GetValueOrDefault(RelationKind.Text) > 0,
            "lang 文本索引有数据时应当有「文本」关联");

        if (audioRows > 0)
            Assert.True(byKind.GetValueOrDefault(RelationKind.Audio) > 0,
                $"音频索引有 {audioRows:N0} 个样本时应当有「音频」关联");
        else
            _output.WriteLine("音频索引为空 → 跳过音频断言（是夹具不全，不是缺陷）");

        // 反向索引：随便挑一个对象的一条关联，能反查出它属于谁（资源预览的「关联资源」靠它）。
        var sample = store.ReadLinks(subjects[0].SubjectId).First();
        Assert.Contains(subjects[0].SubjectId, store.ReadSubjectIdsByRef(sample.RefKey));

        // 二次调用走缓存（毫秒级），结论一致。
        var second = service.Refresh(null);
        Assert.False(second.Rebuilt);
        Assert.Equal(first.SubjectCount, second.SubjectCount);
        _output.WriteLine($"二次：{second.Describe()}");
    }
}
