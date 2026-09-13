using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Relations;
using Xunit.Abstractions;

namespace LimbusModEditor.Domain.Tests;

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

        // 真实数据下的期望：人格身份来自 lang 语音文件名 + SD 人格预制体；
        // 文本 / 静态数据 / 音频 / 图像四类必须都有——这正是「跨资源关联」的价值所在。
        Assert.True(subjects.Count > 0, "真实数据下必须解析出人格");
        foreach (var kind in new[] { RelationKind.Text, RelationKind.StaticData, RelationKind.Audio, RelationKind.Image })
            Assert.True(byKind.GetValueOrDefault(kind) > 0,
                $"真实数据下应当有「{RelationDisplayRules.KindLabel(kind)}」关联");

        // 反向索引：随便挑一个人格的一条关联，能反查出它属于谁（资源预览的「关联资源」靠它）。
        var sample = store.ReadLinks(subjects[0].SubjectId).First();
        Assert.Contains(subjects[0].SubjectId, store.ReadSubjectIdsByRef(sample.RefKey));

        // 二次调用走缓存（毫秒级），结论一致。
        var second = service.Refresh(null);
        Assert.False(second.Rebuilt);
        Assert.Equal(first.SubjectCount, second.SubjectCount);
        _output.WriteLine($"二次：{second.Describe()}");
    }
}
