using AssetsTools.NET;
using AssetsTools.NET.Extra;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

/// <summary>Round-trip tests over a real, hand-built SerializedFile: the
/// enriched field tree (PPtr/enum/vector/byte-array), script info lookup,
/// edit validation and an apply-edit + re-read cycle.</summary>
public class UnityFieldTreeTests : IDisposable
{
    private readonly string _root;

    public UnityFieldTreeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-fieldtree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        UnityTestAssetBuilder.BuildMonoBehaviourFile(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static UnityFieldNode Find(IReadOnlyList<UnityFieldNode> roots, string path)
    {
        foreach (var root in roots)
        {
            var found = TryFind(root, path);
            if (found is not null) return found;
        }
        throw new KeyNotFoundException($"未找到字段: {path}");
    }

    private static UnityFieldNode? TryFind(UnityFieldNode root, string path)
    {
        if (root.Path.Equals(path, StringComparison.Ordinal)) return root;
        foreach (var child in root.Children)
        {
            var found = TryFind(child, path);
            if (found is not null) return found;
        }
        return null;
    }

    private string FilePath => Path.Combine(_root, "testmono.assets");

    [Fact]
    public void FieldTree_carries_pptr_enum_array_and_bytearray_metadata()
    {
        using var backend = new AssetsToolsBackend();
        var tree = backend.ReadObjectFields(FilePath, 1);

        var pptr = Find(tree, "Base.m_Script");
        Assert.True(pptr.IsPPtr);
        Assert.Equal("MonoScript", pptr.PPtrTargetType);
        Assert.Equal(0, pptr.PPtrFileId);
        Assert.Equal(2, pptr.PPtrPathId);
        Assert.False(pptr.Editable);

        var enabled = Find(tree, "Base.m_Enabled");
        Assert.Equal("bool", enabled.ValueType);
        Assert.Equal("True", enabled.Value);
        Assert.True(enabled.Editable);

        var health = Find(tree, "Base.m_Health");
        Assert.Equal("float", health.ValueType);
        Assert.Equal("12.5", health.Value);
        Assert.True(health.Editable);

        var name = Find(tree, "Base.m_Name");
        Assert.Equal("string", name.ValueType);
        Assert.Equal("TestBehaviour", name.Value);
        Assert.True(name.Editable);

        var enumNode = Find(tree, "Base.m_AttackType");
        Assert.True(enumNode.IsEnum);
        Assert.Equal("AttackType", enumNode.EnumTypeName);
        Assert.Equal("int32", enumNode.ValueType);
        Assert.Equal("3", enumNode.Value);
        Assert.True(enumNode.Editable);
        var enumChild = Find(tree, "Base.m_AttackType.value");
        Assert.True(enumChild.Editable);
        Assert.Equal("3", enumChild.Value);

        var vector = Find(tree, "Base.m_Tags");
        Assert.True(vector.IsArray);
        Assert.Equal(3, vector.ArraySize);
        Assert.False(vector.Editable);
        Assert.Equal("5", Find(tree, "Base.m_Tags[0].data").Value);
        Assert.Equal("6", Find(tree, "Base.m_Tags[1].data").Value);
        Assert.Equal("7", Find(tree, "Base.m_Tags[2].data").Value);
        Assert.True(Find(tree, "Base.m_Tags[2].data").Editable);
        Assert.Null(TryFind(Find(tree, "Base.m_Tags"), "Base.m_Tags.size"));

        var data = Find(tree, "Base.m_Data");
        Assert.Equal("byteArray", data.ValueType);
        Assert.Equal(6, data.ByteArrayLength);
        Assert.Equal("DE AD BE EF 00 42", data.ByteArrayPreviewHex);
        Assert.False(data.Editable);
    }

    [Fact]
    public void ScriptInfo_resolves_same_file_mono_script()
    {
        using var backend = new AssetsToolsBackend();
        var script = backend.ReadScriptInfo(FilePath, 1);
        Assert.NotNull(script);
        Assert.Equal(0, script.ScriptFileId);
        Assert.Equal(2, script.ScriptPathId);
        Assert.Equal("TestBehaviourScript", script.ClassName);
        Assert.Equal("LimbusTest", script.Namespace);
        Assert.Equal("Assembly-CSharp.dll", script.AssemblyName);
        Assert.Null(script.TypeTreeMissingReason);
    }

    [Fact]
    public void Validate_reports_ok_for_supported_edits()
    {
        using var backend = new AssetsToolsBackend();
        var results = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Health"] = "30.5",
            ["Base.m_Name"] = "EditedBehaviour",
            ["m_AttackType"] = "7",
            ["m_Tags[0].data"] = "50"
        });
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(UnityFieldEditStatus.Ok, r.Status));
    }

    [Fact]
    public void Validate_reports_unknown_path_parse_error_and_not_editable()
    {
        using var backend = new AssetsToolsBackend();
        var results = backend.ValidateObjectFieldEdits(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Nonexistent"] = "1",
            ["m_Health"] = "abc",
            ["m_AttackType"] = "99999999999",
            ["m_Data"] = "5",
            ["m_Tags"] = "1",
            ["m_GameObject"] = "0"
        });
        var byPath = results.ToDictionary(r => r.Path, r => r);
        Assert.Equal(UnityFieldEditStatus.UnknownPath, byPath["m_Nonexistent"].Status);
        Assert.Equal(UnityFieldEditStatus.ParseError, byPath["m_Health"].Status);
        Assert.Equal(UnityFieldEditStatus.ParseError, byPath["m_AttackType"].Status);
        Assert.Equal(UnityFieldEditStatus.NotEditable, byPath["m_Data"].Status);
        Assert.Equal(UnityFieldEditStatus.NotEditable, byPath["m_Tags"].Status);
        Assert.Equal(UnityFieldEditStatus.NotEditable, byPath["m_GameObject"].Status);
    }

    [Fact]
    public void ApplyEdits_round_trips_primitives_enum_and_array_item()
    {
        using var backend = new AssetsToolsBackend();
        var output = Path.Combine(_root, "edited.assets");
        backend.ReplaceObjectFields(FilePath, 1, new Dictionary<string, string>
        {
            ["m_Health"] = "42.5",
            ["m_AttackType"] = "9",
            ["m_Tags[0].data"] = "50",
            ["m_Name"] = "EditedBehaviour"
        }, output);
        Assert.True(File.Exists(output));

        using var reader = new AssetsToolsBackend();
        var edited = reader.ReadObjectFields(output, 1);
        Assert.Equal("42.5", Find(edited, "Base.m_Health").Value);
        Assert.Equal("9", Find(edited, "Base.m_AttackType").Value);
        Assert.Equal("50", Find(edited, "Base.m_Tags[0].data").Value);
        Assert.Equal("6", Find(edited, "Base.m_Tags[1].data").Value);
        Assert.Equal("EditedBehaviour", Find(edited, "Base.m_Name").Value);
        // untouched fields survive the rewrite byte-exactly
        Assert.Equal("True", Find(edited, "Base.m_Enabled").Value);
        Assert.Equal("DE AD BE EF 00 42", Find(edited, "Base.m_Data").ByteArrayPreviewHex);
        Assert.Equal(2, Find(edited, "Base.m_Script").PPtrPathId);

        // the rewritten file is still a well-formed SerializedFile
        var manager = new AssetsManager();
        var instance = manager.LoadAssetsFile(output, loadDeps: false)!;
        try
        {
            var info = instance.file.GetAssetInfo(1);
            Assert.NotNull(info);
            Assert.Equal(114, info.GetTypeId(instance.file));
        }
        finally { instance.file.Reader.Dispose(); }
    }

    [Fact]
    public void ApplyEdits_rejects_unknown_paths_before_writing()
    {
        using var backend = new AssetsToolsBackend();
        var output = Path.Combine(_root, "should-not-exist.assets");
        Assert.Throws<KeyNotFoundException>(() => backend.ReplaceObjectFields(
            FilePath, 1, new Dictionary<string, string> { ["m_Bogus"] = "1" }, output));
        Assert.False(File.Exists(output));
    }
}
