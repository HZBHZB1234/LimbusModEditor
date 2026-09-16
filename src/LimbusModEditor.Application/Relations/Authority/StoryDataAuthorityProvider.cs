namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 剧情数据权威来源提供者：从 lang 的 StoryData/ 目录提取章节级剧情事实。
///
/// <para><b>权威来源</b>：
/// 1. Lang 文件名约定：`StoryData/S&lt;章节&gt;.json`（章节级）
/// 2. Lang 文件名约定：`AbDlg_&lt;角色&gt;.json`（角色级）</para>
///
/// <para><b>粒度</b>：章节级 + 角色级。若 `dialog` 数据里没有逐句说话者标注，
/// 页面按"章节 → 段落"呈现，不假装"谁说的"。</para>
/// </summary>
public sealed class StoryDataAuthorityProvider : IAuthorityProvider
{
    public string Category => "story";

    public AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = new List<AuthorityFact>();
        var unavailable = new List<UnavailableType>();

        // ① StoryData/S<章节>.json —— 章节级剧情
        ExtractFromStoryData(subjectId, context, facts);

        // ② AbDlg_<角色>.json —— 角色级剧情
        ExtractFromAbDlg(subjectId, context, facts);

        // ③ 静态表 cutscene / storytheater-personality —— 过场 / 登场角色
        ExtractFromStaticTables(subjectId, context, facts);

        // ④ 静态表 subchapter-detail / storytheater-main —— 章节导航
        ExtractChapterNavigation(subjectId, context, facts);

        // ⑤ 静态表 story-dungeon-* / abnormality-event —— 事件与选择
        ExtractStoryEvents(subjectId, context, facts);

        if (!facts.Any())
            unavailable.Add(new UnavailableType("text", "本地数据无来源：无匹配的 StoryData 或 AbDlg 文件"));

        return new AuthorityFacts(subjectId, facts, unavailable);
    }

    private static void ExtractFromStoryData(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        // StoryData/S<章节>.json —— 文件名带章节 token
        foreach (var lang in context.LangFiles)
        {
            var path = lang.RelativePath;
            if (string.IsNullOrEmpty(path)) continue;

            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            var dirName = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');

            // 必须在 StoryData/ 目录下
            if (string.IsNullOrEmpty(dirName) || !dirName.EndsWith("StoryData", StringComparison.OrdinalIgnoreCase))
                continue;

            // 文件名必须带数字段（章节级）。
            // 真实命名是 S001A / 1D101B / ES001B / P01011 等（实测 920 个文件）——
            // 早期那句 int.TryParse(fileName[1..]) 只对 "S1" 成立，对 "S001A" 恒为 false，
            // 等于把全部真实剧情文件都过滤掉了。这里改成「含数字段」判定，
            // 章节键另由 StoryDataReader.ChapterKeyOf 按同一约定推出。
            if (fileName.Length < 2 || !fileName.Any(char.IsAsciiDigit)) continue;

            facts.Add(new AuthorityFact(subjectId, "story_content", path,
                AuthoritySource.LangFileName, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = path,
                Display = fileName,
                Detail = $"{lang.KeyCount} 个键（dataList[].content 为台词正文）",
                MediaKind = "text",
                DeepLink = RelationDeepLink.ForText(path, null),
                SourceDetail = $"Lang 文件名约定 StoryData/{fileName}.json（章节级，含对话正文）",
            });
        }
    }

    private static void ExtractFromAbDlg(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        // AbDlg_<角色>.json —— 角色级剧情
        foreach (var lang in context.LangFiles)
        {
            var path = lang.RelativePath;
            if (string.IsNullOrEmpty(path)) continue;

            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!fileName.StartsWith("AbDlg_", StringComparison.OrdinalIgnoreCase)) continue;

            facts.Add(new AuthorityFact(subjectId, "story_character", path,
                AuthoritySource.LangCharacterToken, ConfidenceLevel.Derived, WritableSourceKind.Path)
            {
                WritableSourcePath = path,
                Display = fileName,
                Detail = $"{lang.KeyCount} 个键",
                MediaKind = "text",
                DeepLink = RelationDeepLink.ForText(path, null),
                SourceDetail = $"Lang 文件名约定 {fileName}（角色级）",
            });
        }
    }

    /// <summary>
    /// 从静态表提取 cutscene 数据（cutscene-* 类别，过场 ID 外键）。
    /// <para>粒度：过场级。若 cutscene 数据里没有逐句说话者标注，按"过场 → 段落"呈现。</para>
    /// </summary>
    private static void ExtractFromStaticTables(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.ContainerEntry)) continue;

            // 匹配 cutscene 类别：Assets/Resources_moved/StaticData/static-data/cutscene/cutscene-*.json
            if (!table.ContainerEntry.Contains("/static-data/cutscene/", StringComparison.OrdinalIgnoreCase))
                continue;

            facts.Add(new AuthorityFact(subjectId, "cutscene", table.ContainerEntry,
                AuthoritySource.StaticTableForeignKey, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = table.ContainerEntry,
                Display = table.Name,
                Detail = $"{table.DataClass} · {table.SizeBytes} B",
                MediaKind = "video",
                DeepLink = RelationDeepLink.ForStatic(table.ContainerEntry, null),
                SourceDetail = $"静态表外键 cutscene（过场级）",
            });
        }

        // 匹配 storytheater-* 类别（登场角色）
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.ContainerEntry)) continue;
            if (!table.ContainerEntry.Contains("/static-data/storytheater-", StringComparison.OrdinalIgnoreCase))
                continue;

            // storytheater-personality 进角色节
            if (table.DataClass.Contains("personality", StringComparison.OrdinalIgnoreCase))
            {
                facts.Add(new AuthorityFact(subjectId, "story_character", table.ContainerEntry,
                    AuthoritySource.StaticTableForeignKey, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
                {
                    WritableSourcePath = table.ContainerEntry,
                    Display = table.Name,
                    Detail = $"{table.DataClass} · {table.SizeBytes} B",
                    MediaKind = "text",
                    DeepLink = RelationDeepLink.ForStatic(table.ContainerEntry, null),
                    SourceDetail = $"静态表外键 storytheater-personality（角色级）",
                });
            }
        }
    }

    /// <summary>
    /// 章节导航（subchapter-detail / storytheater-main）：静态表里的章节定义，
    /// 来源是容器路径 + <c>data_class</c>（<see cref="AuthoritySource.StaticTableForeignKey"/>）。
    /// </summary>
    private static void ExtractChapterNavigation(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.ContainerEntry)) continue;
            if (!table.ContainerEntry.Contains("/static-data/subchapter-detail/", StringComparison.OrdinalIgnoreCase) &&
                !table.ContainerEntry.Contains("/static-data/storytheater-main/", StringComparison.OrdinalIgnoreCase))
                continue;

            facts.Add(new AuthorityFact(subjectId, "story_chapter", table.ContainerEntry,
                AuthoritySource.StaticTableForeignKey, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = table.ContainerEntry,
                Display = table.Name,
                Detail = $"{table.DataClass} · {table.SizeBytes} B",
                MediaKind = "static",
                DeepLink = RelationDeepLink.ForStatic(table.ContainerEntry, null),
                SourceDetail = $"静态表 subchapter-detail / storytheater-main（章节导航）",
            });
        }
    }

    /// <summary>
    /// 事件与选择（story-dungeon-* / abnormality-event）：静态表里的事件定义，
    /// 来源同样是容器路径 + <c>data_class</c>。
    /// </summary>
    private static void ExtractStoryEvents(string subjectId, AuthorityExtractionContext context, List<AuthorityFact> facts)
    {
        foreach (var table in context.StaticTables)
        {
            if (string.IsNullOrEmpty(table.ContainerEntry)) continue;
            if (!table.ContainerEntry.Contains("/static-data/story-dungeon-", StringComparison.OrdinalIgnoreCase) &&
                !table.ContainerEntry.Contains("/static-data/abnormality-event/", StringComparison.OrdinalIgnoreCase))
                continue;

            facts.Add(new AuthorityFact(subjectId, "story_event", table.ContainerEntry,
                AuthoritySource.StaticTableForeignKey, ConfidenceLevel.Authoritative, WritableSourceKind.Path)
            {
                WritableSourcePath = table.ContainerEntry,
                Display = table.Name,
                Detail = $"{table.DataClass} · {table.SizeBytes} B",
                MediaKind = "static",
                DeepLink = RelationDeepLink.ForStatic(table.ContainerEntry, null),
                SourceDetail = $"静态表 story-dungeon-* / abnormality-event（事件与选择）",
            });
        }
    }
}
