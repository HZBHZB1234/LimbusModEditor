using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Unity;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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
        using var scope = Log.Scope("定位 static catalog 记录");
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        if (!File.Exists(catalogPath))
        {
            Log.Error("catalog 文件不存在：{0}", catalogPath);
            throw new FileNotFoundException("catalog 文件不存在。", catalogPath);
        }
        Log.Info("定位 static catalog 记录开始：catalog {0}", catalogPath);
        var data = File.ReadAllBytes(catalogPath);
        Log.Debug("已读入 catalog：{0}，{1} 字节", catalogPath, data.Length);
        var match = StaticBundleName.Match(Encoding.ASCII.GetString(data));
        if (!match.Success)
        {
            Log.Error("catalog 里找不到 static_s1_0_assets_all_<32hex>.bundle（catalog {0}，{1} 字节）——游戏版本可能变了",
                catalogPath, data.Length);
            throw new InvalidDataException("catalog 里找不到 static_s1_0_assets_all_<32hex>.bundle（游戏版本可能变了）");
        }
        var inner = match.Groups[1].Value;
        Log.Debug("static bundle 名匹配成功：static_s1_0_assets_all_{0}.bundle（inner {1}）", inner, inner);
        var hit = FindUnique(data, Convert.FromHexString(inner), out var occurrences);
        if (hit < 0)
        {
            Log.Error("catalog 里找不到唯一的内容哈希记录：inner {0}，出现 {1} 次（catalog {2}，{3} 字节）",
                inner, occurrences, catalogPath, data.Length);
            throw new InvalidDataException($"catalog 里找不到唯一的内容哈希记录（{inner}，出现 {occurrences} 次）");
        }
        var outerLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + 0x10, 4));
        if (outerLength is < 16 or > 64)
        {
            Log.Error("catalog 记录的外层键长度异常：inner {0} 命中偏移 0x{1:X}，外层键长度 {2}，拒绝写入（catalog {3}）",
                inner, hit, outerLength, catalogPath);
            throw new InvalidDataException($"catalog 记录的外层键长度异常（{outerLength}），拒绝按此偏移写入");
        }
        var outer = Encoding.ASCII.GetString(data, hit + 0x14, (int)outerLength);
        if (!Hex32.IsMatch(outer))
        {
            Log.Error("catalog 记录的外层键不是 32-hex：inner {0} 命中偏移 0x{1:X}，外层键 {2}，拒绝写入（catalog {3}）",
                inner, hit, outer, catalogPath);
            throw new InvalidDataException($"catalog 记录的外层键不是 32-hex（{outer}），拒绝按此偏移写入");
        }
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + CrcOffset, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + SizeOffset, 4));
        Log.Debug("按已知布局读取字段：inner 命中偏移 0x{0:X}，outer {1}，crc@+0x{2:X}=0x{3:X8}，size@+0x{4:X}={5}",
            hit, outer, CrcOffset, crc, SizeOffset, size);
        if (size < MinSize || size > MaxSize)
        {
            Log.Error("catalog 记录 size 不合理：inner {0}，偏移 +0x{1:X} 读到 size={2}（期望 {3}–{4}），" +
                      "catalog {5} 的布局与已知布局不同，拒绝写入",
                inner, SizeOffset, size, MinSize, MaxSize, catalogPath);
            throw new InvalidDataException(
                $"catalog 该记录在 +0x{SizeOffset:X} 处读到的 size={size} 不合理（期望 {MinSize}–{MaxSize}），" +
                "说明这一版 catalog 的布局与已知布局不同；拒绝写入 catalog（静态模组本次不应用）");
        }
        var slot = new StaticCatalogSlot(Path.GetFullPath(catalogPath),
            $"static_s1_0_assets_all_{inner}.bundle", inner, outer, CrcOffset, SizeOffset, crc, size);
        Log.Info("定位 static catalog 记录完成：{0}", slot);
        return slot;
    }

    /// <summary>把新的 crc/size 写回 catalog 的同一位置（原子替换）。</summary>
    public void WriteCatalogFields(StaticCatalogSlot slot, uint crc, uint size)
    {
        using var scope = Log.Scope("写回 catalog 字段");
        ArgumentNullException.ThrowIfNull(slot);
        Log.Info("写回 catalog 字段：{0}，新 crc=0x{1:X8}，新 size={2}", slot.CatalogPath, crc, size);
        var data = File.ReadAllBytes(slot.CatalogPath);
        Log.Debug("已读入 catalog 以备写回：{0}，{1} 字节", slot.CatalogPath, data.Length);
        var hit = FindUnique(data, Convert.FromHexString(slot.InnerHash), out _);
        if (hit < 0)
        {
            Log.Error("写回 catalog 时找不到该记录（文件在本次会话中被改动过）：inner {0}，catalog {1}",
                slot.InnerHash, slot.CatalogPath);
            throw new InvalidDataException("写回 catalog 时找不到该记录（文件在本次会话中被改动过）");
        }
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(hit + slot.CrcOffset, 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(hit + slot.SizeOffset, 4), size);
        Log.Debug("catalog 字段已就地改写：命中偏移 0x{0:X}，crc@+0x{1:X}，size@+0x{2:X}",
            hit, slot.CrcOffset, slot.SizeOffset);
        var temporary = slot.CatalogPath + ".lme-tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, slot.CatalogPath, overwrite: true);
        Log.Info("catalog 写回完成：{0} → {1}", temporary, slot.CatalogPath);
    }

    /// <summary>
    /// 应用一个 <c>.staticmod</c>：改 TextAsset → 重打包 → 算解压块 CRC32 → 双写缓存条目与 catalog。
    /// 返回（新 CRC, 新 size, 已应用的对象名）。
    /// </summary>
    public async Task<(uint Crc, uint Size, IReadOnlyList<string> Applied)> ApplyAsync(
        string staticModPath, StaticCatalogSlot slot, string officialBundlePath, string cacheEntryDirectory,
        string cacheEntryInfoContent, CancellationToken cancellationToken = default)
    {
        using var scope = Log.Scope("应用静态模组");
        ArgumentException.ThrowIfNullOrWhiteSpace(staticModPath);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialBundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheEntryDirectory);
        Log.Info("应用静态模组开始：补丁包 {0}，官方 bundle {1}，缓存条目目录 {2}",
            staticModPath, officialBundlePath, cacheEntryDirectory);

        var package = ReadPackage(staticModPath);
        Log.Debug("静态模组包已解析：{0} 个补丁条目", package.Patches.Count);
        var working = Path.Combine(Path.GetTempPath(), "lme-static-apply-" + Guid.NewGuid().ToString("N") + ".bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(working)!);
        Log.Debug("重打包工作文件：{0}", working);
        var applied = new List<string>();
        try
        {
            using var backend = new AssetsToolsBackend();
            foreach (var (dataClass, fileName, container, ops) in package.Patches)
            {
                if (cancellationToken.IsCancellationRequested)
                    Log.Info("应用静态模组：取消已请求（已完成 {0}/{1} 个条目）", applied.Count, package.Patches.Count);
                cancellationToken.ThrowIfCancellationRequested();
                if (Log.IsTraceEnabled) Log.Trace("处理补丁条目：{0}/{1}，{2} 个操作", dataClass, fileName, ops.Count);
                var target = LocateTextAsset(backend, officialBundlePath, dataClass, fileName, container);
                if (target is null)
                {
                    Log.Error("静态 bundle 里找不到 TextAsset：{0}/{1}（container {2}，bundle {3}）",
                        dataClass, fileName, container ?? "-", officialBundlePath);
                    throw new InvalidDataException($"静态 bundle 里找不到 TextAsset: {dataClass}/{fileName}" +
                                                   (string.IsNullOrWhiteSpace(container) ? string.Empty : $"（container={container}）"));
                }
                var document = JsonNode.Parse(target.Value.Script)
                    ?? throw new InvalidDataException($"TextAsset 正文不是 JSON: {dataClass}/{fileName}");
                var patched = new TextDiffService().Apply(document, ops);
                var serialized = BuildTextAssetRaw(target.Value.Name, patched.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                if (Log.IsTraceEnabled)
                    Log.Trace("补丁正文已重建：{0}/{1}，{2} 字节（原 {3} 字节）",
                        target.Value.SerializedFileName, target.Value.PathId, serialized.Length, target.Value.Script.Length);
                backend.ReplaceBundleSerializedAsset(officialBundlePath, target.Value.SerializedFileName,
                    target.Value.PathId, serialized, working);
                applied.Add($"{dataClass}/{fileName}");
            }
            if (applied.Count == 0)
            {
                Log.Error("这个 .staticmod 没有任何补丁条目：{0}", staticModPath);
                throw new InvalidDataException("这个 .staticmod 没有任何补丁条目");
            }
            Log.Debug("补丁重打包完成：应用 {0}/{1} 个条目 → 工作文件 {2}", applied.Count, package.Patches.Count, working);

            // 解压后块数据拼接的 CRC32 + 文件大小（引擎缓存校验的对象）。
            var crcResult = CatalogBaselineService.ComputeBundleCrc(working)
                ?? throw new InvalidDataException("无法计算重打包后 bundle 的解压块 CRC（静态模组本次不应用）");
            var (crc, size) = (crcResult.Crc!.Value, crcResult.DecompressedLength);
            Log.Debug("重打包后 CRC 计算完成：crc=0x{0:X8}，解压长度 {1} 字节", crc, size);

            // 双写：缓存条目 __data + __info，然后写 catalog 字段。
            Directory.CreateDirectory(cacheEntryDirectory);
            var dataPath = Path.Combine(cacheEntryDirectory, "__data");
            var dataStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await using (var input = File.OpenRead(working))
            await using (var output = File.Create(dataPath))
                await input.CopyToAsync(output, cancellationToken);
            Log.Debug("缓存条目 __data 已写出：{0} → {1}，耗时 {2} ms", working, dataPath, dataStopwatch.ElapsedMilliseconds);
            var infoPath = Path.Combine(cacheEntryDirectory, "__info");
            await File.WriteAllTextAsync(infoPath,
                string.IsNullOrEmpty(cacheEntryInfoContent) ? DefaultCacheInfo() : cacheEntryInfoContent,
                new UTF8Encoding(false), cancellationToken);
            Log.Debug("缓存条目 __info 已写出：{0}（{1} 字符，{2}）", infoPath,
                string.IsNullOrEmpty(cacheEntryInfoContent) ? DefaultCacheInfo().Length : cacheEntryInfoContent.Length,
                string.IsNullOrEmpty(cacheEntryInfoContent) ? "使用默认内容" : "沿用传入内容");
            WriteCatalogFields(slot, crc, (uint)size);
            Log.Info("应用静态模组完成：crc=0x{0:X8}，size={1}，应用 {2} 个条目（{3}）",
                crc, (uint)size, applied.Count, string.Join("、", applied));
            return (crc, (uint)size, applied);
        }
        catch (OperationCanceledException)
        {
            Log.Info("应用静态模组已取消：补丁包 {0}（已完成 {1} 个条目，工作文件将被删除）", staticModPath, applied.Count);
            throw;
        }
        finally
        {
            try { File.Delete(working); } catch (Exception delEx) { /* 临时文件 */ Log.Debug(delEx, "删除静态重打包临时文件失败（留在临时目录）：{0}", working); }
        }
    }

    /// <summary>缓存条目的 __info 内容（token 格式：<c>-1\n&lt;ts&gt;\n1\n__data\n</c>）。</summary>
    public static string DefaultCacheInfo()
        => $"-1\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n1\n__data\n";

    // ── .staticmod 包解析（复用 StaticModService 的读语义）──────────────

    private static (IReadOnlyList<(string DataClass, string File, string? Container, JsonArray Ops)> Patches,
        IReadOnlyList<(string DataClass, string File, string? Container, JsonNode Full)> Full) ReadPackage(string path)
    {
        Log.Debug("解析 .staticmod 包开始：{0}", path);
        var package = new StaticModService().Read(path);
        Log.Debug("静态模组包内容：patches {0} 条，fullFiles {1} 条，负载 {2} 个",
            package.Patches.Count, package.FullFiles.Count, package.Payloads.Count);
        var patches = new List<(string, string, string?, JsonArray)>();
        foreach (var patch in package.Patches)
        {
            if (!package.Payloads.TryGetValue(patch.Source, out var payload))
            {
                Log.Error("补丁负载缺失：{0}/{1}，负载键 {2}（包 {3}）", patch.DataClass, patch.File, patch.Source, path);
                throw new InvalidDataException($"补丁负载缺失: {patch.Source}");
            }
            if (!string.Equals(patch.OpType, "jsonpatch", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("本版调试只支持 opType=jsonpatch 的静态补丁：{0}/{1} 实际 opType={2}（包 {3}）",
                    patch.DataClass, patch.File, patch.OpType, path);
                throw new InvalidDataException(
                    $"本版调试只支持 opType=jsonpatch 的静态补丁（实际 {patch.OpType}）；pathset 请先用加载器转换");
            }
            patches.Add((patch.DataClass, patch.File, patch.Container, payload.AsArray()));
        }
        var full = package.FullFiles
            .Select(x => (x.DataClass, x.File, x.Container,
                package.Payloads.TryGetValue(x.Source, out var payload) ? payload : throw new InvalidDataException($"整文件负载缺失: {x.Source}")))
            .ToList();
        if (full.Count > 0)
        {
            Log.Error("本版调试只支持 jsonpatch 条目，但包里有 {0} 个 fullFiles 条目（包 {1}）", full.Count, path);
            throw new InvalidDataException("本版调试只支持 jsonpatch 条目；fullFiles 需先用加载器转换（编辑器导出走的也是 jsonpatch）");
        }
        Log.Debug("解析 .staticmod 包完成：{0}，{1} 个 jsonpatch 条目", path, patches.Count);
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
