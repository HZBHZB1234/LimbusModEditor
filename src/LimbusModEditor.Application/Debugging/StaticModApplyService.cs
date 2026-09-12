using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Debugging;

/// <summary>
/// plan-16 S7b：<b>静态数据模组（.staticmod）在调试期的应用与还原</b>。
///
/// <para>为什么单独一个服务：静态 bundle 是全表唯一开启 <c>UseCrcForCachedBundles</c> 的条目，
/// 引擎加载缓存时会核对「解压后块数据拼接」的 zlib CRC32 与 catalog 里记录的 crc/size。
/// 所以它不能像普通 bundle 那样只改缓存 <c>__data</c>，必须：改 TextAsset → 重打包 →
/// 算新 CRC/size → <b>双写</b>缓存条目与 catalog 记录。这与 LCTA <c>launcher/staticmod.py</c>
/// 的应用语义完全一致（本实现按同一事实实现，不复制其代码）。</para>
///
/// <para><b>默认关闭且需要确认</b>：这会写 catalog（游戏资源索引）。调用方必须先让用户确认；
/// 本服务只负责「按语义正确执行 + 逐字节可还原」。</para>
///
/// <para><b>记录区按偏移显式写入</b>（而不是用 <see cref="CatalogFileService"/> 的自校准解析）：
/// 双写必须是「读哪一位、写哪一位」，偏移错一字节就是灾难。定位规则取自 LCTA
/// <c>locate_static_entry</c>（<c>static_s1_0_assets_all_&lt;32hex&gt;</c> → 该 32hex 的 Hash128
/// → 校验其后外层键为 32-hex → crc@Hash128+0x44 / size@Hash128+0x48），并用 size 合理性
/// （0.1–50 MB）作为落到这一版布局的判据；不合理就拒绝写（宁可不应用，也不写错位置）。</para>
/// </summary>
public sealed class StaticModApplyService
{
    private static readonly Regex StaticBundleName = new(
        @"static_s1_0_assets_all_([0-9a-f]{32})\.bundle", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Hex32 = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>crc/size 相对 Hash128 起点的偏移（LCTA staticmod.py 实证布局）。</summary>
    private const int CrcOffset = 0x44;
    private const int SizeOffset = 0x48;
    private const uint MinSize = 100_000;
    private const uint MaxSize = 50_000_000;

    /// <summary>catalog 中静态条目的定位结果（含可变字段的偏移）。</summary>
    public sealed record StaticCatalogSlot(string CatalogPath, string BundleName, string InnerHash, string OuterKey,
        int CrcOffset, int SizeOffset, uint CurrentCrc, uint CurrentSize)
    {
        public override string ToString()
            => $"{BundleName}（inner {InnerHash[..8]}… / outer {OuterKey[..8]}… / crc 0x{CurrentCrc:X8} / size {CurrentSize}）";
    }

    /// <summary>定位当前 catalog 里的 static 条目；定位失败抛 <see cref="InvalidDataException"/> 并说明原因。</summary>
    public StaticCatalogSlot Locate(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        if (!File.Exists(catalogPath)) throw new FileNotFoundException("catalog 文件不存在。", catalogPath);
        var data = File.ReadAllBytes(catalogPath);
        var match = StaticBundleName.Match(Encoding.ASCII.GetString(data));
        if (!match.Success)
            throw new InvalidDataException("catalog 里找不到 static_s1_0_assets_all_<32hex>.bundle（游戏版本可能变了）");
        var inner = match.Groups[1].Value;
        var hit = FindUnique(data, Convert.FromHexString(inner), out var occurrences);
        if (hit < 0)
            throw new InvalidDataException($"catalog 里找不到唯一的内容哈希记录（{inner}，出现 {occurrences} 次）");
        var outerLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + 0x10, 4));
        if (outerLength is < 16 or > 64)
            throw new InvalidDataException($"catalog 记录的外层键长度异常（{outerLength}），拒绝按此偏移写入");
        var outer = Encoding.ASCII.GetString(data, hit + 0x14, (int)outerLength);
        if (!Hex32.IsMatch(outer))
            throw new InvalidDataException($"catalog 记录的外层键不是 32-hex（{outer}），拒绝按此偏移写入");
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + CrcOffset, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + SizeOffset, 4));
        if (size < MinSize || size > MaxSize)
            throw new InvalidDataException(
                $"catalog 该记录在 +0x{SizeOffset:X} 处读到的 size={size} 不合理（期望 {MinSize}–{MaxSize}），" +
                "说明这一版 catalog 的布局与已知布局不同；拒绝写入 catalog（静态模组本次不应用）");
        return new StaticCatalogSlot(Path.GetFullPath(catalogPath),
            $"static_s1_0_assets_all_{inner}.bundle", inner, outer, CrcOffset, SizeOffset, crc, size);
    }

    /// <summary>把新的 crc/size 写回 catalog 的同一位置（原子替换）。</summary>
    public void WriteCatalogFields(StaticCatalogSlot slot, uint crc, uint size)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var data = File.ReadAllBytes(slot.CatalogPath);
        var hit = FindUnique(data, Convert.FromHexString(slot.InnerHash), out _);
        if (hit < 0) throw new InvalidDataException("写回 catalog 时找不到该记录（文件在本次会话中被改动过）");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(hit + slot.CrcOffset, 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(hit + slot.SizeOffset, 4), size);
        var temporary = slot.CatalogPath + ".lme-tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, slot.CatalogPath, overwrite: true);
    }

    /// <summary>
    /// 应用一个 <c>.staticmod</c>：改 TextAsset → 重打包 → 算解压块 CRC32 → 双写缓存条目与 catalog。
    /// 返回（新 CRC, 新 size, 已应用的对象名）。
    /// </summary>
    public async Task<(uint Crc, uint Size, IReadOnlyList<string> Applied)> ApplyAsync(
        string staticModPath, StaticCatalogSlot slot, string officialBundlePath, string cacheEntryDirectory,
        string cacheEntryInfoContent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staticModPath);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialBundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheEntryDirectory);

        var package = ReadPackage(staticModPath);
        var working = Path.Combine(Path.GetTempPath(), "lme-static-apply-" + Guid.NewGuid().ToString("N") + ".bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(working)!);
        var applied = new List<string>();
        try
        {
            using var backend = new AssetsToolsBackend();
            foreach (var (dataClass, fileName, container, ops) in package.Patches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = LocateTextAsset(backend, officialBundlePath, dataClass, fileName, container);
                if (target is null)
                    throw new InvalidDataException($"静态 bundle 里找不到 TextAsset: {dataClass}/{fileName}" +
                                                   (string.IsNullOrWhiteSpace(container) ? string.Empty : $"（container={container}）"));
                var document = JsonNode.Parse(target.Value.Script)
                    ?? throw new InvalidDataException($"TextAsset 正文不是 JSON: {dataClass}/{fileName}");
                var patched = new TextDiffService().Apply(document, ops);
                var serialized = BuildTextAssetRaw(target.Value.Name, patched.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                backend.ReplaceBundleSerializedAsset(officialBundlePath, target.Value.SerializedFileName,
                    target.Value.PathId, serialized, working);
                applied.Add($"{dataClass}/{fileName}");
            }
            if (applied.Count == 0) throw new InvalidDataException("这个 .staticmod 没有任何补丁条目");

            // 解压后块数据拼接的 CRC32 + 文件大小（引擎缓存校验的对象）。
            var crcResult = CatalogBaselineService.ComputeBundleCrc(working)
                ?? throw new InvalidDataException("无法计算重打包后 bundle 的解压块 CRC（静态模组本次不应用）");
            var (crc, size) = (crcResult.Crc!.Value, crcResult.DecompressedLength);

            // 双写：缓存条目 __data + __info，然后写 catalog 字段。
            Directory.CreateDirectory(cacheEntryDirectory);
            await using (var input = File.OpenRead(working))
            await using (var output = File.Create(Path.Combine(cacheEntryDirectory, "__data")))
                await input.CopyToAsync(output, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(cacheEntryDirectory, "__info"),
                string.IsNullOrEmpty(cacheEntryInfoContent) ? DefaultCacheInfo() : cacheEntryInfoContent,
                new UTF8Encoding(false), cancellationToken);
            WriteCatalogFields(slot, crc, (uint)size);
            return (crc, (uint)size, applied);
        }
        finally
        {
            try { File.Delete(working); } catch (Exception) { /* 临时文件 */ }
        }
    }

    /// <summary>缓存条目的 __info 内容（token 格式：<c>-1\n&lt;ts&gt;\n1\n__data\n</c>）。</summary>
    public static string DefaultCacheInfo()
        => $"-1\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n1\n__data\n";

    // ── .staticmod 包解析（复用 StaticModService 的读语义）──────────────

    private static (IReadOnlyList<(string DataClass, string File, string? Container, JsonArray Ops)> Patches,
        IReadOnlyList<(string DataClass, string File, string? Container, JsonNode Full)> Full) ReadPackage(string path)
    {
        var package = new StaticModService().Read(path);
        var patches = new List<(string, string, string?, JsonArray)>();
        foreach (var patch in package.Patches)
        {
            if (!package.Payloads.TryGetValue(patch.Source, out var payload))
                throw new InvalidDataException($"补丁负载缺失: {patch.Source}");
            if (!string.Equals(patch.OpType, "jsonpatch", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"本版调试只支持 opType=jsonpatch 的静态补丁（实际 {patch.OpType}）；pathset 请先用加载器转换");
            patches.Add((patch.DataClass, patch.File, patch.Container, payload.AsArray()));
        }
        var full = package.FullFiles
            .Select(x => (x.DataClass, x.File, x.Container,
                package.Payloads.TryGetValue(x.Source, out var payload) ? payload : throw new InvalidDataException($"整文件负载缺失: {x.Source}")))
            .ToList();
        if (full.Count > 0)
            throw new InvalidDataException("本版调试只支持 jsonpatch 条目；fullFiles 需先用加载器转换（编辑器导出走的也是 jsonpatch）");
        return (patches, full);
    }

    // ── TextAsset 定位与原始字节重建 ────────────────────────────────

    private static (string SerializedFileName, long PathId, string Name, string Script)? LocateTextAsset(
        AssetsToolsBackend backend, string bundlePath, string dataClass, string fileName, string? container)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal) { fileName, $"{dataClass}/{fileName}" };
        var wantedContainer = string.IsNullOrWhiteSpace(container) ? null : container.Replace('\\', '/').ToLowerInvariant();
        foreach (var asset in backend.InspectBundle(bundlePath))
        {
            if (asset.AssetType != AssetType.Text) continue;
            // container 精确寻址优先（同名不同目录的表就靠它区分）；没给 container 时按名字兜底。
            var byContainer = wantedContainer is not null &&
                              string.Equals(asset.ContainerEntryPath?.ToLowerInvariant(), wantedContainer, StringComparison.Ordinal);
            if (!byContainer && (wantedContainer is not null || !wanted.Contains(asset.ContainerPath)))
                continue;
            var text = backend.ReadBundleTextAsset(bundlePath, asset.ContainerPath, asset.PathId);
            var script = text.TryDecodeUtf8()
                ?? throw new InvalidDataException($"TextAsset 不是合法 UTF-8（不猜编码）: {asset.ContainerPath}");
            return (asset.ContainerPath, asset.PathId, text.Name, script);
        }
        return null;
    }

    /// <summary>
    /// 重建 TextAsset 的对象原始字节：<c>&lt;i32 名长&gt;&lt;名&gt;</c>（4 字节对齐）
    /// + <c>&lt;i32 正文本长&gt;&lt;正文&gt;</c>（4 字节对齐）——与 Unity 的 TextAsset 序列化布局一致，
    /// 也与 LCTA <c>staticmod._build_textasset_raw</c> 同口径。
    /// </summary>
    public static byte[] BuildTextAssetRaw(string name, string script)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(script);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        writer.Write(nameBytes.Length);
        writer.Write(nameBytes);
        while (stream.Position % 4 != 0) writer.Write((byte)0);
        var scriptBytes = Encoding.UTF8.GetBytes(script);
        writer.Write(scriptBytes.Length);
        writer.Write(scriptBytes);
        while (stream.Position % 4 != 0) writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>在字节序列里找唯一出现位置；没有或多次出现时返回 -1 并给出次数。</summary>
    private static int FindUnique(byte[] data, byte[] pattern, out int occurrences)
    {
        var first = -1;
        occurrences = 0;
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (!data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) continue;
            occurrences++;
            if (first < 0) first = i;
            if (occurrences > 1) return -1;
        }
        return first;
    }
}
