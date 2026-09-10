using System.Globalization;

namespace LimbusModEditor.Application.Caching;

/// <summary>
/// plan-09：<c>(length, mtimeUtcTicks)</c> 组合签名——「这份源文件/源目录是否变过」的
/// 唯一判据。
///
/// <para><b>唯一失效规则：签名变 = 重新解析。</b>
/// 长度或修改时间任一不同，就认为源变了，必须重新读取并覆盖缓存里的对应行；
/// 签名相同则直接信任缓存。<b>不缓存任何游戏版本常量、不猜内容</b>——热修/换版本
/// 会自然改变文件的 (length, mtime)，规则只有这一条，没有例外。</para>
///
/// <para>目录签名：目录没有长度概念，<see cref="FromDirectory"/> 用 <c>Length = 0</c> +
/// 目录自身的 mtime；目录里每个文件的新鲜度由各自的文件签名负责（如
/// <c>cache/text-index.db</c> 的 <c>files.size/mtime_ticks</c>）。</para>
///
/// <para>持久化形态是 <see cref="Format"/> 的 <c>"length:mtimeTicks"</c> 文本
/// （存在 <c>index_meta.signature</c> 或各表自己的两列里）。</para>
/// </summary>
public readonly record struct CacheSignature(long Length, long MTimeUtcTicks)
{
    /// <summary>读一个文件的签名（不存在 → <see cref="FileNotFoundException"/>）。</summary>
    public static CacheSignature FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"源文件不存在：{path}", path);
        return new CacheSignature(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>读一个目录的签名（不存在 → <see cref="DirectoryNotFoundException"/>）。
    /// 长度恒为 0——目录大小对新鲜度没有意义。</summary>
    public static CacheSignature FromDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException($"源目录不存在：{path}");
        return new CacheSignature(0, info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>按路径类型自动分派（目录 → <see cref="FromDirectory"/>，其余按文件）。</summary>
    public static CacheSignature FromPath(string path)
        => Directory.Exists(path) ? FromDirectory(path) : FromFile(path);

    /// <summary>非抛出式读取：路径不存在或不可访问时返回 null。</summary>
    public static CacheSignature? TryFromPath(string path)
    {
        try
        {
            return FromPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>持久化文本：<c>"length:mtimeTicks"</c>。</summary>
    public string Format()
        => $"{Length.ToString(CultureInfo.InvariantCulture)}:{MTimeUtcTicks.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>解析 <see cref="Format"/> 的文本；格式不符返回 false（调用方据此判定「需重建」）。</summary>
    public static bool TryParse(string? text, out CacheSignature signature)
    {
        signature = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)) return false;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return false;
        signature = new CacheSignature(length, ticks);
        return true;
    }

    /// <summary>存下来的签名文本是否与当前签名一致（不可解析 = 不一致 = 需要重解析）。</summary>
    public static bool Matches(string? stored, CacheSignature current)
        => TryParse(stored, out var parsed) && parsed == current;

    /// <summary>存下来的签名文本是否与当前签名一致（直接比较文本，供内容哈希类签名使用）。</summary>
    public static bool Matches(string? stored, string current)
        => !string.IsNullOrEmpty(stored) && string.Equals(stored, current, StringComparison.Ordinal);
}
