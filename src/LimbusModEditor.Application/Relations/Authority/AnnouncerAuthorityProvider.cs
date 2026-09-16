namespace LimbusModEditor.Application.Relations.Authority;

public sealed class AnnouncerAuthorityProvider : IAuthorityProvider
{
    public string Category => RelationCategories.Announcer;

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();
        ExtractFromSpriteBaseName(subjectId, context, facts);

        if (!facts.Any())
            unavailable.Add(new UnavailableType("image", "本地数据无来源：无匹配的播报员 sprite"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromSpriteBaseName(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        const string marker = "/Sprite/BattleAnnouncer/";
        const string suffix = "_announcer";
        foreach (var asset in context.Assets)
        {
            var entry = asset.ContainerPath;
            if (string.IsNullOrEmpty(entry) || !entry.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = System.IO.Path.GetFileNameWithoutExtension(entry);
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var key = fileName.ToLowerInvariant();
            if (!string.Equals(key, subjectId.Split(':')[1], StringComparison.OrdinalIgnoreCase)) continue;

            facts.Add(new AuthorityFact(subjectId, "image", entry,
                AuthoritySource.SpriteBaseNameMatch, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = entry,
                Display = fileName,
                Detail = $"{asset.Type} · {HumanSize(asset.Size)}",
                MediaKind = "image",
                DeepLink = RelationDeepLink.ForAsset(entry),
                SourceDetail = $"Sprite 基名精确匹配 {marker}*{suffix}",
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
