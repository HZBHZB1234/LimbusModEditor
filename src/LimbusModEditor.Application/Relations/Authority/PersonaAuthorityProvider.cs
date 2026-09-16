namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 人格（Persona）类别的权威来源提供者。
/// </summary>
public sealed class PersonaAuthorityProvider : IAuthorityProvider
{
    public string Category => RelationCategories.Persona;

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();

        ExtractFromContainerPath(subjectId, context, facts);
        ExtractFromLangFileName(subjectId, context, facts);
        ExtractFromBankSamples(subjectId, context, facts);
        ExtractFromStaticTableForeignKey(subjectId, context, facts);

        if (!facts.Any(f => f.ContentType == "text"))
            unavailable.Add(new UnavailableType("text", "本地数据无来源：无匹配的 lang 文件"));
        if (!facts.Any(f => f.ContentType == "audio"))
            unavailable.Add(new UnavailableType("audio", "本地数据无来源：无匹配的 bank 样本"));
        if (!facts.Any(f => f.ContentType == "static_data"))
            unavailable.Add(new UnavailableType("static_data", "本地数据无来源：无匹配的静态表"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromContainerPath(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        const string marker = "/Prefab/SD/Personality/";
        foreach (var asset in context.Assets)
        {
            var entry = asset.ContainerPath;
            if (string.IsNullOrEmpty(entry) || !entry.Contains(marker, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.Contains(subjectId.Split(':')[1], StringComparison.OrdinalIgnoreCase))
                continue;

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

    private static void ExtractFromLangFileName(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        var key = subjectId.Split(':')[1];
        foreach (var lang in context.LangFiles)
        {
            var path = lang.RelativePath;
            if (string.IsNullOrEmpty(path)) continue;

            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!fileName.StartsWith("Voice_", StringComparison.OrdinalIgnoreCase)) continue;

            var tokens = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) continue;
            if (!string.Equals(tokens[^1], key, StringComparison.OrdinalIgnoreCase)) continue;

            facts.Add(new AuthorityFact(subjectId, "text", path,
                AuthoritySource.LangFileName, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = path,
                Display = System.IO.Path.GetFileName(path),
                Detail = $"{lang.KeyCount} 个键",
                MediaKind = "text",
                DeepLink = RelationDeepLink.ForText(path, null),
                SourceDetail = $"Lang 文件名约定 Voice_*_{key}",
            });
        }
    }

    private static void ExtractFromBankSamples(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        var anchors = context.AnchorIndex();
        foreach (var sample in context.Audio)
        {
            if (!anchors.TryGetValue(sample.SampleName, out var anchor)) continue;
            if (!anchor.Anchor.StartsWith(subjectId.Split(':')[1], StringComparison.Ordinal)) continue;

            var refKey = sample.BankPath + RelationDeepLink.Separator + sample.SampleName;
            facts.Add(new AuthorityFact(subjectId, "audio", refKey,
                AuthoritySource.BankSampleExactMatch, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = sample.BankPath,
                Display = sample.SampleName,
                Detail = $"{sample.CodecName ?? "未知编码"} · {HumanSize(sample.SizeBytes)}",
                MediaKind = "audio",
                DurationSec = sample.DurationSeconds,
                DeepLink = RelationDeepLink.ForAudio(sample.BankPath, sample.SampleName),
                SourceDetail = $"Bank 样本名精确匹配 {sample.SampleName}",
            });
        }
    }

    private static void ExtractFromStaticTableForeignKey(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        var key = subjectId.Split(':')[1];
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.Text)) continue;
            if (!table.Text.Contains($"\"characterId\":{key}", StringComparison.Ordinal) &&
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
                SourceDetail = $"静态表外键 characterId={key}",
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
