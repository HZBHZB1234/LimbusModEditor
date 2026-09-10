using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LimbusModEditor.Application.Texts;

/// <summary>文本浏览列中的一个 lang JSON 文件条目。</summary>
/// <param name="RelativePath">相对 lang 根的路径，恒用 '/' 分隔（与补丁文档键一致）。</param>
/// <param name="FullPath">磁盘完整路径。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
/// <param name="KeyCount">JSON 顶层键数；根不是 JSON 对象或解析失败时为 0。</param>
/// <param name="IsUtf8">内容是否为合法 UTF-8；非 UTF-8 明确标记，不做编码猜测。</param>
/// <param name="MTimeUtcTicks">文件最后写入时间（UTC ticks，plan-10 缓存新鲜度判定用，读元数据即可得）。</param>
public sealed record LangTextFileInfo(
    string RelativePath,
    string FullPath,
    long SizeBytes,
    int KeyCount,
    bool IsUtf8,
    long MTimeUtcTicks = 0);

/// <summary>搜索命中类别。</summary>
public enum LangTextSearchKind
{
    /// <summary>相对路径（含文件名）包含关键字。</summary>
    FileName,
    /// <summary>展平后的键路径（如 <c>dataList/0/dialog</c>）包含关键字。</summary>
    Key,
    /// <summary>叶子节点的值文本包含关键字。</summary>
    Value,
}

/// <summary>一条搜索命中。</summary>
/// <param name="RelativePath">命中的文件（相对 lang 根，'/' 分隔）。</param>
/// <param name="Kind">命中类别。</param>
/// <param name="KeyPath">键/值命中时的展平键路径；文件名命中为 null。</param>
/// <param name="Snippet">命中片段（值命中时截取匹配附近窗口）。</param>
public sealed record LangTextSearchHit(string RelativePath, LangTextSearchKind Kind, string? KeyPath, string? Snippet);

/// <summary>导出补丁时单个被编辑文件的结果。</summary>
public sealed record LangTextExportFileStatus(string RelativePath, int OperationCount, string? Note = null);

/// <summary>「导出 lang 补丁」的结果报告。</summary>
public sealed record LangTextExportReport(string OutputPath, int EditedFileCount, int PatchedFileCount, IReadOnlyList<LangTextExportFileStatus> Files);

/// <summary>
/// plan-10：一个 lang 文件的「新鲜度观测值」= 文件系统上的 <c>(size, mtimeUtcTicks)</c>。
/// 只读元数据、不读内容，因此「签名是否命中」的判定本身几乎不花钱。
/// 由 <see cref="LangTextWorkbenchService.EnumerateFiles"/> 在枚举时交给调用方落盘，
/// 下次枚举拿它和缓存条目比对：命中就复用缓存条目（不读盘、不解析），不命中才重新解析。
/// </summary>
/// <param name="Size">文件字节数。</param>
/// <param name="MTimeUtcTicks">最后写入时间（UTC ticks）。</param>
public readonly record struct CacheObservation(long Size, long MTimeUtcTicks)
{
    /// <summary>缓存条目（<see cref="LangTextFileInfo"/>）是否就是这份观测值描述的那个版本。</summary>
    public static bool Matches(LangTextFileInfo cached, CacheObservation observation)
        => cached.SizeBytes == observation.Size && cached.MTimeUtcTicks == observation.MTimeUtcTicks;
}

/// <summary>
/// plan-07 文本工作台后端服务：枚举活动语言 JSON 文件、键值/文件名搜索、
/// 编辑集（vanilla 快照 + 内存修改 + 还原）、导出与 LCTA changes.py 语义一致的
/// RFC6902 lang 补丁文档。全部方法 UI 无关；UTF-8（无 BOM）读写，检测到
/// 非 UTF-8 内容明确报错不猜编码。本服务绝不写游戏 lang 目录——唯一的写盘动作
/// 是把补丁文档写到调用方指定的输出路径。
/// </summary>
public sealed class LangTextWorkbenchService
{
    /// <summary>默认的单文件命中上限（<see cref="Search"/> 的参数默认值；
    /// plan-10 的索引缓存按它决定「哪些候选在排序上轮得到被检查」，因此必须与它同值）。</summary>
    public const int DefaultMaxHitsPerFile = 20;

    /// <summary>默认的命中总数上限（<see cref="Search"/> 的参数默认值）。</summary>
    public const int DefaultMaxTotalHits = 500;

    private readonly LangTextPatchService _patch = new();
    private readonly TextDiffService _diff = new();

    /// <summary>编辑集：相对路径（'/' 分隔）→ vanilla 快照与当前修改文本（会话内存）。</summary>
    private readonly Dictionary<string, EditEntry> _edits = new(StringComparer.OrdinalIgnoreCase);

    private string? _langRoot;

    private sealed record EditEntry(string VanillaText, string ModifiedText);

    /// <summary>枚举后记录的当前 lang 根（<see cref="BeginEdit"/> 等编辑集方法依赖它）。</summary>
    public string? CurrentLangRoot => _langRoot;

    /// <summary>编辑集中的相对路径（'/' 分隔，字典序）。</summary>
    public IReadOnlyList<string> EditedFiles => _edits.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ── 定位与活动语言 ────────────────────────────────────────────────

    /// <summary>由游戏目录解析 lang 根：<c>&lt;游戏目录&gt;/LimbusCompany_Data/lang</c>；
    /// 目录不存在或入参为空时返回 null。</summary>
    public string? ResolveLangRoot(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        var langRoot = Path.Combine(gameDirectory.Trim(), "LimbusCompany_Data", "lang");
        return Directory.Exists(langRoot) ? Path.GetFullPath(langRoot) : null;
    }

    /// <summary>读取 lang 根 config.json 指向的活动语言名。直接转调
    /// <see cref="LangTextPatchService.ReadActiveLanguage"/>，保持单一实现。</summary>
    public string? ReadActiveLanguage(string langRoot) => _patch.ReadActiveLanguage(langRoot);

    // ── 枚举与索引 ────────────────────────────────────────────────────

    /// <summary>枚举工作台数据源：根级 <c>config.json</c> + config.json 指向的活动语言
    /// 目录下全部 *.json（子目录逐层展开，如 StoryData）。其余翻译组目录不索引。
    /// 相对路径以 '/' 分隔，先 config.json 后字典序。同时记录大小、顶层键数
    /// （JSON 对象才计）与 UTF-8 合法性。调用成功后本服务进入「已定位」状态，
    /// 编辑集方法以该 lang 根为基线。非 UTF-8 文件：IsUtf8=false、键数 0、明确标记不猜。
    /// 单个文件读取失败不中断枚举（按不可读处理）。
    ///
    /// <para><paramref name="cached"/> 与 <paramref name="refreshed"/> 是 plan-10 的
    /// <b>加速旁路</b>（可选，缺省即旧行为）：目录结构仍然真实枚举（保证增删文件不会漏），
    /// 但<b>内容</b>只对签名（大小 + mtime）变过的文件重新读取解析，签名命中的文件
    /// 直接复用缓存条目。<b>两个路径的返回值逐字段相同</b>——删掉缓存库后功能完全不受影响，
    /// 只是变慢（单测 <c>TextIndexStoreTests</c> 以「有缓存 vs 删库」逐字段比对钉死）。
    /// <paramref name="refreshed"/> 会把本次真实观测到的签名交给调用方落盘。</para></summary>
    public IReadOnlyList<LangTextFileInfo> EnumerateFiles(
        string langRoot,
        IReadOnlyDictionary<string, LangTextFileInfo>? cached = null,
        Action<LangTextFileInfo, CacheObservation>? refreshed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        if (!Directory.Exists(langRoot)) throw new DirectoryNotFoundException($"lang 目录不存在: {langRoot}");
        _langRoot = Path.GetFullPath(langRoot);

        var files = new List<LangTextFileInfo>();
        var config = Path.Combine(langRoot, "config.json");
        if (File.Exists(config)) files.Add(BuildFileInfo(_langRoot, config, cached, refreshed));

        var active = ReadActiveLanguage(langRoot);
        if (!string.IsNullOrWhiteSpace(active))
        {
            var activeDir = Path.Combine(langRoot, active);
            if (Directory.Exists(activeDir))
            {
                foreach (var path in Directory.EnumerateFiles(activeDir, "*.json", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    files.Add(BuildFileInfo(_langRoot, path, cached, refreshed));
                }
            }
        }
        return files;
    }

    private static LangTextFileInfo BuildFileInfo(
        string langRoot,
        string fullPath,
        IReadOnlyDictionary<string, LangTextFileInfo>? cached,
        Action<LangTextFileInfo, CacheObservation>? refreshed)
    {
        var relative = Path.GetRelativePath(langRoot, fullPath).Replace('\\', '/');
        if (TryObserve(fullPath, out var observation) && cached is not null &&
            cached.TryGetValue(relative, out var hit) &&
            CacheObservation.Matches(hit, observation))
        {
            refreshed?.Invoke(hit, observation);
            return hit; // 签名命中：不读盘、不解析（这才是「二次进页面不再全目录重读」的来源）
        }

        var info = ReadFileInfo(fullPath, relative, observation);
        refreshed?.Invoke(info, observation);
        return info;
    }

    /// <summary>只看文件系统的 (size, mtime)——不读内容，用于「签名是否变过」判定。</summary>
    private static bool TryObserve(string fullPath, out CacheObservation observation)
    {
        try
        {
            var info = new FileInfo(fullPath);
            observation = new CacheObservation(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            observation = default;
            return false;
        }
    }

    private static LangTextFileInfo BuildFileInfo(string langRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(langRoot, fullPath).Replace('\\', '/');
        var observed = TryObserve(fullPath, out var observation) ? observation : default;
        return ReadFileInfo(fullPath, relative, observed);
    }

    private static LangTextFileInfo ReadFileInfo(string fullPath, string relative, CacheObservation observation)
    {
        long size = 0;
        var isUtf8 = false;
        var keyCount = 0;
        try
        {
            size = new FileInfo(fullPath).Length;
            var text = ReadTextStrict(fullPath); // 非 UTF-8 在此抛 InvalidDataException
            isUtf8 = true;
            keyCount = CountTopLevelKeys(text);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // 非 UTF-8 明确标记不猜；不可读文件保持占位条目，不中断整个枚举。
        }
        return new LangTextFileInfo(relative, Path.GetFullPath(fullPath), size, keyCount, isUtf8, observation.MTimeUtcTicks);
    }

    // ── 搜索 ─────────────────────────────────────────────────────────

    /// <summary>文件名 / 键 / 值 包含匹配（忽略大小写）。文件名命中无需读内容；
    /// 键与值命中逐文件读取并解析（键路径展平为 <c>a/b/0/c</c> 形式，值取叶子文本）。
    /// 逐文件容错：非 UTF-8、不可读或非法 JSON 的文件不参与键值搜索，绝不让单个
    /// 坏文件中断搜索。返回数量受 <paramref name="maxTotalHits"/> 与
    /// <paramref name="maxHitsPerFile"/> 上限保护。
    ///
    /// <para><paramref name="cached"/> 是 plan-10 的加速旁路（可选，缺省即旧行为）：
    /// 键值事实已由缓存建索引时算好，这里只做「匹配 + 按文件切片」，
    /// <b>不再逐文件读盘 + JSON 解析</b>。切片与匹配口径都在本方法里，
    /// 与逐文件路径共用同一套 <see cref="LangTextSearchKind"/> 顺序规则，
    /// 因此两条路径的返回值逐条相同（单测钉死）。</para></summary>
    public IReadOnlyList<LangTextSearchHit> Search(
        string query,
        IReadOnlyList<LangTextFileInfo> files,
        int maxTotalHits = DefaultMaxTotalHits,
        int maxHitsPerFile = DefaultMaxHitsPerFile,
        IReadOnlyDictionary<string, IReadOnlyList<LangTextSearchHit>>? cached = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();
        var hits = new List<LangTextSearchHit>();
        foreach (var file in files)
        {
            if (hits.Count >= maxTotalHits) break;
            var perFile = 0;

            if (file.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new LangTextSearchHit(file.RelativePath, LangTextSearchKind.FileName, null, file.RelativePath));
                perFile++;
            }
            if (perFile >= maxHitsPerFile || hits.Count >= maxTotalHits) continue;

            IReadOnlyList<LangTextSearchHit> candidates;
            if (cached is not null)
            {
                candidates = cached.TryGetValue(file.RelativePath, out var indexed) ? indexed : [];
            }
            else
            {
                candidates = ReadFileHits(file.FullPath, file.RelativePath);
            }

            foreach (var candidate in candidates)
            {
                if (perFile >= maxHitsPerFile || hits.Count >= maxTotalHits) break;
                if (!TryMatch(candidate, query, file.RelativePath, out var matched)) continue;
                hits.Add(matched);
                perFile++;
            }
        }
        return hits;
    }

    /// <summary>单条命中是否匹配查询（与逐文件路径逐字一致：键命中先看键路径、命中即不再看值；
    /// 值命中在<b>完整值文本</b>上找匹配位置，片段按同一窗口规则截取）。</summary>
    private static bool TryMatch(LangTextSearchHit hit, string query, string relativePath, out LangTextSearchHit matched)
    {
        if (hit.Kind == LangTextSearchKind.Key)
        {
            if (hit.KeyPath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                matched = hit;
                return true;
            }
        }
        else if (hit.Snippet is { } valueText)
        {
            var index = valueText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                matched = new LangTextSearchHit(relativePath, LangTextSearchKind.Value, hit.KeyPath,
                    Snippet(valueText, index, query.Length));
                return true;
            }
        }
        matched = hit;
        return false;
    }

    /// <summary>
    /// 读一个文件并算出它的<b>全部可达命中候选</b>（键值事实；单文件容错：非 UTF-8 /
    /// 非法 JSON / 不可读一律返回空集合，绝不让坏文件中断搜索）。
    /// <b>缓存建索引时也走这里</b>，保证「索引里的事实」与「现读的事实」是同一份实现、同一个顺序。
    ///
    /// <para><b>「可达」的准确含义</b>（这是缓存与现读逐条一致的关键）：候选按
    /// 键、值、键、值… 交错排列——与逐文件搜索遍历叶子节点的顺序完全一致。逐文件搜索在
    /// <c>perFile</c> 达到上限（默认 <see cref="DefaultMaxHitsPerFile"/>）时就跳出该文件的叶子循环，
    /// 因此第 N 个键候选之前的<b>值</b>候选在排序上永远轮不到被检查，可以安全丢弃。
    /// 键候选则必须全部保留（键命中会让 <c>perFile</c> 涨到上限之上，这正是两条路径
    /// 唯一容易出错的地方）。</para></summary>
    public static IReadOnlyList<LangTextSearchHit> ReadFileHits(
        string fullPath, string relativePath, int perFileLimit = DefaultMaxHitsPerFile)
    {
        var hits = new List<LangTextSearchHit>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(ReadTextStrict(fullPath));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return hits; // 非 UTF-8 / 非法 JSON / 不可读：明确跳过，不猜不崩
        }
        if (root is null) return hits;

        var keysSoFar = 0;
        foreach (var (keyPath, leaf) in EnumerateLeaves(root, string.Empty))
        {
            hits.Add(KeyHitCandidate(relativePath, keyPath));
            keysSoFar++;
            // 值候选：Snippet 槽位放「完整值文本」（查询串到 Search 里才比对，
            // 命中时按同一窗口规则重新截取片段，两条路径的产物逐字段相同）；
            // 只有在排序上轮得到被检查的才落盘（见方法注释）。
            if (keysSoFar < perFileLimit)
                hits.Add(new LangTextSearchHit(relativePath, LangTextSearchKind.Value, keyPath, LeafToText(leaf)));
        }
        return hits;
    }

    /// <summary>键命中候选行（缓存建索引用；匹配与否由 <see cref="Search"/> 判定）。</summary>
    public static LangTextSearchHit KeyHitCandidate(string relativePath, string keyPath)
        => new(relativePath, LangTextSearchKind.Key, keyPath, keyPath);

    /// <summary>文件名命中候选行。</summary>
    public static LangTextSearchHit FileNameHitCandidate(string relativePath)
        => new(relativePath, LangTextSearchKind.FileName, null, relativePath);

    private static IEnumerable<(string Path, JsonNode Node)> EnumerateLeaves(JsonNode node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0) { yield return (prefix, node); break; }
                foreach (var (key, value) in obj)
                {
                    if (value is null) continue;
                    var childPath = prefix.Length == 0 ? key : $"{prefix}/{key}";
                    foreach (var hit in EnumerateLeaves(value, childPath)) yield return hit;
                }
                break;
            case JsonArray array:
                if (array.Count == 0) { yield return (prefix, node); break; }
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is null) continue;
                    foreach (var hit in EnumerateLeaves(array[i]!, $"{prefix}/{i}")) yield return hit;
                }
                break;
            default:
                yield return (prefix, node);
                break;
        }
    }

    private static string LeafToText(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();

    private static string Snippet(string text, int matchIndex, int matchLength)
    {
        const int window = 90;
        var start = Math.Max(0, matchIndex - 24);
        var end = Math.Min(text.Length, Math.Max(matchIndex + matchLength, start + window));
        var slice = text[start..end].ReplaceLineEndings(" ");
        return (start > 0 ? "…" : string.Empty) + slice + (end < text.Length ? "…" : string.Empty);
    }

    // ── 编辑集 ───────────────────────────────────────────────────────

    /// <summary>开始编辑一个文件：读取 lang 目录原文存为 vanilla 快照并进入编辑集，
    /// 返回当前应展示/编辑的文本（首次为原文；该文件已在编辑集中时返回当前修改文本，
    /// 不重置快照）。原文非法 JSON 或非 UTF-8 时 fail fast。</summary>
    public string BeginEdit(string relativePath)
    {
        var langRoot = RequireLangRoot();
        relativePath = NormalizeRelativePath(relativePath);
        if (_edits.TryGetValue(relativePath, out var existing)) return existing.ModifiedText;

        var fullPath = Path.Combine(langRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"lang 文件不存在: {relativePath}", fullPath);
        var vanilla = ReadTextStrict(fullPath);
        try { _ = JsonNode.Parse(vanilla); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"原文不是合法 JSON，拒绝进入编辑集: {relativePath}: {ex.Message}", ex);
        }
        _edits[relativePath] = new EditEntry(vanilla, vanilla);
        return vanilla;
    }

    /// <summary>写入某文件的新 JSON 文本到编辑集。新文本必须是合法 JSON，
    /// 校验失败抛 <see cref="InvalidDataException"/> 且编辑集保持原状态；
    /// 尚未 <see cref="BeginEdit"/> 的文件抛 <see cref="InvalidOperationException"/>。</summary>
    public void SetModified(string relativePath, string newJsonText)
    {
        relativePath = NormalizeRelativePath(relativePath);
        try { _ = JsonNode.Parse(newJsonText); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"新内容不是合法 JSON，已拒绝写入编辑集（{relativePath}）: {ex.Message}", ex);
        }
        if (!_edits.TryGetValue(relativePath, out var entry))
            throw new InvalidOperationException($"请先对 {relativePath} 调用 BeginEdit 建立编辑集条目。");
        _edits[relativePath] = entry with { ModifiedText = newJsonText };
    }

    /// <summary>还原单文件：移出编辑集（内存编辑，lang 目录从未被改动，无需写盘）。
    /// 返回该文件此前是否在编辑集中。</summary>
    public bool Revert(string relativePath) => _edits.Remove(NormalizeRelativePath(relativePath));

    /// <summary>清空编辑集（页面「清理入口」用）。</summary>
    public void ClearEdits() => _edits.Clear();

    /// <summary>该文件是否在编辑集中。</summary>
    public bool IsModified(string relativePath) => _edits.ContainsKey(NormalizeRelativePath(relativePath));

    /// <summary>当前修改文本；不在编辑集中时返回 null。</summary>
    public string? TryGetModifiedText(string relativePath) =>
        _edits.TryGetValue(NormalizeRelativePath(relativePath), out var entry) ? entry.ModifiedText : null;

    /// <summary>vanilla 快照原文；不在编辑集中时返回 null（预览 diff 用）。</summary>
    public string? TryGetVanillaText(string relativePath) =>
        _edits.TryGetValue(NormalizeRelativePath(relativePath), out var entry) ? entry.VanillaText : null;

    // ── 导出补丁 ─────────────────────────────────────────────────────

    /// <summary>「导出 lang 补丁」：对每个编辑集条目用 <see cref="TextDiffService"/> 对
    /// vanilla 快照 → 修改文本生成 RFC6902 ops，组装 <see cref="LangPatchDocument"/>
    /// （键 = 相对 lang 根路径，'/' 分隔）并经 <see cref="LangTextPatchService.Write"/>
    /// 写到 <paramref name="outputPath"/>——与 LCTA changes.py 的 patchs 语义一致。
    /// 无差异的文件不进入补丁文档（状态中注明）。只写指定输出路径，绝不触碰游戏目录。
    /// 返回报告；编辑集为空时写出仅含空 patchs 的文档。</summary>
    public LangTextExportReport ExportPatch(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var document = new LangPatchDocument();
        var statuses = new List<LangTextExportFileStatus>();
        foreach (var (relativePath, entry) in _edits.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var before = JsonNode.Parse(entry.VanillaText);
            var after = JsonNode.Parse(entry.ModifiedText);
            var ops = _diff.Generate(before, after);
            if (ops.Count > 0)
            {
                document.Patches[relativePath] = ops;
                statuses.Add(new LangTextExportFileStatus(relativePath, ops.Count));
            }
            else
            {
                statuses.Add(new LangTextExportFileStatus(relativePath, 0, "编辑后无差异，未进入补丁"));
            }
        }

        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _patch.Write(document, fullOutput);

        var patched = statuses.Count(x => x.OperationCount > 0);
        return new LangTextExportReport(fullOutput, _edits.Count, patched, statuses);
    }

    /// <summary><see cref="ExportPatch"/> 的异步签名（供 UI 后台 await；当前差分在
    /// 调用线程同步完成，JSON 量级小，UI 侧应自行放到后台线程调用）。</summary>
    public Task<LangTextExportReport> ExportPatchAsync(string outputPath) => Task.FromResult(ExportPatch(outputPath));

    // ── 内部工具 ─────────────────────────────────────────────────────

    private string RequireLangRoot() =>
        _langRoot ?? throw new InvalidOperationException("请先调用 EnumerateFiles 定位 lang 根，再使用编辑集。");

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(x => x is ".." or ""))
            throw new InvalidDataException($"路径非法（必须是 lang 根内的相对路径）: {relativePath}");
        return normalized;
    }

    /// <summary>严格 UTF-8 解码（非法字节序列抛 InvalidDataException，不做编码猜测），
    /// 去除可能存在的 BOM 字符；写路径一律 UTF-8 无 BOM（由各写出点保证）。</summary>
    private static string ReadTextStrict(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} 不是合法的 UTF-8（不做编码猜测）: {ex.Message}", ex);
        }
    }

    // ── plan-10：缓存签名用的「活动语言事实」（不写文件，只读 + 算哈希）──────

    /// <summary>
    /// 配置哈希：由 <c>config.json</c> 的<b>内容</b>算出（SHA-256，十六进制小写）。
    /// 文件不存在或不可读时为 <c>"none"</c>。
    ///
    /// <para><b>为什么必须有它</b>：活动语言目录是由 <c>config.json</c> 的 <c>lang</c>
    /// 字段决定的。只比目录 mtime / 只比签名里的大小与时间，会漏掉「玩家切换了活动语言」
    /// 这种变化（config.json 常是几十字节的小文件，改写后大小可能不变）。
    /// 把内容哈希并进 <c>index_meta.signature</c> 后，切换语言必然使整库失效重建。</para>
    /// </summary>
    public static string ComputeConfigContentHash(string langRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        try
        {
            var path = Path.Combine(langRoot, "config.json");
            if (!File.Exists(path)) return "none";
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    /// <summary>活动语言目录的规范化路径；<c>config.json</c> 未指定或目录不存在时返回 null。</summary>
    public static string? ResolveActiveLanguageDirectory(string langRoot, string? activeLanguage)
        => string.IsNullOrWhiteSpace(activeLanguage)
            ? null
            : Path.Combine(langRoot, activeLanguage);

    /// <summary>顶层键数：根是 JSON 对象才计；数组/标量/解析失败为 0。</summary>
    private static int CountTopLevelKeys(string jsonText)
    {
        try
        {
            return JsonNode.Parse(jsonText) is JsonObject obj ? obj.Count : 0;
        }
        catch (JsonException)
        {
            return 0; // 非 JSON 内容：列出但不计键数
        }
    }
}
