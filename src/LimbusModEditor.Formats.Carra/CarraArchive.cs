using System.IO.Compression;
using System.Text.RegularExpressions;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using NLog;

namespace LimbusModEditor.Formats.Carra;

public sealed class CarraArchive
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly Regex EntryPattern = new(@"^(?<account>[^/\\]+)/(?<bundle>[^/\\]+)/(?<id>-?\d+)(?:\.(?<type>\d+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FormatProbeResult Probe(Stream input, string? fileName = null)
    {
        using var scope = Log.Scope("Carra 探测");
        Log.Debug("Carra 格式探测开始：{0}", fileName ?? "-");
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
            var matches = entries.Count(x => EntryPattern.IsMatch(x.FullName.Replace('\\', '/')));
            var isMatch = entries.Count > 0 && matches > 0;
            Log.Debug("Carra 探测判定：ZIP 条目 {0} 个，其中匹配对象路径 {1} 个 → {2}",
                entries.Count, matches, isMatch ? "命中 Carra2" : "未命中");
            return new(ModFormatKind.Carra2, isMatch, isMatch ? Math.Min(100, 60 + matches * 5) : 0,
                isMatch ? $"检测到 Carra 对象条目 ({matches})" : "未检测到 Carra 对象路径", []);
        }
        catch (InvalidDataException ex)
        {
            Log.Error(ex, "Carra 探测失败：不是有效 ZIP 容器（{0}）", fileName ?? "-");
            return new(ModFormatKind.Carra2, false, 0, "不是有效 ZIP 容器", [new(DiagnosticSeverity.Error, "CARRA_ZIP", ex.Message)]);
        }
    }

    public static CarraPackage Read(Stream input, bool preserveUnknownFiles = true)
    {
        using var scope = Log.Scope("Carra 读取");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var package = new CarraPackage();
        var entryIndex = 0L;
        var unmatched = 0;
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            entryIndex++;
            var path = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            EnsureSafePath(path);
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            totalBytes += buffer.Length;
            Log.Every(entryIndex, 200, LogLevel.Debug, () => $"Carra 读取进度：第 {entryIndex} 个条目 {path}（{buffer.Length} 字节）");
            if (Log.IsTraceEnabled) Log.Trace("Carra 条目：{0}，压缩后 {1} 字节", path, buffer.Length);
            var match = EntryPattern.Match(path);
            if (match.Success)
            {
                var type = match.Groups["type"].Success ? int.Parse(match.Groups["type"].Value) : (int?)null;
                package.Entries.Add(new CarraEntry(new(match.Groups["account"].Value, match.Groups["bundle"].Value,
                    long.Parse(match.Groups["id"].Value), type), buffer.ToArray(), path));
            }
            else if (preserveUnknownFiles)
                package.UnknownFiles.Add((path, buffer.ToArray()));
            else
            {
                unmatched++;
                Log.Warn("Carra 条目路径不匹配对象命名规则，且未保留未知文件：{0}", path);
            }
        }
        Log.Debug("Carra 读取完成：对象条目 {0} 个，未知文件 {1} 个，跳过路径不匹配 {2} 个，压缩数据合计 {3} 字节",
            package.Entries.Count, package.UnknownFiles.Count, unmatched, totalBytes);
        return package;
    }

    public static void Write(CarraPackage package, Stream output, bool preserveUnknownFiles = true, Func<byte[], byte[]>? compress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(output);
        using var scope = Log.Scope("Carra 写入");
        Log.Info("Carra 归档写入开始：对象条目 {0} 个，未知文件 {1} 个，保留未知文件 {2}，压缩器 {3}",
            package.Entries.Count, package.UnknownFiles.Count, preserveUnknownFiles, compress is null ? "缺失" : "已提供");
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var entryIndex = 0L;
        var recompressed = 0L;
        foreach (var entry in package.Entries.OrderBy(x => x.SourcePath, StringComparer.Ordinal))
        {
            EnsureSafePath(entry.SourcePath);
            if (!paths.Add(entry.SourcePath))
            {
                Log.Warn("Carra 写入中断：包含重复路径 {0}", entry.SourcePath);
                throw new InvalidDataException($"Carra 包含重复路径: {entry.SourcePath}");
            }
            var data = entry.ModifiedData is null
                ? entry.CompressedData
                : !entry.IsXzCompressed && !entry.RequiresXzCompression
                    ? entry.ModifiedData
                    : compress is null
                        ? throw new NotSupportedException("修改后的 Carra 对象需要提供 XZ 压缩器。")
                        : compress(entry.ModifiedData);
            var zip = archive.CreateEntry(entry.SourcePath, CompressionLevel.NoCompression);
            using var target = zip.Open();
            target.Write(data);
            entryIndex++;
            if (entry.ModifiedData is not null && (entry.IsXzCompressed || entry.RequiresXzCompression)) recompressed++;
            Log.Every(entryIndex, 200, LogLevel.Debug,
                () => $"Carra 写入进度：第 {entryIndex} 个条目 {entry.SourcePath}（写出 {data.Length} 字节）");
        }
        var unknownIndex = 0L;
        if (preserveUnknownFiles)
            foreach (var (path, data) in package.UnknownFiles)
            {
                EnsureSafePath(path);
                if (!paths.Add(path))
                {
                    Log.Warn("Carra 写入中断：未知文件包含重复路径 {0}", path);
                    throw new InvalidDataException($"Carra 包含重复路径: {path}");
                }
                var zip = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var target = zip.Open();
                target.Write(data);
                unknownIndex++;
                Log.Every(unknownIndex, 200, LogLevel.Debug, () => $"Carra 写入进度（未知文件）：第 {unknownIndex} 个 {path}（写出 {data.Length} 字节）");
            }
        Log.Info("Carra 归档写入完成：写出对象条目 {0} 个（其中重新 XZ 压缩 {1} 个），未知文件 {2} 个", entryIndex, recompressed, unknownIndex);
    }

    private static void EnsureSafePath(string path)
    {
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is ".." or "" or "."))
            throw new InvalidDataException($"Carra 包含非法路径: {path}");
    }
}
