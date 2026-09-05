using System.IO.Compression;
using System.Security.Cryptography;

namespace LimbusModEditor.Formats.Lunartique;

public sealed record LunartiqueResource(string RelativePath, byte[] Uninstallation, byte[] Installation);

public sealed class LunartiquePackage
{
    public string Root { get; init; } = string.Empty;
    public List<LunartiqueResource> Resources { get; } = [];
    public List<(string Path, byte[] Data)> PreservedFiles { get; } = [];
}

public static class LunartiqueArchive
{
    public static void Write(LunartiquePackage package, Stream output, bool preserveUnknownFiles = true)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(output);
        var root = string.IsNullOrWhiteSpace(package.Root) ? "LunartiqueMod" : package.Root.Trim('/');
        EnsureSafePath(root);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in package.Resources.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            EnsureSafePath(resource.RelativePath);
            WriteEntry(archive, $"{root}/Uninstallation/{resource.RelativePath}/__data", resource.Uninstallation, paths);
            WriteEntry(archive, $"{root}/Installation/{resource.RelativePath}/__data", resource.Installation, paths);
        }
        if (preserveUnknownFiles)
            foreach (var (path, data) in package.PreservedFiles)
            {
                EnsureSafePath(path);
                WriteEntry(archive, path, data, paths);
            }
    }

    public static bool IsMatch(Stream input)
    {
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var names = archive.Entries.Select(x => x.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return FindRoot(names) is not null;
        }
        catch (InvalidDataException) { return false; }
    }

    public static LunartiquePackage Read(Stream input, bool preserveUnknownFiles = true)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var names = archive.Entries.Select(x => x.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = FindRoot(names) ?? throw new InvalidDataException("未找到 Lunartique Installation/Uninstallation 根目录。");
        var package = new LunartiquePackage { Root = root };
        var uninstallation = archive.Entries.Where(x => IsDataFile(x.FullName, root, "Uninstallation"))
            .ToDictionary(x => RelativeDataPath(x.FullName, root, "Uninstallation"), ReadEntry, StringComparer.OrdinalIgnoreCase);
        var installation = archive.Entries.Where(x => IsDataFile(x.FullName, root, "Installation"))
            .ToDictionary(x => RelativeDataPath(x.FullName, root, "Installation"), ReadEntry, StringComparer.OrdinalIgnoreCase);
        foreach (var path in uninstallation.Keys.Union(installation.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
        {
            // Installation-only entries represent added assets; an
            // Uninstallation-only entry represents a deletion. Keep both in
            // the neutral model so authoring does not silently lose them.
            var before = uninstallation.TryGetValue(path, out var oldData) ? oldData : [];
            var after = installation.TryGetValue(path, out var newData) ? newData : [];
            package.Resources.Add(new(path, before, after));
        }
        if (package.Resources.Count == 0)
            throw new InvalidDataException("Lunartique 包中没有 __data 资源。");
        if (preserveUnknownFiles)
            foreach (var entry in archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)))
            {
                var path = entry.FullName.Replace('\\', '/');
                EnsureSafePath(path);
                if (!path.StartsWith(root + "/Uninstallation/", StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(root + "/Installation/", StringComparison.OrdinalIgnoreCase))
                    package.PreservedFiles.Add((path, ReadEntry(entry)));
            }
        return package;
    }

    public static IEnumerable<LunartiqueResource> ChangedResources(LunartiquePackage package)
        => package.Resources.Where(x => !SHA256.HashData(x.Uninstallation).AsSpan().SequenceEqual(SHA256.HashData(x.Installation)));

    private static string? FindRoot(HashSet<string> names)
    {
        var prefixes = names.SelectMany(name =>
        {
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? [] : Enumerable.Range(1, parts.Length).Select(i => string.Join('/', parts.Take(i)));
        }).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in prefixes)
            if (names.Any(x => x.StartsWith(candidate + "/Installation/", StringComparison.OrdinalIgnoreCase)) &&
                names.Any(x => x.StartsWith(candidate + "/Uninstallation/", StringComparison.OrdinalIgnoreCase)))
                return candidate;
        return null;
    }

    private static bool IsDataFile(string path, string root, string folder) => path.Replace('\\', '/').StartsWith(root + "/" + folder + "/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/__data", StringComparison.OrdinalIgnoreCase);
    private static string RelativeDataPath(string path, string root, string folder) => path.Replace('\\', '/').Substring((root + "/" + folder + "/").Length).Replace("/__data", "", StringComparison.OrdinalIgnoreCase);
    private static byte[] ReadEntry(ZipArchiveEntry entry) { using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
    private static void WriteEntry(ZipArchive archive, string path, byte[] data, HashSet<string> paths)
    {
        EnsureSafePath(path);
        if (!paths.Add(path)) throw new InvalidDataException($"Lunartique 包含重复路径: {path}");
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(data);
    }
    private static void EnsureSafePath(string path) { if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is "" or ".." or ".")) throw new InvalidDataException($"Lunartique 包含非法路径: {path}"); }
}
