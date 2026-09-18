using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.SpineData;

/// <summary>prefab 引用链里捞到的一套 Spine 素材（骨架 / 图集 / 纹理页）。</summary>
/// <param name="SkeletonPathId">骨架 TextAsset 的 path_id。</param>
/// <param name="SkeletonName">骨架文件名（TextAsset 的 <c>m_Name</c>）。</param>
/// <param name="SkeletonBytes">骨架正文（<see cref="Binary"/> 为真时是 <c>.skel</c> 二进制）。</param>
/// <param name="Binary">骨架是否为二进制格式（Spine 3.8+ 的 <c>.skel</c>）。</param>
/// <param name="AtlasPathId">图集 TextAsset 的 path_id。</param>
/// <param name="AtlasText">图集正文。</param>
/// <param name="TexturePathIds">按图集页序排好的 Texture2D path_id（<b>顺序即页序</b>）。</param>
public sealed record SpinePrefabChain(
    long SkeletonPathId,
    string SkeletonName,
    byte[] SkeletonBytes,
    bool Binary,
    long AtlasPathId,
    string AtlasText,
    IReadOnlyList<long> TexturePathIds);

/// <summary>
/// 从 prefab（<c>Assets/Resources_moved/Prefab/SpineIllustPrefab/*.prefab</c>）的<b>引用链</b>里
/// 把 Spine 三件套挖出来。
///
/// <para><b>为什么必须走引用链</b>：这套素材里的 <c>SkeletonGraphic</c> / <c>SkeletonDataAsset</c> /
/// 骨架 TextAsset / 图集 TextAsset / 纹理<b>都不在 bundle 的容器表（m_Container）里</b>，
/// 因此 <c>unity-cache-index.db</c> 只登记了那一个 prefab 的 GameObject —— 按名字、
/// 按同目录都找不到它们的行。唯一能到它们身边的路，是从 prefab 出发逐跳跟着 PPtr 走。</para>
///
/// <para><b>不猜内容、只认内容</b>：拿到疑似对象后，<b>按正文</b>判定它是不是骨架
/// （<c>"skeleton"</c> + <c>"bones"</c>）/ 是不是图集（首行页面名 + <c>size:</c> 之类指令），
/// 而不是按文件名或类型名（本机既有样本的教训：按名字搜 atlas 命中 65 个，64 个是 Unity
/// 的 <c>spriteatlasv2</c>，全是假命中）。</para>
///
/// <para>纹理页的<b>顺序</b>来自 Spine-Unity 的既有约定：图集资源的 <c>materials</c> 数组与
/// 图集文本的页面一一对应，因此按 <c>materials.Array[i]</c> 取到的第 i 张纹理就是第 i 页。</para>
/// </summary>
internal sealed class SpinePrefabChainResolver
{
    /// <summary>一次遍历最多看多少个对象（防病态图；真实 prefab 实测 ≤ 200 个可达对象）。</summary>
    private const int MaxNodes = 400;

    /// <summary>遍历的最大跳数（prefab → Transform → SkeletonGraphic → SkeletonData → Atlas → Material → Texture 是 6 跳）。</summary>
    private const int MaxDepth = 8;

    private readonly AssetsToolsBackend.BundleObjectReader _reader;
    private readonly string _serializedFileName;

    public SpinePrefabChainResolver(AssetsToolsBackend.BundleObjectReader reader, string serializedFileName)
    {
        _reader = reader;
        _serializedFileName = serializedFileName;
    }

    /// <summary>
    /// 从 prefab 的根 GameObject 出发找三件套。找不到返回 null（调用方负责给中文原因，
    /// 本方法不抛业务异常；只有 IO / 格式层面的问题才往外抛）。
    /// </summary>
    public SpinePrefabChain? Resolve(long rootPathId)
    {
        // ── 第 1 步：广度优先走一遍引用图，顺手把可疑对象记下来 ─────────
        var queue = new Queue<(long PathId, int Depth)>();
        var visited = new HashSet<long> { rootPathId };
        queue.Enqueue((rootPathId, 0));

        // pathId → 该对象的引用列表（后面排页序要用）
        var references = new Dictionary<long, IReadOnlyList<UnityObjectReference>>();

        long skeletonPathId = 0;
        string skeletonName = string.Empty;
        byte[]? skeletonBytes = null;
        bool skeletonBinary = false;
        long atlasPathId = 0;
        string? atlasText = null;
        // 图集资源（AtlasAsset）自己的 pathId：它的 materials 数组就是页数与顺序
        long atlasAssetPathId = 0;

        while (queue.Count > 0 && visited.Count <= MaxNodes)
        {
            var (pathId, depth) = queue.Dequeue();
            IReadOnlyList<UnityObjectReference> refs;
            try { refs = _reader.ReadReferences(_serializedFileName, pathId); }
            catch (Exception) { continue; } // 单个对象读不动就跳过：整条链还有其他分支
            references[pathId] = refs;

            foreach (var reference in refs)
            {
                if (reference.FileId != 0 || reference.PathId == 0) continue; // 外部文件引用：本次不看
                var target = reference.PathId;
                if (!visited.Add(target)) continue;
                if (depth + 1 > MaxDepth) continue;

                // 按 content 判定 TextAsset：命中就留在 cheese 里
                UnityTextAsset? textAsset = TryReadTextAsset(target);
                if (textAsset is { Data.Length: > 0 })
                {
                    var text = TryUtf8(textAsset.Data);
                    if (text is not null)
                    {
                        if (skeletonBytes is null && IsSkeletonJson(text))
                        {
                            skeletonPathId = target;
                            skeletonName = textAsset.Name;
                            skeletonBytes = textAsset.Data;
                        }
                        else if (atlasText is null && IsAtlasText(text))
                        {
                            atlasPathId = target;
                            atlasText = text;
                            atlasAssetPathId = pathId; // 谁引用了它，谁就是那个 Atlas 资源
                        }
                    }
                }

                queue.Enqueue((target, depth + 1));
            }
        }

        if (skeletonBytes is null || atlasText is null) return null; // 骨架或图集缺一就不是三件套

        return new SpinePrefabChain(
            skeletonPathId, skeletonName, skeletonBytes, skeletonBinary, atlasPathId, atlasText,
            ResolveTexturePages(atlasAssetPathId, atlasText, references));
    }

    /// <summary>
    /// 纹理页 <b>path_id</b> 的清单，顺序与图集文本里的页面顺序一致：
    /// 先按 <c>materials.Array[i]</c> 的序号取（Spine-Unity 的既有约定），
    /// 这条不成立时回退成「发现顺序」，页面的名字由
    /// <see cref="SpineAtlasPageNames"/> 从图集正文里读，两者数量不一致时调用方会记警告。
    /// </summary>
    private IReadOnlyList<long> ResolveTexturePages(
        long atlasAssetPathId, string atlasText, Dictionary<long, IReadOnlyList<UnityObjectReference>> references)
    {
        var pages = new List<long>();
        if (atlasAssetPathId != 0 && references.TryGetValue(atlasAssetPathId, out var atlasRefs))
        {
            var materials = atlasRefs
                .Where(x => x.FieldPath.Contains("materials.Array[", StringComparison.Ordinal) && x.FileId == 0)
                .Select(x => (Index: MaterialIndex(x.FieldPath), x.PathId))
                .Where(x => x.Index >= 0)
                .OrderBy(x => x.Index)
                .ToList();
            foreach (var material in materials)
            {
                var texture = FirstTextureOf(material.PathId, references);
                if (texture is { } pathId && !pages.Contains(pathId)) pages.Add(pathId);
            }
        }
        if (pages.Count > 0) return pages;

        // 回退：链上所有可达 Texture2D，按发现顺序（可能多出非图集页的纹理，调用方只对前 N 个取名）
        foreach (var (owner, refs) in references)
        {
            foreach (var reference in refs)
            {
                if (reference.FileId != 0 || reference.PathId == 0) continue;
                if (!IsTexture(reference.TargetType, reference.PathId)) continue;
                if (!pages.Contains(reference.PathId)) pages.Add(reference.PathId);
            }
        }
        return pages;
    }

    /// <summary>找一个 Material 引用的第一张纹理（Material 内部的 <c>m_TexEnvs</c> / <c>m_Texture</c>）。</summary>
    private long? FirstTextureOf(long materialPathId, Dictionary<long, IReadOnlyList<UnityObjectReference>> references)
    {
        if (!references.TryGetValue(materialPathId, out var materialRefs)) return null;
        foreach (var reference in materialRefs)
        {
            if (reference.FileId != 0 || reference.PathId == 0) continue;
            if (IsTexture(reference.TargetType, reference.PathId)) return reference.PathId;
            // Material 的组织形态之一是先指向子结构；跟一层再找（最多一层，不递归到底）
            if (references.TryGetValue(reference.PathId, out var nested))
            {
                var hit = nested.FirstOrDefault(x => x.FileId == 0 && x.PathId != 0
                    && IsTexture(x.TargetType, x.PathId));
                if (hit is not null) return hit.PathId;
            }
        }
        return null;
    }

    private bool IsTexture(string? targetType, long pathId)
    {
        if (targetType is not null && targetType.Contains("Texture", StringComparison.OrdinalIgnoreCase)) return true;
        try { return _reader.ClassIdOf(_serializedFileName, pathId) == UnityClassId.Texture2D; }
        catch (Exception) { return false; }
    }

    private UnityTextAsset? TryReadTextAsset(long pathId)
    {
        try { return _reader.ReadTextAsset(_serializedFileName, pathId); }
        catch (Exception) { return null; } // 不是 TextAsset（或缺字段）：不是错误，跳过
    }

    private static int MaterialIndex(string fieldPath)
    {
        var start = fieldPath.IndexOf("materials.Array[", StringComparison.Ordinal) + "materials.Array[".Length;
        var end = fieldPath.IndexOf(']', start);
        return end > start && int.TryParse(fieldPath[start..end], out var index) ? index : -1;
    }

    private static string? TryUtf8(byte[] data)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            return text.Contains('\0') ? null : text; // 二进制骨架 / 乱码不算文本
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 正文是否<b>像 Spine 骨架 JSON</b>：必须同时有 <c>"skeleton"</c> 与 <c>"bones"</c> 两个键，
    /// 且能被 JSON 解析器读通 —— 光名字像（.json）不算，本机样本里假命中很多。
    /// </summary>
    internal static bool IsSkeletonJson(string text)
    {
        if (!text.Contains("\"skeleton\"", StringComparison.Ordinal)) return false;
        if (!text.Contains("\"bones\"", StringComparison.Ordinal)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>
    /// 正文是否<b>像 Spine 图集</b>：第一行是页面文件名（<c>.png</c>），跟随
    /// <c>size:</c> / <c>filter:</c> / <c>pma:</c> / <c>format:</c> 之类页面指令。
    /// 这是与 Unity <c>spriteatlasv2</c> 区分开的关键（后者是 YAML/二进制，没有这类指令行）。
    /// </summary>
    internal static bool IsAtlasText(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;
        var first = lines[0].Trim();
        if (!(first.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
              || first.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))) return false;
        for (var i = 1; i < Math.Min(lines.Length, 8); i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("size:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("format:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("filter:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("pma:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("scale:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>从 Spine 图集正文里读出<b>页面文件名</b>（顺序 = 页序）：那些非缩进、以页图扩展名结尾的行。</summary>
internal static class SpineAtlasPageNames
{
    public static IReadOnlyList<string> Read(string atlasText)
    {
        var pages = new List<string>();
        foreach (var rawLine in atlasText.Split('\n'))
        {
            var line = rawLine.Trim('\r').Trim();
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue; // 缩进行是区域，不缩进才是页面/指令
            if (line.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || line.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                pages.Add(line);
        }
        return pages;
    }
}
