using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Catalog;

public enum CatalogVerdict
{
    /// <summary>在 catalog 中，且大小与解压块 CRC32 都与 vanilla 记录一致。</summary>
    Vanilla,
    /// <summary>在 catalog 中，但大小或 CRC 与 vanilla 记录不一致（可能已被加载器补丁修改）。</summary>
    Modified,
    /// <summary>inner 内容哈希不在当前 catalog 中（游戏更新更换的旧键，或非官方 bundle）。</summary>
    NotInCatalog,
    /// <summary>在 catalog 中但没有可信的 CRC/大小记录，或实际 CRC 无法计算 —— 状态未知。</summary>
    Uncertain
}

/// <summary>catalog 中一个 bundle 的 vanilla 基线记录（布局事实来自 LCTA
/// launcher/staticmod.py 的定位代码，本项目不做猜测性扩展）。</summary>
public sealed record CatalogBundleRecord(string Name, string InnerHash, string? OuterKey, uint? Crc, uint? Size)
{
    public override string ToString() => OuterKey is null
        ? $"{Name}（无外层键记录）"
        : $"{Name} outer={OuterKey} crc={(Crc is null ? "—" : $"0x{Crc:X8}")} size={Size?.ToString() ?? "—"}";
}

/// <summary>对一个缓存 bundle 的 vanilla 基线判定结果。</summary>
public sealed record CatalogBaselineResult(CatalogVerdict Verdict, string InnerHash, string Detail)
{
    /// <summary>资源列表/导入报告使用的短摘要。</summary>
    public string Summary => Verdict switch
    {
        CatalogVerdict.Vanilla => "vanilla 一致",
        CatalogVerdict.Modified => "与 vanilla 不符",
        CatalogVerdict.NotInCatalog => "不在 catalog",
        _ => "基线未知"
    };
}

/// <summary>
/// 官方 catalog（catalog.bin / catalog_S1.bin，Unity ContentCatalog 二进制）的
/// 只读解析 + vanilla 基线判定。布局事实来源与对照：
/// - bundle 名提取与 inner/outer 映射：LCTA resource_updater/core.py
///   parse_catalog（435-460 行，正则启发式）；
/// - 记录区：数据中查找 16 字节 Hash128（=inner 内容哈希），+0x10 为 LE32
///   长度前缀的外层键字符串（LCTA launcher/staticmod.py 132-155 行）；
///   CRC32 与文件大小字段的位置在格式版本间会整体平移（staticmod 文档记录
///   2026-08-22 为 +0x44/+0x48，2026-09-03 实测 catalog 为 +0x3C/+0x40，
///   已用多个真实 bundle 的实际文件大小与解压块 CRC 逐一对照确证），因此
///   解析时在两个候选布局间按「大小值合理占比」自校准。
/// 本服务只读解析，绝不写回 catalog。
/// </summary>
public sealed class CatalogFileService
{
    /// <summary>候选记录布局：CRC 与大小的偏移（相对 Hash128 起点）。</summary>
    private static readonly (uint CrcOffset, uint SizeOffset)[] CandidateLayouts =
    [
        (0x3C, 0x40), // 2026-09-03 实测布局
        (0x44, 0x48), // LCTA staticmod.py 记录的 2026-08-22 布局
    ];
    private static readonly Regex BundleNamePattern = new(@"[A-Za-z0-9_.\-]+\.bundle", RegexOptions.Compiled);
    private static readonly Regex PureHashNamePattern = new(@"^(?:l_)?[0-9a-fA-F]{32}\.bundle$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InnerHashPattern = new(@"([0-9a-fA-F]{32})\.bundle$", RegexOptions.Compiled);
    private static readonly Regex OuterKeyPattern = new(@"(?<![0-9a-fA-F])([0-9a-fA-F]{32})(?![0-9a-fA-F])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private CatalogFileService(string filePath, IReadOnlyList<string> names, Dictionary<string, CatalogBundleRecord> recordsByInner)
    {
        FilePath = filePath;
        Names = names;
        RecordsByInnerHash = recordsByInner;
    }

    public string FilePath { get; }
    /// <summary>catalog 中解析出的全部 bundle 名（字典序，已排除纯哈希名）。</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>按 inner 内容哈希（32 位小写 hex）索引的记录。</summary>
    public IReadOnlyDictionary<string, CatalogBundleRecord> RecordsByInnerHash { get; }

    /// <summary>解析 catalog 文件；文件不存在或过小（连魔数都放不下）即报错。</summary>
    public static CatalogFileService Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("catalog 文件不存在。", path);
        var data = File.ReadAllBytes(path);
        if (data.Length < 4) throw new InvalidDataException($"catalog 文件过小，无法解析: {path}");
        return Parse(data, path);
    }

    public static CatalogFileService Parse(byte[] data, string? filePath = null)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in BundleNamePattern.Matches(Encoding.ASCII.GetString(data)))
        {
            var name = match.Value;
            // 与 LCTA parse_catalog 一致：过滤超长名与纯哈希名（外层键文件名）
            if (name.Length >= 200 || PureHashNamePattern.IsMatch(name)) continue;
            names.Add(name);
        }

        // 先定位全部 Hash128 记录区（外层键读取与布局无关）
        var regions = new List<(string Name, string Inner, int Hit)>();
        foreach (var name in names)
        {
            var innerMatch = InnerHashPattern.Match(name);
            if (!innerMatch.Success) continue;
            var inner = innerMatch.Groups[1].Value.ToLowerInvariant();
            if (regions.Any(x => x.Inner == inner)) continue; // 同内容多平台名共享记录
            var hit = IndexOf(data, HexToBytes(inner), 0);
            if (hit >= 0) regions.Add((name, inner, hit));
        }

        // 记录布局在格式版本间整体平移：按「大小值合理占比」在候选布局中自校准
        (uint CrcOffset, uint SizeOffset) layout = CandidateLayouts[0];
        var bestSaneRatio = -1.0;
        foreach (var candidate in CandidateLayouts)
        {
            var sane = regions.Count(r => TryReadCrcSize(data, r.Hit, candidate, out _, out _));
            var ratio = regions.Count == 0 ? 0 : (double)sane / regions.Count;
            if (ratio > bestSaneRatio)
            {
                bestSaneRatio = ratio;
                layout = candidate;
            }
        }

        var records = new Dictionary<string, CatalogBundleRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, inner, hit) in regions)
        {
            var outer = ReadOuterKey(data, hit);
            if (outer is null) outer = FindNearbyOuterKey(data, name); // core.py 启发式兜底
            if (TryReadCrcSize(data, hit, layout, out var crc, out var size))
                records[inner] = new CatalogBundleRecord(name, inner, outer, crc, size);
            else
                records[inner] = new CatalogBundleRecord(name, inner, outer, null, null);
        }
        return new CatalogFileService(filePath ?? string.Empty, names.ToList(), records);
    }

    public CatalogBundleRecord? FindByInnerHash(string innerHash)
    {
        if (string.IsNullOrWhiteSpace(innerHash) || innerHash.Length != 32) return null;
        return RecordsByInnerHash.TryGetValue(innerHash.ToLowerInvariant(), out var record) ? record : null;
    }

    /// <summary>读取记录区 +0x10 起的长度前缀外层键（staticmod.py 布局）；
    /// 布局不可信时返回 null。</summary>
    private static string? ReadOuterKey(byte[] data, int hit)
    {
        if (hit + 0x14 > data.Length) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + 0x10, 4));
        if (length is < 16 or > 64 || hit + 0x14 + length > data.Length) return null;
        var candidate = Encoding.ASCII.GetString(data, hit + 0x14, (int)length);
        return Regex.IsMatch(candidate, "^[0-9a-f]{32}$") ? candidate : null;
    }

    /// <summary>按候选布局读取 CRC32 与文件大小；值不合理（大小越界）返回 false。
    /// 大小合理范围放宽（staticmod 只针对 static bundle 用 10 万..5000 万）。</summary>
    private static bool TryReadCrcSize(byte[] data, int hit, (uint CrcOffset, uint SizeOffset) layout, out uint crc, out uint size)
    {
        crc = 0;
        size = 0;
        var end = hit + (int)Math.Max(layout.CrcOffset, layout.SizeOffset) + 4;
        if (end > data.Length) return false;
        crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + (int)layout.CrcOffset, 4));
        size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + (int)layout.SizeOffset, 4));
        return size is >= 1024 and <= 500_000_000;
    }

    private static string? FindNearbyOuterKey(byte[] data, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var index = IndexOf(data, nameBytes, 0);
        if (index < 0) return null;
        var window = data.AsSpan(index + nameBytes.Length, Math.Min(200, data.Length - index - nameBytes.Length));
        var match = OuterKeyPattern.Match(Encoding.ASCII.GetString(window));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (match) return i;
        }
        return -1;
    }
}
