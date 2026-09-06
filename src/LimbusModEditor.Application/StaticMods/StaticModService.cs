using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>manifest.json 中一条补丁声明（LCTA launcher/staticmod.py 布局）。</summary>
public sealed record StaticModPatchEntry(string DataClass, string File, string OpType, string Source, string? Container);

/// <summary>manifest.json 中一条整文件替换声明（fullFiles[]）。</summary>
public sealed record StaticModFullFileEntry(string DataClass, string File, string Source, string? Container);

/// <summary>一个 .staticmod 包（zip 容器）：manifest + 各补丁负载 + 未知文件。</summary>
public sealed class StaticModPackage
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public List<StaticModPatchEntry> Patches { get; } = [];
    public List<StaticModFullFileEntry> FullFiles { get; } = [];
    /// <summary>补丁负载：键 = manifest 里的 source 相对路径。</summary>
    public Dictionary<string, JsonNode> Payloads { get; } = new(StringComparer.Ordinal);
    public List<(string Path, byte[] Data)> UnknownFiles { get; } = [];
}

/// <summary>
/// .staticmod 静态数据模组的读写与补丁应用，布局与语义对照真实加载器
/// （LCTA launcher/staticmod.py）：zip 容器，manifest.json 必须
/// format=="staticmod/v1"；patches 条目 {dataClass,file,opType,source,container}；
/// opType=jsonpatch（RFC6902 操作数组）或 pathset（{"a.b[0].c": 值} 路径-值覆盖，
/// 路径语法 `([A-Za-z_][A-Za-z0-9_]*)(\[(\d+)\])?`，逐段转成 RFC6901 指针后
/// 全部以 op=add 应用，与加载器 _pathset_to_jsonpatch 一致）；fullFiles[] 为
/// {dataClass,file,source,container} 整文件替换（diff 由加载器首次加载时完成）。
/// bundle 打补丁与 catalog 双写属于加载器运行时行为（带风险开关），本服务只做
/// 包的读写/生成/预览，绝不直接改写游戏缓存或 catalog。
/// </summary>
public sealed class StaticModService
{
    private const string FormatId = "staticmod/v1";
    private readonly TextDiffService _diff = new();

    /// <summary>探测 zip 是否为 .staticmod（含 format==staticmod/v1 的 manifest）。</summary>
    public static bool LooksLikeStaticMod(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("manifest.json");
            if (entry is null) return false;
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("format", out var format) &&
                   format.ValueEquals(FormatId);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>读取 .staticmod 包；manifest 缺失/格式不符/补丁负载缺失一律 fail fast。</summary>
    public StaticModPackage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("静态数据模组不存在。", path);
        using var archive = ZipFile.OpenRead(path);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException($"静态数据模组缺少 manifest.json: {path}");
        JsonObject manifest;
        using (var stream = manifestEntry.Open())
        {
            try { manifest = JsonNode.Parse(stream) as JsonObject
                    ?? throw new InvalidDataException("manifest.json 顶层必须是 JSON 对象。"); }
            catch (JsonException ex) { throw new InvalidDataException($"无法解析 manifest.json: {ex.Message}"); }
        }
        if (manifest["format"]?.GetValue<string>() is not { } format || format != FormatId)
            throw new InvalidDataException($"manifest format 不是 {FormatId}（实际 {manifest["format"]?.GetValue<string>() ?? "缺失"}），拒绝解析。");

        var package = new StaticModPackage
        {
            Name = manifest["name"]?.GetValue<string>() ?? string.Empty,
            Version = manifest["version"]?.GetValue<string>() ?? "1.0.0",
            Description = manifest["description"]?.GetValue<string>() ?? string.Empty,
        };
        var payloadNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in manifest["patches"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry) throw new InvalidDataException("patches[] 存在非对象条目。");
            var source = entry["source"]?.GetValue<string>()
                ?? throw new InvalidDataException("patches[] 条目缺少 source。");
            EnsureSafeEntryPath(source);
            package.Patches.Add(new(
                entry["dataClass"]?.GetValue<string>() ?? throw new InvalidDataException("patches[] 条目缺少 dataClass。"),
                entry["file"]?.GetValue<string>() ?? throw new InvalidDataException("patches[] 条目缺少 file。"),
                entry["opType"]?.GetValue<string>() ?? throw new InvalidDataException("patches[] 条目缺少 opType。"),
                source,
                entry["container"]?.GetValue<string>()));
            payloadNames.Add(source);
        }
        foreach (var node in manifest["fullFiles"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry) throw new InvalidDataException("fullFiles[] 存在非对象条目。");
            var source = entry["source"]?.GetValue<string>()
                ?? throw new InvalidDataException("fullFiles[] 条目缺少 source。");
            EnsureSafeEntryPath(source);
            package.FullFiles.Add(new(
                entry["dataClass"]?.GetValue<string>() ?? throw new InvalidDataException("fullFiles[] 条目缺少 dataClass。"),
                entry["file"]?.GetValue<string>() ?? throw new InvalidDataException("fullFiles[] 条目缺少 file。"),
                source,
                entry["container"]?.GetValue<string>()));
            payloadNames.Add(source);
        }
        foreach (var payloadName in payloadNames)
        {
            var entry = archive.GetEntry(payloadName.Replace('\\', '/'))
                ?? throw new InvalidDataException($"manifest 声明的补丁文件缺失: {payloadName}");
            using var stream = entry.Open();
            try { package.Payloads[payloadName] = JsonNode.Parse(stream)
                    ?? throw new InvalidDataException($"补丁文件为空: {payloadName}"); }
            catch (JsonException ex) { throw new InvalidDataException($"无法解析补丁文件 {payloadName}: {ex.Message}"); }
        }
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName is "manifest.json" || payloadNames.Contains(entry.FullName) || string.IsNullOrEmpty(entry.Name)) continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            package.UnknownFiles.Add((entry.FullName, buffer.ToArray()));
        }
        return package;
    }

    /// <summary>写成 LCTA 兼容的 .staticmod zip（manifest + 负载 + 未知文件）。</summary>
    public void Write(StaticModPackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var manifest = new JsonObject
        {
            ["format"] = FormatId,
            ["name"] = package.Name,
            ["version"] = package.Version,
            ["description"] = package.Description,
            ["patches"] = new JsonArray(package.Patches.Select(p => (JsonNode)new JsonObject
            {
                ["dataClass"] = p.DataClass,
                ["file"] = p.File,
                ["opType"] = p.OpType,
                ["source"] = p.Source,
                ["container"] = p.Container
            }).ToArray()),
            ["fullFiles"] = new JsonArray(package.FullFiles.Select(f => (JsonNode)new JsonObject
            {
                ["dataClass"] = f.DataClass,
                ["file"] = f.File,
                ["source"] = f.Source,
                ["container"] = f.Container
            }).ToArray()),
        };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("manifest.json");
        using (var stream = manifestEntry.Open())
        using (var writer = new StreamWriter(stream))
            writer.Write(manifest.ToJsonString(jsonOptions));
        foreach (var (source, payload) in package.Payloads)
        {
            EnsureSafeEntryPath(source);
            var entry = archive.CreateEntry(source.Replace('\\', '/'));
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(payload.ToJsonString(jsonOptions));
        }
        foreach (var (entryPath, data) in package.UnknownFiles)
        {
            EnsureSafeEntryPath(entryPath);
            var entry = archive.CreateEntry(entryPath);
            using var stream = entry.Open();
            stream.Write(data);
        }
    }

    /// <summary>从「官方 JSON vs 修改后 JSON」生成 jsonpatch 条目（编辑器创作
    /// 通道；diff 语义与加载器首次加载 fullFiles 时的 from_diff 一致）。</summary>
    public StaticModPackage CreateJsonPatchPackage(string name, string version, string description,
        IReadOnlyList<(string DataClass, string File, string? Container, string OfficialJsonPath, string ModifiedJsonPath)> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (entries.Count == 0) throw new ArgumentException("至少需要一个补丁条目。", nameof(entries));
        var package = new StaticModPackage { Name = name, Version = version, Description = description };
        foreach (var (index, entry) in entries.Select((value, index) => (index, value)))
        {
            JsonNode official, modified;
            try
            {
                official = JsonNode.Parse(File.ReadAllText(entry.OfficialJsonPath))
                    ?? throw new InvalidDataException($"官方 JSON 为空: {entry.OfficialJsonPath}");
                modified = JsonNode.Parse(File.ReadAllText(entry.ModifiedJsonPath))
                    ?? throw new InvalidDataException($"修改 JSON 为空: {entry.ModifiedJsonPath}");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"无法解析条目 {entry.File} 的输入 JSON: {ex.Message}");
            }
            var ops = _diff.Generate(official, modified);
            if (ops.Count == 0) continue; // 无差异的条目不写入
            var source = $"patches/{entry.DataClass}_{index}.json";
            package.Patches.Add(new(entry.DataClass, entry.File, "jsonpatch", source, entry.Container));
            package.Payloads[source] = ops;
        }
        if (package.Patches.Count == 0)
            throw new InvalidDataException("所有条目都没有差异，未生成补丁。");
        return package;
    }

    /// <summary>把一条补丁应用到 JSON 文档（预览用），语义与加载器
    /// apply_patch_to_json 一致；返回新文档（就地修改入参并返回）。</summary>
    public JsonNode ApplyPatchToDocument(JsonNode document, JsonNode patchData, string opType)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(patchData);
        switch (opType)
        {
            case "jsonpatch":
            {
                var ops = patchData is JsonArray array ? array : new JsonArray(patchData.DeepClone());
                return _diff.Apply(document, ops);
            }
            case "pathset":
            {
                if (patchData is not JsonObject pathset)
                    throw new InvalidDataException("pathset 补丁必须是对象。");
                return _diff.Apply(document, PathsetToJsonPatch(pathset));
            }
            default:
                throw new InvalidDataException($"未知 opType: {opType}（支持 jsonpatch | pathset）");
        }
    }

    /// <summary>pathset → RFC6902 操作列表；与加载器 _pathset_to_jsonpatch 完全
    /// 一致：路径段 `key` 或 `key[下标]`，全部转成 op=add（对象键覆盖/新建，
    /// 数组下标插入——加载器即此语义）。</summary>
    public JsonArray PathsetToJsonPatch(JsonObject pathset)
    {
        var ops = new JsonArray();
        foreach (var (path, value) in pathset)
        {
            var pointer = BuildPointer(path ?? throw new InvalidDataException("pathset 存在空路径。"));
            ops.Add(new JsonObject { ["op"] = "add", ["path"] = pointer, ["value"] = value?.DeepClone() });
        }
        return ops;
    }

    private static string BuildPointer(string path)
    {
        var pointer = new StringBuilder();
        foreach (var part in path.Split('.'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(part, @"^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d+)\])?$");
            if (!match.Success)
                throw new InvalidDataException($"非法 pathset 路径段: {part}（路径 {path}）");
            pointer.Append('/').Append(match.Groups[1].Value);
            if (match.Groups[2].Success) pointer.Append('/').Append(match.Groups[2].Value);
        }
        return pointer.ToString();
    }

    /// <summary>拒绝绝对路径与越级引用（zip 内条目必须是相对路径）。</summary>
    private static void EnsureSafeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || Path.IsPathRooted(entryPath) ||
            entryPath.Split('/', '\\').Any(x => x is ".." or ""))
            throw new InvalidDataException($"模组内路径非法（必须是包内相对路径）: {entryPath}");
    }
}
