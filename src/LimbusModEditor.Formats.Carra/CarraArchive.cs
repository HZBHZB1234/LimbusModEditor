using System.IO.Compression;
using System.Text.RegularExpressions;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Carra;

public sealed class CarraArchive
{
    private static readonly Regex EntryPattern = new(@"^(?<account>[^/\\]+)/(?<bundle>[^/\\]+)/(?<id>-?\d+)(?:\.(?<type>\d+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FormatProbeResult Probe(Stream input, string? fileName = null)
    {
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
            var matches = entries.Count(x => EntryPattern.IsMatch(x.FullName.Replace('\\', '/')));
            var isMatch = entries.Count > 0 && matches > 0;
            return new(ModFormatKind.Carra2, isMatch, isMatch ? Math.Min(100, 60 + matches * 5) : 0,
                isMatch ? $"检测到 Carra 对象条目 ({matches})" : "未检测到 Carra 对象路径", []);
        }
        catch (InvalidDataException ex)
        {
            return new(ModFormatKind.Carra2, false, 0, "不是有效 ZIP 容器", [new(DiagnosticSeverity.Error, "CARRA_ZIP", ex.Message)]);
        }
    }

    public static CarraPackage Read(Stream input, bool preserveUnknownFiles = true)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var package = new CarraPackage();
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            EnsureSafePath(path);
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var match = EntryPattern.Match(path);
            if (match.Success)
            {
                var type = match.Groups["type"].Success ? int.Parse(match.Groups["type"].Value) : (int?)null;
                package.Entries.Add(new CarraEntry(new(match.Groups["account"].Value, match.Groups["bundle"].Value,
                    long.Parse(match.Groups["id"].Value), type), buffer.ToArray(), path));
            }
            else if (preserveUnknownFiles)
                package.UnknownFiles.Add((path, buffer.ToArray()));
        }
        return package;
    }

    public static void Write(CarraPackage package, Stream output, bool preserveUnknownFiles = true, Func<byte[], byte[]>? compress = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(output);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in package.Entries.OrderBy(x => x.SourcePath, StringComparer.Ordinal))
        {
            EnsureSafePath(entry.SourcePath);
            if (!paths.Add(entry.SourcePath)) throw new InvalidDataException($"Carra 包含重复路径: {entry.SourcePath}");
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
        }
        if (preserveUnknownFiles)
            foreach (var (path, data) in package.UnknownFiles)
            {
                EnsureSafePath(path);
                if (!paths.Add(path)) throw new InvalidDataException($"Carra 包含重复路径: {path}");
                var zip = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var target = zip.Open();
                target.Write(data);
            }
    }

    private static void EnsureSafePath(string path)
    {
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is ".." or "" or "."))
            throw new InvalidDataException($"Carra 包含非法路径: {path}");
    }
}
