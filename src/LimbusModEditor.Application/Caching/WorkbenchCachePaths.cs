namespace LimbusModEditor.Application.Caching;

/// <summary>工作台表缓存的三个库（plan-09 §3）。</summary>
public enum WorkbenchCacheKind
{
    /// <summary><c>cache/bank-index.db</c>：音频工作台的 bank 头信息 + 逐样本行（plan-11）。</summary>
    BankIndex,
    /// <summary><c>cache/static-tables.db</c>：静态数据表元数据 + 按需 JSON 文本缓存（plan-12）。</summary>
    StaticTables,
    /// <summary><c>cache/text-index.db</c>：lang 文件键数/大小 + 搜索命中（plan-10）。</summary>
    TextIndex,
}

/// <summary>
/// plan-09：工作台表缓存的文件位置（集中一处，后续计划不各自拼路径）。
///
/// <para>缓存一律落在<b>程序目录的 <c>cache/</c></b>，与既有的
/// <c>cache/unity-cache-index.db</c> 同处（<c>AppEnvironment.CacheDirectory</c>）。
/// <b>绝不写游戏目录 / Unity 缓存 / catalog。</b></para>
/// </summary>
public static class WorkbenchCachePaths
{
    /// <summary>缓存目录名（程序目录下）。</summary>
    public const string CacheDirectoryName = "cache";

    /// <summary>既有扫描索引库文件名（unity-cache-index.json → .db）。</summary>
    public const string UnityCacheIndexFileName = "unity-cache-index.db";

    /// <summary>音频工作台索引库。</summary>
    public const string BankIndexFileName = "bank-index.db";

    /// <summary>静态数据表缓存库。</summary>
    public const string StaticTablesFileName = "static-tables.db";

    /// <summary>lang 文本索引库。</summary>
    public const string TextIndexFileName = "text-index.db";

    /// <summary>程序目录下的缓存目录（= <c>AppEnvironment.CacheDirectory</c>）。</summary>
    public static string CacheDirectory(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        return Path.Combine(Path.GetFullPath(programDirectory), CacheDirectoryName);
    }

    /// <summary>库文件名。</summary>
    public static string FileName(WorkbenchCacheKind kind) => kind switch
    {
        WorkbenchCacheKind.BankIndex => BankIndexFileName,
        WorkbenchCacheKind.StaticTables => StaticTablesFileName,
        WorkbenchCacheKind.TextIndex => TextIndexFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的缓存库种类。"),
    };

    /// <summary>库文件完整路径；<paramref name="cacheDirectory"/> 传
    /// <c>AppEnvironment.CacheDirectory</c>（或 <see cref="CacheDirectory"/> 的结果）。</summary>
    public static string DatabasePath(WorkbenchCacheKind kind, string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        return Path.Combine(Path.GetFullPath(cacheDirectory), FileName(kind));
    }

    /// <summary>三个工作台缓存库的全部路径（清理/诊断入口用）。</summary>
    public static IReadOnlyList<string> AllDatabasePaths(string cacheDirectory)
        => [DatabasePath(WorkbenchCacheKind.BankIndex, cacheDirectory),
            DatabasePath(WorkbenchCacheKind.StaticTables, cacheDirectory),
            DatabasePath(WorkbenchCacheKind.TextIndex, cacheDirectory)];
}
