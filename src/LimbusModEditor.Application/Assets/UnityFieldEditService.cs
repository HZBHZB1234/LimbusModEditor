using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Assets;

/// <summary>One requested field edit from the editor UI.</summary>
public sealed record UnityFieldEditDraft(string Path, string NewValue);

/// <summary>
/// Validates and stores versioned field edits for Unity serialized objects.
/// Every edit is checked against the field tree's real value type before it is
/// saved; the stored payload keeps original value, hash, timestamp, author and
/// a per-path revision so builds can be audited. The format layer applies them
/// during Bundle/SerializedFile build.
/// </summary>
public sealed class UnityFieldEditService
{
    public const string MetadataKey = "unityFieldEdits";

    public UnityFieldEditSet? ReadStored(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!asset.Metadata.TryGetValue(MetadataKey, out var json)) return null;
        return UnityFieldEditSetCodec.Deserialize(json);
    }

    /// <summary>Replaces the stored edit set with the given drafts. Only the
    /// selected drafts are saved — unlisted paths are dropped.</summary>
    public void Set(ModProject project, AssetRecord asset, IReadOnlyList<UnityFieldNode> roots,
        IEnumerable<UnityFieldEditDraft> drafts, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(drafts);
        var nodes = IndexNodes(roots);
        var previous = ReadStored(asset);
        var previousRevisions = previous?.Edits
            .GroupBy(x => x.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Revision), StringComparer.Ordinal) ?? [];

        var entries = new List<UnityFieldEdit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var resolvedAuthor = string.IsNullOrWhiteSpace(author) ? Environment.UserName : author.Trim();
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Path)) throw new ArgumentException("字段路径不能为空。", nameof(drafts));
            var path = draft.Path.Trim();
            if (!seen.Add(path)) throw new ArgumentException($"字段路径重复: {path}", nameof(drafts));
            if (!nodes.TryGetValue(path, out var node))
                throw new KeyNotFoundException($"未找到 Unity 字段路径: {path}");
            if (!node.Editable)
                throw new InvalidOperationException($"字段 {path}（类型 {node.Type}）不是可编辑的基础字段。");
            if (!UnityFieldValueParser.TryValidate(node.ValueType, draft.NewValue ?? string.Empty, out var error))
                throw new FormatException($"字段 {path}: {error}");
            entries.Add(new UnityFieldEdit(
                path,
                node.ValueType,
                node.Value,
                draft.NewValue ?? string.Empty,
                UnityFieldEditSetCodec.HashValue(node.Value),
                now,
                resolvedAuthor,
                previousRevisions.TryGetValue(path, out var revision) ? revision + 1 : 1));
        }
        if (entries.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(drafts));

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        asset.Metadata[MetadataKey] = UnityFieldEditSetCodec.Serialize(
            new UnityFieldEditSet(UnityFieldEditSetCodec.CurrentSchemaVersion, entries));
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            BeforeHash = asset.OriginalHash
        });
    }

    public void Clear(ModProject project, AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        asset.Metadata.Remove(MetadataKey);
        if (asset.EditState == AssetEditState.Modified) asset.EditState = AssetEditState.Unchanged;
    }

    private static Dictionary<string, UnityFieldNode> IndexNodes(IReadOnlyList<UnityFieldNode> roots)
    {
        var nodes = new Dictionary<string, UnityFieldNode>(StringComparer.Ordinal);
        foreach (var root in roots) IndexNode(root, nodes);
        return nodes;
    }

    private static void IndexNode(UnityFieldNode node, IDictionary<string, UnityFieldNode> nodes)
    {
        nodes[node.Path] = node;
        foreach (var child in node.Children) IndexNode(child, nodes);
    }
}
