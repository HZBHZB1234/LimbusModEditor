using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Catalog;

/// <summary>把缓存 bundle（&lt;缓存根&gt;/&lt;外层&gt;/&lt;inner&gt;/__data 或
/// &lt;32hex&gt;.bundle 文件）对照官方 catalog 记录做 vanilla 基线判定。
/// CRC 口径与 LCTA launcher/staticmod.py 的 bundle_decompressed_crc 一致：
/// UnityFS 全部块解压后拼接的 zlib CRC32；解压交给 AssetsTools.NET，
/// 本服务只在其解压结果流上计算。</summary>
public static class CatalogBaselineService
{
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
    public static CatalogBaselineResult Evaluate(CatalogFileService catalog, string bundleDataPath)
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

        var crcResult = ComputeBundleCrc(bundleDataPath);
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

    /// <summary>zlib CRC32（IEEE 反射多项式 0xEDB88320），表驱动实现。</summary>
    private static uint Crc32Of(Stream stream)
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        Span<byte> buffer = stackalloc byte[8192];
        uint crc = 0xFFFFFFFF;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
                crc = table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
