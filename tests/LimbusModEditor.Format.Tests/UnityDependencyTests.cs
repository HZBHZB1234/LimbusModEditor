using AssetsTools.NET.Extra;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

/// <summary>P1.2 dependency and referencer tests over the real sample file:
/// resolution of same-file / external / null / dangling pointers, referencer
/// scans, PPtr edit semantic validation and rewrite verification.</summary>
public class UnityDependencyTests : IDisposable
{
    private readonly string _root;

    public UnityDependencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        UnityTestAssetBuilder.BuildMonoBehaviourFile(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_root, "testmono.assets");

    [Fact]
    public void Dependencies_resolve_same_file_external_and_null()
    {
        using var backend = new AssetsToolsBackend();
        var deps = backend.ReadObjectDependencies(FilePath, 1).ToDictionary(d => d.FieldPath, d => d);

        Assert.Equal(UnityDependencyResolution.NullReference, deps["Base.m_GameObject"].Resolution);
        Assert.Equal(UnityDependencyResolution.SameFile, deps["Base.m_Script"].Resolution);
        Assert.Equal(2, deps["Base.m_Script"].PathId);
        Assert.Equal(115, deps["Base.m_Script"].TargetTypeId);
        Assert.Equal("MonoScript", deps["Base.m_Script"].TargetTypeName);
        Assert.Equal(UnityDependencyResolution.ExternalFile, deps["Base.m_Target"].Resolution);
        Assert.Equal("resources.assets", deps["Base.m_Target"].ExternalPath);
        Assert.Equal(100, deps["Base.m_Target"].PathId);
        Assert.Contains("外部文件 resources.assets Path 100", deps["Base.m_Target"].Describe());

        var second = backend.ReadObjectDependencies(FilePath, 3).ToDictionary(d => d.FieldPath, d => d);
        Assert.Equal(UnityDependencyResolution.SameFile, second["Base.m_Target"].Resolution);
        Assert.Equal(1, second["Base.m_Target"].PathId);
        Assert.Equal(114, second["Base.m_Target"].TargetTypeId);
    }

    [Fact]
    public void FindReferencers_returns_same_file_pointers()
    {
        using var backend = new AssetsToolsBackend();
        var scriptReferencers = backend.FindReferencers(FilePath, 2)
            .OrderBy(r => r.SourcePathId).ToArray();
        Assert.Equal(2, scriptReferencers.Length);
        Assert.Equal(1, scriptReferencers[0].SourcePathId);
        Assert.Equal("Base.m_Script", scriptReferencers[0].FieldPath);
        Assert.Equal(3, scriptReferencers[1].SourcePathId);
        Assert.Equal("Base.m_Script", scriptReferencers[1].FieldPath);

        var behaviourReferencers = backend.FindReferencers(FilePath, 1).ToArray();
        var referencer = Assert.Single(behaviourReferencers);
        Assert.Equal(3, referencer.SourcePathId);
        Assert.Equal("Base.m_Target", referencer.FieldPath);

        Assert.Empty(backend.FindReferencers(FilePath, 999));
    }

    [Fact]
    public void Pointer_edits_are_validated_semantically()
    {
        using var backend = new AssetsToolsBackend();
        var ok = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Script.m_PathID"] = "2",
            ["m_Target.m_PathID"] = "0"
        });
        Assert.All(ok, r => Assert.Equal(UnityFieldEditStatus.Ok, r.Status));

        var bad = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Script.m_PathID"] = "999"
        });
        var diagnostic = Assert.Single(bad);
        Assert.Equal(UnityFieldEditStatus.InvalidTarget, diagnostic.Status);
        Assert.Contains("悬空引用", diagnostic.Message);

        var outOfRange = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Target.m_FileID"] = "99"
        });
        var rangeDiagnostic = Assert.Single(outOfRange);
        Assert.Equal(UnityFieldEditStatus.InvalidTarget, rangeDiagnostic.Status);
        Assert.Contains("超出外部引用表范围", rangeDiagnostic.Message);

        // an edit set may retarget both halves at once: file 1 + path 100 stays external
        var combined = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Target.m_FileID"] = "1",
            ["m_Target.m_PathID"] = "100"
        });
        Assert.All(combined, r => Assert.Equal(UnityFieldEditStatus.Ok, r.Status));

        // retargeting m_Target inside the same file must reference an existing object
        var retarget = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Target.m_FileID"] = "0",
            ["m_Target.m_PathID"] = "1"
        });
        Assert.All(retarget, r => Assert.Equal(UnityFieldEditStatus.Ok, r.Status));

        var danglingRetarget = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Target.m_FileID"] = "0",
            ["m_Target.m_PathID"] = "77"
        });
        Assert.Contains(danglingRetarget, r => r.Status == UnityFieldEditStatus.InvalidTarget);
    }

    [Fact]
    public void Verify_detects_no_regressions_for_value_edits()
    {
        using var backend = new AssetsToolsBackend();
        var output = Path.Combine(_root, "edited-value.assets");
        backend.ReplaceObjectFields(FilePath, 3, new Dictionary<string, string>
        {
            ["m_Health"] = "9.75"
        }, output);
        var report = backend.VerifySerializedReferences(FilePath, output);
        Assert.True(report.Ok);
        Assert.Empty(report.Changes);
    }

    [Fact]
    public void Verify_flags_dangling_pointer_after_rewrite()
    {
        using var backend = new AssetsToolsBackend();
        var output = Path.Combine(_root, "edited-dangling.assets");
        backend.ReplaceObjectFields(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Script.m_PathID"] = "999"
        }, output);
        var report = backend.VerifySerializedReferences(FilePath, output);
        Assert.False(report.Ok);
        var regression = Assert.Single(report.Changes);
        Assert.Equal("Base.m_Script", regression.FieldPath);
        Assert.Equal("1", regression.SourcePathId);
        Assert.True(regression.IsRegression);
        Assert.Contains("SameFile", regression.Before);
        Assert.Contains("Missing", regression.After);
    }

    [Fact]
    public void Verify_accepts_deliberate_pointer_retarget()
    {
        using var backend = new AssetsToolsBackend();
        var output = Path.Combine(_root, "edited-retarget.assets");
        // deliberately clear the external pointer: no resolvable dependency is lost
        backend.ReplaceObjectFields(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Target.m_FileID"] = "0",
            ["m_Target.m_PathID"] = "0"
        }, output);
        var report = backend.VerifySerializedReferences(FilePath, output);
        Assert.True(report.Ok);
        var change = Assert.Single(report.Changes);
        Assert.Equal("Base.m_Target", change.FieldPath);
        Assert.False(change.IsRegression);
    }
}
