namespace LimbusModEditor.Application.Relations.Authority;

public sealed class EgoGiftAuthorityProvider : IAuthorityProvider
{
    public string Category => RelationCategories.EgoGift;

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();
        ExtractFromLangAuthoritativeList(subjectId, context, facts);

        if (!facts.Any())
            unavailable.Add(new UnavailableType("text", "本地数据无来源：EGOgift*.json 中无此 id"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromLangAuthoritativeList(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        var key = subjectId.Split(':')[1];
        foreach (var anchor in context.TextAnchors)
        {
            if (anchor.Table != RelationAnchorTables.EgoGift) continue;
            if (!string.Equals(anchor.Anchor, key, StringComparison.OrdinalIgnoreCase)) continue;

            facts.Add(new AuthorityFact(subjectId, "text", anchor.RelativePath,
                AuthoritySource.LangAuthoritativeList, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = anchor.RelativePath,
                Display = anchor.Name ?? key,
                Detail = anchor.Desc,
                MediaKind = "text",
                DeepLink = RelationDeepLink.ForText(anchor.RelativePath, anchor.Anchor),
                SourceDetail = $"Lang 权威清单 EGOgift*.json id={key}",
            });
        }
    }
}
