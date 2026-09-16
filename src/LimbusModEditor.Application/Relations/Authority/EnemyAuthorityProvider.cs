namespace LimbusModEditor.Application.Relations.Authority;

public sealed class EnemyAuthorityProvider : IAuthorityProvider
{
    public string Category => RelationCategories.Enemy;

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();
        ExtractFromContainerPath(subjectId, context, facts);
        ExtractFromStaticTableForeignKey(subjectId, context, facts);

        if (!facts.Any(f => f.ContentType == "static_data"))
            unavailable.Add(new UnavailableType("static_data", "本地数据无来源：无匹配的静态表"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromContainerPath(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        const string marker = "/Prefab/SD/Enemy/";
        foreach (var asset in context.Assets)
        {
            var entry = asset.ContainerPath;
            if (string.IsNullOrEmpty(entry) || !entry.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.Contains(subjectId.Split(':')[1], StringComparison.OrdinalIgnoreCase)) continue;

            var kind = RelationDisplayRules.Classify(entry, asset.Type);
            facts.Add(new AuthorityFact(subjectId, kind.ToString().ToLowerInvariant(), entry,
                AuthoritySource.ContainerPathPrefix, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = entry,
                Display = System.IO.Path.GetFileName(entry),
                Detail = $"{asset.Type} · {HumanSize(asset.Size)}",
                MediaKind = RelationDisplayRules.MediaKindOf(kind),
                DeepLink = RelationDeepLink.ForAsset(entry),
                SourceDetail = $"容器路径前缀 {marker}",
            });
        }
    }

    private static void ExtractFromStaticTableForeignKey(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        var key = subjectId.Split(':')[1];
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.Text)) continue;
            if (!table.Text.Contains($"\"enemyId\":{key}", StringComparison.Ordinal) &&
                !table.Text.Contains($"\"enemyId\": {key}", StringComparison.Ordinal) &&
                !table.Text.Contains($"\"characterId\":{key}", StringComparison.Ordinal) &&
                !table.Text.Contains($"\"characterId\": {key}", StringComparison.Ordinal))
                continue;

            facts.Add(new AuthorityFact(subjectId, "static_data", table.ContainerEntry,
                AuthoritySource.StaticTableForeignKey, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = table.ContainerEntry,
                Display = table.Name,
                Detail = $"{table.DataClass} · {HumanSize(table.SizeBytes)}",
                MediaKind = "static",
                DeepLink = RelationDeepLink.ForStatic(table.ContainerEntry, null),
                SourceDetail = $"静态表外键 enemyId/characterId={key}",
            });
        }
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };
}
