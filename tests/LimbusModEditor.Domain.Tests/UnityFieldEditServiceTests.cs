using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

public sealed class UnityFieldEditServiceTests
{
    private static ModProject Project() => new();

    private static AssetRecord Asset() => new()
    {
        LogicalPath = "main.assets/10.114",
        UnityPathId = 10,
        UnityTypeId = 114,
        Metadata = { ["unitySerializedFile"] = "true" }
    };

    private static UnityFieldNode Node(string path, string type, string valueType, string? value,
        params UnityFieldNode[] children)
        => new(path, path[(path.LastIndexOf('.') + 1)..], type, value, children, valueType, Editable: UnityFieldNode.IsEditableValueType(valueType));

    private static readonly UnityFieldNode SampleTree =
        Node("Base", "MonoBehaviour", "unknown", null,
            Node("Base.m_Enabled", "bool", "bool", "True"),
            Node("Base.m_Name", "string", "string", "TestBehaviour"),
            Node("Base.m_Health", "float", "float", "12.5"),
            Node("Base.m_AttackType", "AttackType", "int32", "3", IsEnum: true, EnumTypeName: "AttackType"),
            Node("Base.m_Script", "PPtr<$MonoScript>", "unknown", null, IsPPtr: true, PPtrFileId: 0, PPtrPathId: 2, PPtrTargetType: "MonoScript"),
            Node("Base.m_Data", "TypelessData", "byteArray", "<6 bytes>", ByteArrayLength: 6));

    private static UnityFieldNode Node(string path, string type, string valueType, string? value,
        bool IsEnum = false, string EnumTypeName = "", bool IsPPtr = false,
        long PPtrFileId = 0, long PPtrPathId = 0, string? PPtrTargetType = null,
        long ByteArrayLength = 0, params UnityFieldNode[] children)
        => new(path, path[(path.LastIndexOf('.') + 1)..], type, value, children,
            valueType, IsEnum: IsEnum, EnumTypeName: EnumTypeName, IsPPtr: IsPPtr,
            PPtrFileId: PPtrFileId, PPtrPathId: PPtrPathId, PPtrTargetType: PPtrTargetType,
            ByteArrayLength: ByteArrayLength,
            Editable: UnityFieldNode.IsEditableValueType(valueType) || IsEnum);

    [Fact]
    public void StoresVersionedFieldEdits()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        service.Set(project, asset, [SampleTree],
        [
            new UnityFieldEditDraft("Base.m_Enabled", "false"),
            new UnityFieldEditDraft("Base.m_Name", "Edited")
        ], author: "tester");
        var stored = service.ReadStored(asset);
        Assert.NotNull(stored);
        Assert.Equal(UnityFieldEditSetCodec.CurrentSchemaVersion, stored!.SchemaVersion);
        Assert.Equal(2, stored.Edits.Count);
        var enabled = stored.Edits.Single(x => x.Path == "Base.m_Enabled");
        Assert.Equal("bool", enabled.FieldType);
        Assert.Equal("True", enabled.OriginalValue);
        Assert.Equal("false", enabled.NewValue);
        Assert.Equal(UnityFieldEditSetCodec.HashValue("True"), enabled.OriginalValueHash);
        Assert.Equal("tester", enabled.Author);
        Assert.Equal(1, enabled.Revision);
        Assert.Single(project.Edits);
        Assert.Equal(AssetEditState.Modified, asset.EditState);
    }

    [Fact]
    public void BumpsRevisionForRepeatedEdits()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        service.Set(project, asset, [SampleTree], [new UnityFieldEditDraft("Base.m_Health", "20")]);
        service.Set(project, asset, [SampleTree], [new UnityFieldEditDraft("Base.m_Health", "30")]);
        var stored = service.ReadStored(asset);
        var edit = Assert.Single(stored!.Edits);
        Assert.Equal(2, edit.Revision);
        Assert.Equal("30", edit.NewValue);
        Assert.Equal("12.5", edit.OriginalValue);
    }

    [Fact]
    public void RejectsUnknownPath()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        Assert.Throws<KeyNotFoundException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_Nonexistent", "1")]));
        Assert.False(asset.Metadata.ContainsKey(UnityFieldEditService.MetadataKey));
    }

    [Fact]
    public void RejectsValueMismatchingFieldType()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        Assert.Throws<FormatException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_Health", "not-a-float")]));
        Assert.Throws<FormatException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_Enabled", "2")]));
        Assert.Throws<FormatException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_AttackType", "99999999999")]));
        Assert.False(asset.Metadata.ContainsKey(UnityFieldEditService.MetadataKey));
    }

    [Fact]
    public void RejectsNonEditableNodes()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        Assert.Throws<InvalidOperationException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_Data", "AB")]));
        Assert.Throws<InvalidOperationException>(() => service.Set(project, asset, [SampleTree],
            [new UnityFieldEditDraft("Base.m_Script", "1")]));
    }

    [Fact]
    public void ClearRemovesStoredEdits()
    {
        var project = Project();
        var asset = Asset();
        var service = new UnityFieldEditService();
        service.Set(project, asset, [SampleTree], [new UnityFieldEditDraft("Base.m_Enabled", "false")]);
        service.Clear(project, asset);
        Assert.Null(service.ReadStored(asset));
        Assert.Equal(AssetEditState.Unchanged, asset.EditState);
    }

    [Fact]
    public void CodecRoundTripsVersionedSet()
    {
        var set = new UnityFieldEditSet(UnityFieldEditSetCodec.CurrentSchemaVersion,
        [
            new UnityFieldEdit("Base.m_Health", "float", "12.5", "20",
                UnityFieldEditSetCodec.HashValue("12.5"), new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "tester", 2)
        ]);
        var json = UnityFieldEditSetCodec.Serialize(set);
        var parsed = UnityFieldEditSetCodec.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(UnityFieldEditSetCodec.CurrentSchemaVersion, parsed!.SchemaVersion);
        var edit = Assert.Single(parsed.Edits);
        Assert.Equal("Base.m_Health", edit.Path);
        Assert.Equal("float", edit.FieldType);
        Assert.Equal("12.5", edit.OriginalValue);
        Assert.Equal("20", edit.NewValue);
        Assert.Equal(2, edit.Revision);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), edit.EditedAt);
        var map = UnityFieldEditSetCodec.ToPathValueMap(parsed);
        Assert.Equal("20", map["Base.m_Health"]);
    }

    [Fact]
    public void CodecReadsLegacyDictionaryPayload()
    {
        var legacy = """{"Base.m_Enabled":"false","Base.m_Speed":"1.25"}""";
        var set = UnityFieldEditSetCodec.Deserialize(legacy);
        Assert.NotNull(set);
        Assert.Equal(1, set!.SchemaVersion);
        Assert.Equal(2, set.Edits.Count);
        var speed = set.Edits.Single(x => x.Path == "Base.m_Speed");
        Assert.Null(speed.OriginalValue);
        Assert.Equal(1, speed.Revision);
        var map = UnityFieldEditSetCodec.ToPathValueMap(set);
        Assert.Equal("false", map["Base.m_Enabled"]);
        Assert.Equal("1.25", map["Base.m_Speed"]);
    }
}
