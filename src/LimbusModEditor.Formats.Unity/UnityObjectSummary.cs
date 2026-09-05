namespace LimbusModEditor.Formats.Unity;

/// <summary>One human-readable line of a P1.5 read-only object summary.</summary>
public sealed record UnityObjectSummaryField(string Label, string Value);

/// <summary>Read-only structural summary of a Unity object (P1.5): the values
/// come only from the file's own type tree; a field the tree does not contain
/// is reported as absent in <see cref="Notes"/> instead of being guessed.</summary>
public sealed record UnityObjectSummary(
    long PathId,
    string TypeName,
    string? ObjectName,
    IReadOnlyList<UnityObjectSummaryField> Fields,
    IReadOnlyList<string> Notes)
{
    public string Describe()
    {
        var lines = new List<string> { $"{TypeName}（Path {PathId}）{ObjectName ?? string.Empty}".TrimEnd() };
        lines.AddRange(Fields.Select(f => $"  {f.Label}: {f.Value}"));
        lines.AddRange(Notes.Select(n => $"  ⚠ {n}"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Builds read-only summaries for Mesh (43), AnimationClip (74) and
/// Font (128) from the generic field tree plus resolved dependencies. Field
/// names follow Unity's serialized type reference; anything missing in the
/// real type tree is reported, never fabricated.</summary>
public static class UnityObjectSummaryBuilder
{
    public static UnityObjectSummary? TryBuild(long pathId, UnityFieldNode root,
        IReadOnlyList<UnityDependency> dependencies)
    {
        var fields = root.Type switch
        {
            "Mesh" => MeshFields,
            "AnimationClip" => AnimationClipFields,
            "Font" => FontFields,
            _ => null
        };
        if (fields is null) return null;

        var rows = new List<UnityObjectSummaryField>();
        var notes = new List<string>();
        var missing = new List<string>();
        foreach (var (label, suffix, kind) in fields)
        {
            var node = Find(root, suffix);
            if (node is null)
            {
                missing.Add(suffix);
                continue;
            }
            rows.Add(new UnityObjectSummaryField(label, Describe(node, dependencies, kind)));
        }
        if (missing.Count > 0)
            notes.Add($"以下字段在该版本/剥离的类型树中不存在，摘要未包含：{string.Join("、", missing)}。");

        var objectName = Find(root, "m_Name")?.Value;
        return new UnityObjectSummary(pathId, root.Type, string.IsNullOrEmpty(objectName) ? null : objectName, rows, notes);
    }

    private static UnityFieldNode? Find(UnityFieldNode root, string suffix)
    {
        var current = root;
        foreach (var segment in suffix.Split('.'))
        {
            var next = current.Children.FirstOrDefault(c =>
                c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) &&
                !c.Name.StartsWith("[", StringComparison.Ordinal));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private static string Describe(UnityFieldNode node,
        IReadOnlyList<UnityDependency> dependencies, SummaryKind kind)
    {
        return kind switch
        {
            SummaryKind.AnswerSampleRate => $"{node.Value} Hz",
            SummaryKind.AnswerAnimationType when int.TryParse(node.Value, out var animationType) =>
                animationType switch
                {
                    0 => "0（旧版 Legacy）",
                    1 => "1（Generic）",
                    2 => "2（Humanoid）",
                    _ => node.Value ?? "未知"
                },
            SummaryKind.ByteCount when node.ByteArrayLength > 0 => $"{node.ByteArrayLength:N0} 字节",
            SummaryKind.ArrayCount when node.IsArray => $"{node.ArraySize:N0} 项",
            SummaryKind.PPtr when node.IsPPtr =>
                dependencies.FirstOrDefault(d => d.FieldPath.Equals(node.Path, StringComparison.Ordinal))?.Describe()
                ?? $"File {node.PPtrFileId} / Path {node.PPtrPathId}",
            _ => node.Value ?? "—"
        };
    }

    private static readonly (string Label, string Suffix, SummaryKind Kind)[] MeshFields =
    [
        ("子网格数", "m_SubMeshes", SummaryKind.ArrayCount),
        ("顶点数", "m_VertexData.m_VertexCount", SummaryKind.Raw),
        ("顶点数据大小", "m_VertexData.m_DataSize", SummaryKind.ByteCount),
        ("索引缓冲大小", "m_IndexBuffer", SummaryKind.ByteCount)
    ];

    private static readonly (string Label, string Suffix, SummaryKind Kind)[] AnimationClipFields =
    [
        ("旧版模式", "m_Legacy", SummaryKind.Raw),
        ("采样率", "m_SampleRate", SummaryKind.AnswerSampleRate),
        ("动画类型", "m_AnimationType", SummaryKind.AnswerAnimationType)
    ];

    private static readonly (string Label, string Suffix, SummaryKind Kind)[] FontFields =
    [
        ("字号", "m_FontSize", SummaryKind.Raw),
        ("字符表条目数", "m_CharacterData", SummaryKind.ArrayCount),
        ("内嵌字体数据大小", "m_FontData", SummaryKind.ByteCount),
        ("默认材质", "m_DefaultMaterial", SummaryKind.PPtr)
    ];

    private enum SummaryKind { Raw, ArrayCount, ByteCount, PPtr, AnswerAnimationType, AnswerSampleRate }
}
