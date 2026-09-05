using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// Builds small, real Unity SerializedFiles for backend round-trip tests: a
/// MonoBehaviour with primitive, enum, PPtr, vector and byte-array fields plus
/// the MonoScript it points at. The type tree is written as raw nodes and the
/// asset payloads are serialized by hand, matching the layout AssetsTools.NET
/// reads back.
/// </summary>
internal static class UnityTestAssetBuilder
{
    public const string UnityVersion = "2021.3.0f1";
    public const uint WindowsStandalonePlatform = 19;
    private const uint AlignMetaFlag = 0x4000;
    private const uint ArrayTypeFlag = 1;

    /// <summary>A leaf or container description; children appear below it.</summary>
    private sealed record N(string Type, string Name, byte Level, bool IsArray = false, bool Aligned = false);

    private static readonly N[] MonoScriptTree =
    [
        new("MonoScript", "Base", 0),
        new("string", "m_Name", 1),
        new("Type[]", "Array", 2),
        new("char", "data", 3),
        new("string", "m_ClassName", 1),
        new("Type[]", "Array", 2),
        new("char", "data", 3),
        new("string", "m_Namespace", 1),
        new("Type[]", "Array", 2),
        new("char", "data", 3),
        new("string", "m_AssemblyName", 1),
        new("Type[]", "Array", 2),
        new("char", "data", 3)
    ];

    private static readonly N[] MonoBehaviourTree =
    [
        new("MonoBehaviour", "Base", 0),
        new("PPtr<$GameObject>", "m_GameObject", 1),
        new("int", "m_FileID", 2),
        new("SInt64", "m_PathID", 2),
        new("bool", "m_Enabled", 1),
        new("PPtr<$MonoScript>", "m_Script", 1),
        new("int", "m_FileID", 2),
        new("SInt64", "m_PathID", 2),
        new("string", "m_Name", 1),
        new("Type[]", "Array", 2),
        new("char", "data", 3),
        new("float", "m_Health", 1),
        new("AttackType", "m_AttackType", 1),
        new("int", "value", 2),
        // Cross-file PPtr: exercises the external reference table (FileID 1).
        new("PPtr<$Sprite>", "m_Target", 1),
        new("int", "m_FileID", 2),
        new("SInt64", "m_PathID", 2),
        // Vector arrays: exactly two children under the flagged node — the
        // int size and the unflagged item template ("Type[]" maps to None).
        new("vector", "m_Tags", 1, IsArray: true, Aligned: true),
        new("int", "size", 2),
        new("Type[]", "Array", 2),
        new("int", "data", 3),
        // Byte arrays: the item template's type name must map to UInt8 so
        // FromTypeTree classifies the parent as ByteArray.
        new("TypelessData", "m_Data", 1, IsArray: true, Aligned: true),
        new("int", "size", 2),
        new("unsigned char", "Array", 2)
    ];

    /// <summary>Sample layout: a MonoBehaviour (path 1) pointing at the
    /// MonoScript (path 2) and an external Sprite (file 1, path 100), plus a
    /// second MonoBehaviour (path 3) that also references the script and the
    /// first behaviour — enough shapes to test dependencies and referencers.</summary>
    public static string BuildMonoBehaviourFile(string directory, string fileName = "testmono.assets")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = 21, Endianness = false },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion,
                TargetPlatform = WindowsStandalonePlatform,
                TypeTreeEnabled = true,
                TypeTreeTypes = [],
                AssetInfos = [],
                ScriptTypes = [],
                Externals = [],
                RefTypes = [],
                UserInformation = string.Empty
            }
        };
        file.Metadata.TypeTreeTypes.Add(BuildTypeTreeType(115, MonoScriptTree));
        file.Metadata.TypeTreeTypes.Add(BuildTypeTreeType(114, MonoBehaviourTree));
        file.Metadata.Externals.Add(new AssetsFileExternal
        {
            PathName = "resources.assets",
            OriginalPathName = string.Empty,
            VirtualAssetPathName = string.Empty,
            // d41d8cd9-8f00-b204-e980-0998ecf8427e as four little-endian words
            Guid = new GUID128 { data0 = 0xD98C1DD4, data1 = 0x04B2008F, data2 = 0xE9800998, data3 = 0x7E42F8EC }
        });

        var scriptInfo = AssetFileInfo.Create(file, 2, 115, 0);
        scriptInfo.Replacer = new ContentReplacerFromBuffer(SerializeMonoScript());
        file.Metadata.AddAssetInfo(scriptInfo);

        var behaviourInfo = AssetFileInfo.Create(file, 1, 114, 0);
        behaviourInfo.Replacer = new ContentReplacerFromBuffer(SerializeMonoBehaviour(
            name: "TestBehaviour", health: 12.5f, attackType: 3, tags: [5, 6, 7],
            data: [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42],
            scriptPathId: 2, targetFileId: 1, targetPathId: 100));
        file.Metadata.AddAssetInfo(behaviourInfo);

        var secondInfo = AssetFileInfo.Create(file, 3, 114, 0);
        secondInfo.Replacer = new ContentReplacerFromBuffer(SerializeMonoBehaviour(
            name: "SecondBehaviour", health: 3.25f, attackType: 1, tags: [1, 2],
            data: [0x11, 0x22],
            scriptPathId: 2, targetFileId: 0, targetPathId: 1));
        file.Metadata.AddAssetInfo(secondInfo);

        using var writer = new AssetsFileWriter(path);
        file.Write(writer, 0);
        return path;
    }

    private static byte[] SerializeMonoScript()
    {
        using var stream = new MemoryStream();
        using var writer = new AssetsFileWriter(stream);
        WriteString(writer, "TestScript");
        WriteString(writer, "TestBehaviourScript");
        WriteString(writer, "LimbusTest");
        WriteString(writer, "Assembly-CSharp.dll");
        return stream.ToArray();
    }

    private static byte[] SerializeMonoBehaviour(string name, float health, int attackType, int[] tags,
        byte[] data, long scriptPathId, int targetFileId, long targetPathId)
    {
        using var stream = new MemoryStream();
        using var writer = new AssetsFileWriter(stream);
        // m_GameObject PPtr (file 0, path 0 — deliberately null)
        writer.Write(0);
        writer.Write(0L);
        // m_Enabled
        writer.Write((byte)1);
        // m_Script PPtr (file 0 → the MonoScript in this file)
        writer.Write(0);
        writer.Write(scriptPathId);
        // m_Name
        WriteString(writer, name);
        // m_Health
        writer.Write(health);
        // m_AttackType (enum carried by its "value" child)
        writer.Write(attackType);
        // m_Target PPtr (cross-file when targetFileId > 0)
        writer.Write(targetFileId);
        writer.Write(targetPathId);
        // m_Tags vector<int> (aligned)
        writer.Write(tags.Length);
        foreach (var tag in tags) writer.Write(tag);
        writer.Align();
        // m_Data TypelessData (aligned)
        writer.Write(data.Length);
        writer.Write(data);
        writer.Align();
        return stream.ToArray();
    }

    private static void WriteString(AssetsFileWriter writer, string text)
    {
        writer.Write(text.Length);
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Align();
    }

    private static TypeTreeType BuildTypeTreeType(int typeId, N[] nodes)
    {
        var offsets = new Dictionary<string, uint>();
        var buffer = new List<byte>();
        uint Offset(string text)
        {
            if (offsets.TryGetValue(text, out var existing)) return existing;
            var offset = (uint)buffer.Count;
            buffer.AddRange(Encoding.UTF8.GetBytes(text));
            buffer.Add(0);
            offsets[text] = offset;
            return offset;
        }

        var treeNodes = new List<TypeTreeNode>();
        foreach (var node in nodes)
        {
            treeNodes.Add(new TypeTreeNode
            {
                Version = 3,
                Level = node.Level,
                TypeFlags = node.IsArray ? (TypeTreeNodeFlags)ArrayTypeFlag : TypeTreeNodeFlags.None,
                TypeStrOffset = Offset(node.Type),
                NameStrOffset = Offset(node.Name),
                ByteSize = ByteSizeOf(node.Type),
                Index = unchecked((uint)-1),
                MetaFlags = node.Aligned ? AlignMetaFlag : 0,
                RefTypeHash = 0
            });
        }

        return new TypeTreeType
        {
            TypeId = typeId,
            IsStrippedType = false,
            ScriptTypeIndex = 0,
            // Hash data arrays must be non-null; the Write path writes them raw.
            ScriptIdHash = new Hash128(new byte[16]),
            TypeHash = new Hash128(new byte[16]),
            ExtTypeHash = new Hash128(new byte[16]),
            TypeDependencies = [],
            // Nodes/StringBufferBytes forward to the TypeBlob, which must be
            // initialized before either property is touched.
            TypeBlob = new TypeTreeBlob(),
            Nodes = treeNodes,
            StringBufferBytes = [.. buffer]
        };
    }

    private static int ByteSizeOf(string type) => type switch
    {
        "bool" or "char" or "unsigned char" => 1,
        "int" or "unsigned int" or "float" => 4,
        "SInt64" or "double" => 8,
        _ => -1
    };
}
