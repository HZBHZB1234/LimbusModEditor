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
public sealed record LangTextFileInfo(string RelativePath, string FullPath, long SizeBytes, int KeyCount, bool IsUtf8);

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
/// plan-07 文本工作台后端服务：枚举活动语言 JSON 文件、键值/文件名搜索、
/// 编辑集（vanilla 快照 + 内存修改 + 还原）、导出与 LCTA changes.py 语义一致的
/// RFC6902 lang 补丁文档。全部方法 UI 无关；UTF-8（无 BOM）读写，检测到
/// 非 UTF-8 内容明确报错不猜编码。本服务绝不写游戏 lang 目录——唯一的写盘动作
/// 是把补丁文档写到调用方指定的输出路径。
/// </summary>
public sealed class LangTextWorkbenchService
{
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
    /// 单个文件读取失败不中断枚举（按不可读处理）。</summary>
    public IReadOnlyList<LangTextFileInfo> EnumerateFiles(string langRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        if (!Directory.Exists(langRoot)) throw new DirectoryNotFoundException($"lang 目录不存在: {langRoot}");
        _langRoot = Path.GetFullPath(langRoot);

        var files = new List<LangTextFileInfo>();
        var config = Path.Combine(langRoot, "config.json");
        if (File.Exists(config)) files.Add(BuildFileInfo(_langRoot, config));

        var active = ReadActiveLanguage(langRoot);
        if (!string.IsNullOrWhiteSpace(active))
        {
            var activeDir = Path.Combine(langRoot, active);
            if (Directory.Exists(activeDir))
            {
                foreach (var path in Directory.EnumerateFiles(activeDir, "*.json", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    files.Add(BuildFileInfo(_langRoot, path));
                }
            }
        }
        return files;
    }

    private static LangTextFileInfo BuildFileInfo(string langRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(langRoot, fullPath).Replace('\\', '/');
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
        return new LangTextFileInfo(relative, Path.GetFullPath(fullPath), size, keyCount, isUtf8);
    }

    // ── 搜索 ─────────────────────────────────────────────────────────

    /// <summary>文件名 / 键 / 值 包含匹配（忽略大小写）。文件名命中无需读内容；
    /// 键与值命中逐文件读取并解析（键路径展平为 <c>a/b/0/c</c> 形式，值取叶子文本）。
    /// 逐文件容错：非 UTF-8、不可读或非法 JSON 的文件不参与键值搜索，绝不让单个
    /// 坏文件中断搜索。返回数量受 <paramref name="maxTotalHits"/> 与
    /// <paramref name="maxHitsPerFile"/> 上限保护。</summary>
    public IReadOnlyList<LangTextSearchHit> Search(
        string query,
        IReadOnlyList<LangTextFileInfo> files,
        int maxTotalHits = 500,
        int maxHitsPerFile = 20)
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

            string text;
            JsonNode? root;
            try
            {
                text = ReadTextStrict(file.FullPath);
                root = JsonNode.Parse(text);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
            {
                continue; // 非 UTF-8 / 非法 JSON / 不可读：明确跳过，不猜不崩
            }
            if (root is null) continue;

            foreach (var (keyPath, leaf) in EnumerateLeaves(root, string.Empty))
            {
                if (perFile >= maxHitsPerFile || hits.Count >= maxTotalHits) break;

                if (keyPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new LangTextSearchHit(file.RelativePath, LangTextSearchKind.Key, keyPath, keyPath));
                    perFile++;
                    continue;
                }

                var valueText = LeafToText(leaf);
                var index = valueText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    hits.Add(new LangTextSearchHit(file.RelativePath, LangTextSearchKind.Value, keyPath, Snippet(valueText, index, query.Length)));
                    perFile++;
                }
            }
        }
        return hits;
    }

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
