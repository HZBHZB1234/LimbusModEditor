using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

/// <summary>P1.5 read-only summary tests over synthetic Mesh / AnimationClip /
/// Font objects: values come from the type tree; absent fields are reported
/// instead of guessed.</summary>
public class UnityObjectSummaryTests : IDisposable
{
    private readonly string _root;

    public UnityObjectSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-summary-" + Guid.NewGuid().ToString("N"));
        UnityTestAssetBuilder.BuildSummaryFile(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_root, "testsummary.assets");

    [Fact]
    public void Mesh_summary_reports_counts_and_byte_sizes()
    {
        var service = new UnityAssetService();
        var summary = service.ReadObjectSummary(FilePath, 10);
        Assert.NotNull(summary);
        Assert.Equal("Mesh", summary.TypeName);
        Assert.Equal("TestMesh", summary.ObjectName);

        Assert.Contains(summary.Fields, f => f.Label == "子网格数" && f.Value == "2 项");
        Assert.Contains(summary.Fields, f => f.Label == "顶点数" && f.Value == "8");
        Assert.Contains(summary.Fields, f => f.Label == "顶点数据大小" && f.Value == "24 字节");
        Assert.Contains(summary.Fields, f => f.Label == "索引缓冲大小" && f.Value == "6 字节");
        Assert.Empty(summary.Notes);
    }

    [Fact]
    public void AnimationClip_summary_reports_rate_and_type()
    {
        var service = new UnityAssetService();
        var summary = service.ReadObjectSummary(FilePath, 11);
        Assert.NotNull(summary);
        Assert.Equal("AnimationClip", summary.TypeName);
        Assert.Contains(summary.Fields, f => f.Label == "采样率" && f.Value == "60 Hz");
        Assert.Contains(summary.Fields, f => f.Label == "动画类型" && f.Value.Contains("Humanoid"));
    }

    [Fact]
    public void Font_summary_resolves_default_material_dependency()
    {
        var service = new UnityAssetService();
        var summary = service.ReadObjectSummary(FilePath, 12);
        Assert.NotNull(summary);
        Assert.Equal("Font", summary.TypeName);
        Assert.Contains(summary.Fields, f => f.Label == "字号" && f.Value == "16");
        Assert.Contains(summary.Fields, f => f.Label == "字符表条目数" && f.Value == "3 项");
        Assert.Contains(summary.Fields, f => f.Label == "内嵌字体数据大小" && f.Value == "64 字节");
        var material = Assert.Single(summary.Fields, f => f.Label == "默认材质");
        Assert.Contains("同文件 Path 10（Mesh）", material.Value);
    }

    [Fact]
    public void Unknown_types_return_null_instead_of_guess()
    {
        var service = new UnityAssetService();
        var fields = service.ReadObjectFields(FilePath, 11);
        var summary = UnityObjectSummaryBuilder.TryBuild(11, fields[0], []);
        Assert.NotNull(summary);

        // A root type outside the summarized classes yields null.
        var monoRoot = new UnityFieldNode("TestBehaviour", "TestBehaviour", "MonoBehaviour", null, [], "");
        Assert.Null(UnityObjectSummaryBuilder.TryBuild(1, monoRoot, []));
    }

    [Fact]
    public void Describe_lists_rows_and_notes()
    {
        var service = new UnityAssetService();
        var summary = service.ReadObjectSummary(FilePath, 10)!;
        var text = summary.Describe();
        Assert.Contains("Mesh（Path 10）TestMesh", text);
        Assert.Contains("顶点数: 8", text);
    }
}
