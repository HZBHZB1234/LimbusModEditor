using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LimbusModEditor.Application.Texts;

/// <summary>一个 lang 文本模组（RFC6902 通道）中的单文件应用结果。</summary>
public sealed record LangPatchEntryStatus(string RelativePath, bool Applied, int OperationCount, string? Note = null);

/// <summary>与真实加载器（LCTA launcher/changes.py）兼容的文本补丁文档：
/// <c>{"patchs": { "语言目录/文件.json": [RFC6902 操作…], … }}</c>。
/// 键是相对 lang 根目录的路径（正斜杠），值是作用在该文件内容上的补丁数组。</summary>
public sealed class LangPatchDocument
{
    public Dictionary<string, JsonArray> Patches { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// lang 文本模组通道：读取游戏 lang 目录的活动语言、在「原版目录 vs 修改目录」
/// 之间生成 RFC6902 差分补丁、把补丁应用回 lang 目录。布局与语义对照
/// LCTA launcher/changes.py（apply_patch / cleanup_patch）实测：补丁 JSON 放进
/// 模组目录（任意层，_disable 段禁用），加载器启动时对
/// &lt;游戏&gt;/LimbusCompany_Data/lang/&lt;键&gt; 逐文件 .bak 备份后应用，退出还原。
/// 本服务只负责补丁的生成/应用/读写，不触碰游戏目录的备份约定。
/// </summary>
public sealed class LangTextPatchService
{
    private readonly TextDiffService _diff = new();

    /// <summary>读取 lang 根目录 config.json 的活动语言名；没有 config.json 时返回 null。</summary>
    public string? ReadActiveLanguage(string langRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        var config = Path.Combine(langRoot, "config.json");
        if (!File.Exists(config)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(config))?["lang"]?.GetValue<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法解析 lang 配置文件 {config}: {ex.Message}");
        }
    }

    /// <summary>判断一段 JSON 是否是文本补丁文档（有 patchs 对象且值均为数组）。</summary>
    public static bool LooksLikeLangPatch(string jsonText)
    {
        try
        {
            if (JsonNode.Parse(jsonText) is not JsonObject root ||
                root["patchs"] is not JsonObject patches) return false;
            return patches.All(x => x.Value is JsonArray);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>从磁盘读取补丁文档；结构不符合约定即 fail fast。</summary>
    public LangPatchDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"无法解析文本补丁 {path}: {ex.Message}");
        }
        if (root is not JsonObject rootObject || rootObject["patchs"] is not JsonObject patches)
            throw new InvalidDataException($"文本补丁缺少 \"patchs\" 对象: {path}");
        var document = new LangPatchDocument();
        foreach (var (relativePath, value) in patches)
        {
            EnsureSafeRelativePath(relativePath);
            if (value is not JsonArray ops)
                throw new InvalidDataException($"补丁条目 {relativePath} 的值必须是 RFC6902 操作数组。");
            document.Patches[relativePath] = ops;
        }
        return document;
    }

    /// <summary>把补丁文档写成与 LCTA 兼容的 JSON 文件（键按字典序稳定输出）。</summary>
    public void Write(LangPatchDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var patches = new JsonObject();
        foreach (var (relativePath, ops) in document.Patches.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            EnsureSafeRelativePath(relativePath);
            patches[relativePath] = JsonNode.Parse(ops.ToJsonString())!.AsArray();
        }
        var root = new JsonObject { ["patchs"] = patches };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    /// <summary>对逐文件比较「原版目录 vs 修改目录」生成补丁。修改目录中多出的
    /// 文件无法用 RFC6902 承载（真实加载器只补丁已存在的文件），作为诊断返回，
    /// 绝不静默丢弃。</summary>
    public (LangPatchDocument Document, IReadOnlyList<string> Diagnostics) GenerateFromDirectories(
        string vanillaRoot, string modifiedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vanillaRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modifiedRoot);
        if (!Directory.Exists(vanillaRoot)) throw new DirectoryNotFoundException($"原版目录不存在: {vanillaRoot}");
        if (!Directory.Exists(modifiedRoot)) throw new DirectoryNotFoundException($"修改目录不存在: {modifiedRoot}");

        var document = new LangPatchDocument();
        var diagnostics = new List<string>();
        foreach (var modifiedFile in Directory.EnumerateFiles(modifiedRoot, "*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(modifiedRoot, modifiedFile).Replace('\\', '/');
            var vanillaFile = Path.Combine(vanillaRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(vanillaFile))
            {
                diagnostics.Add($"{relative}: 修改目录多出的文件（加载器只补丁已存在的 lang 文件，无法以补丁承载）");
                continue;
            }
            JsonNode? before, after;
            try
            {
                before = JsonNode.Parse(File.ReadAllText(vanillaFile));
                after = JsonNode.Parse(File.ReadAllText(modifiedFile));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"无法解析 {relative}: {ex.Message}");
            }
            var ops = _diff.Generate(before, after);
            if (ops.Count > 0) document.Patches[relative] = ops;
        }
        foreach (var vanillaFile in Directory.EnumerateFiles(vanillaRoot, "*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(vanillaRoot, vanillaFile).Replace('\\', '/');
            if (!File.Exists(Path.Combine(modifiedRoot, relative.Replace('/', Path.DirectorySeparatorChar))))
                diagnostics.Add($"{relative}: 原版目录存在但修改目录缺失（补丁通道无法表达删除，该文件将保持原样）");
        }
        return (document, diagnostics);
    }

    /// <summary>把补丁应用回 lang 目录（就地写回目标文件）。目标不存在的条目按
    /// 真实加载器行为跳过并报告，而不是失败整个应用。返回逐文件结果。</summary>
    public IReadOnlyList<LangPatchEntryStatus> ApplyToDirectory(string langRoot, LangPatchDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        ArgumentNullException.ThrowIfNull(document);
        if (!Directory.Exists(langRoot)) throw new DirectoryNotFoundException($"lang 目录不存在: {langRoot}");

        var results = new List<LangPatchEntryStatus>();
        foreach (var (relativePath, ops) in document.Patches)
        {
            EnsureSafeRelativePath(relativePath);
            var target = Path.Combine(langRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
            {
                results.Add(new(relativePath, false, ops.Count, "目标文件不存在（真实加载器同样会跳过该条目）"));
                continue;
            }
            JsonNode? content;
            try { content = JsonNode.Parse(File.ReadAllText(target)); }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"无法解析目标文件 {relativePath}: {ex.Message}");
            }
            var patched = _diff.Apply(content ?? new JsonObject(), ops);
            WriteAtomically(target, patched.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            results.Add(new(relativePath, true, ops.Count));
        }
        return results;
    }

    /// <summary>拒绝绝对路径与越级引用（补丁键必须是 lang 根内的相对路径）。</summary>
    private static void EnsureSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Any(x => x is ".." or ""))
            throw new InvalidDataException($"补丁条目路径非法（必须是 lang 根内的相对路径）: {relativePath}");
    }

    private static void WriteAtomically(string target, string content)
    {
        var temporary = target + ".lme-patch.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, target, overwrite: true);
    }
}
