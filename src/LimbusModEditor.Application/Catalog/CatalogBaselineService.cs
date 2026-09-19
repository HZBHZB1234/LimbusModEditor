using LimbusModEditor.Formats.Unity;
using NLog;
using System.IO.Hashing;

namespace LimbusModEditor.Application.Catalog;

/// <summary>把缓存 bundle（&lt;缓存根&gt;/&lt;外层&gt;/&lt;inner&gt;/__data 或
/// &lt;32hex&gt;.bundle 文件）对照官方 catalog 记录做 vanilla 基线判定。
/// CRC 口径与 LCTA launcher/staticmod.py 的 bundle_decompressed_crc 一致：
/// UnityFS 全部块解压后拼接的 zlib CRC32；解压交给 AssetsTools.NET，
/// 本服务只在其解压结果流上计算。</summary>
public static class CatalogBaselineService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>从候选路径提取 inner 内容哈希：优先缓存布局的父目录名，
    /// 其次 &lt;32hex&gt;.bundle 文件名（与 LCTA _bundle_inner 同口径，大小写
    /// 不敏感统一为小写）。</summary>
    public static string? ExtractInnerHash(string bundleDataPath)
    {
        if (string.IsNullOrWhiteSpace(bundleDataPath)) return null;
        var fileName = Path.GetFileName(bundleDataPath);
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(bundleDataPath)));
        if (parent is { Length: 32 } && IsHex(parent)) return parent.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(?:^|l_)([0-9a-fA-F]{32})\.bundle$");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static bool IsHex(string text)
        => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>判定一个 bundle 的 vanilla 基线状态（只读，不修改任何文件）。</summary>
    public static CatalogBaselineResult Evaluate(CatalogFileService catalog, string bundleDataPath, Stream? decompressed = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDataPath);
        if (!File.Exists(bundleDataPath)) throw new FileNotFoundException("bundle 文件不存在。", bundleDataPath);

        var inner = ExtractInnerHash(bundleDataPath);
        if (inner is null)
            return new(CatalogVerdict.Uncertain, string.Empty, "无法识别内容哈希（不是标准缓存布局：父目录名或文件名都不是 32 位 hex）");

        var record = catalog.FindByInnerHash(inner);
        if (record is null)
            return new(CatalogVerdict.NotInCatalog, inner, "inner 哈希不在当前 catalog 中（游戏更新更换了外层键，或非官方 bundle）");

        if (record.Size is not { } expectedSize)
            return new(CatalogVerdict.Uncertain, inner, $"在 catalog 中找到（{record.Name}），但记录区缺少可信的 CRC/大小");

        var actualSize = new FileInfo(bundleDataPath).Length;
        if (actualSize != expectedSize)
            return new(CatalogVerdict.Modified, inner,
                $"大小与 vanilla 记录不一致（catalog {expectedSize}，实际 {actualSize}）——可能已被模组补丁修改");

        if (record.Crc is not { } expectedCrc)
            return new(CatalogVerdict.Uncertain, inner, "大小一致但记录区缺少 CRC，无法完成对比");

        // 扫描可借用刚解析过的解压流，避免同一 bundle 为 CRC 再打开、解包一次。
        // 但「借用」是有代价的：解析阶段已经把这条流读过一遍，回绕（Position=0）后
        // 再顺序读，AssetsTools 的 LZ4BlockStream 在个别 bundle 上会直接越界
        // （日志实测：System.IndexOutOfRangeException at LZ4BlockStream.Read）。
        // 那属于第三方流实现的脾气，不是 bundle 坏了 —— 退回「重新打开一次」
        // 就能算出正确 CRC，代价只是一个 bundle 的加载，且只发生在极少数条目上。
        (uint? Crc, long DecompressedLength)? crcResult = null;
        if (decompressed is not null)
        {
            try { crcResult = (Crc32Of(decompressed), decompressed.Length); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException
                                          or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                Log.Debug(ex, "借用解压流算 CRC 失败，改为重新打开 bundle：{0}", bundleDataPath);
            }
        }
        crcResult ??= ComputeBundleCrc(bundleDataPath);
        if (crcResult is null || crcResult.Value.Crc is not { } actualCrc)
            return new(CatalogVerdict.Uncertain, inner, "大小一致但实际 CRC 无法计算（不是可解压的 UnityFS）");

        return actualCrc == expectedCrc
            ? new(CatalogVerdict.Vanilla, inner, $"与 vanilla 基线一致（大小 {actualSize}，CRC 0x{actualCrc:X8}）")
            : new(CatalogVerdict.Modified, inner,
                $"CRC 与 vanilla 记录不一致（catalog 0x{expectedCrc:X8}，实际 0x{actualCrc:X8}）——可能已被模组补丁修改");
    }

    /// <summary>UnityFS 全块解压后拼接流的 zlib CRC32（与 LCTA 口径一致）。
    /// 返回 null 表示该文件不是可解压的 UnityFS bundle。</summary>
    public static (uint? Crc, long DecompressedLength)? ComputeBundleCrc(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        if (!File.Exists(bundlePath)) throw new FileNotFoundException("bundle 文件不存在。", bundlePath);
        using var backend = new AssetsToolsBackend();
        // LoadBundleFile(unpackIfPacked: true) 之后 DataReader 就是「全部块解压
        // 后拼接」的数据流 —— 与 staticmod.py bundle_decompressed_crc 的口径一致
        if (!backend.TryLoadBundleForCrc(bundlePath, out var stream) || stream is null) return null;
        var crc = Crc32Of(stream);
        return (crc, stream.Length);
    }

    /// <summary>zlib/IEEE CRC32。使用运行库优化的流式实现，避免逐字节托管循环。
    /// 读之前把流拨回 0；结束（含中途抛异常）时尽量还原原位置，让调用方
    /// 还能接着用这条借来的流。还原失败不覆盖真正的失败原因。</summary>
    private static uint Crc32Of(Stream stream)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            var crc = new Crc32();
            crc.Append(stream);
            return crc.GetCurrentHashAsUInt32();
        }
        finally
        {
            try { stream.Position = position; }
            catch (Exception) { /* 不可回绕的流：还原不了就让它停在原处 */ }
        }
    }
}
