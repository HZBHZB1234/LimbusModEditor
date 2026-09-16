namespace LimbusModEditor.Application.Relations.Authority;

public sealed class AbnormalityAuthorityProvider : IAuthorityProvider
{
    public string Category => RelationCategories.Abnormality;

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();
        ExtractFromContainerPath(subjectId, context, facts);

        if (!facts.Any())
            unavailable.Add(new UnavailableType("prefab", "本地数据无来源：无匹配的异想体 prefab"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromContainerPath(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        const string marker = "/Prefab/SD/Abnormality/";
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

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };
}
