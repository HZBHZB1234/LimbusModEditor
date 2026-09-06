using System.IO;

namespace LimbusModEditor.Application.Debugging;

/// <summary>One installed mod entry inside the loader's mods root directory
/// (LCTA launcher convention: flat files or one directory per mod, with a
/// "_disable" suffix toggling the entry).</summary>
public sealed record InstalledModEntry(string Name, string FullPath, bool IsDirectory, bool IsDisabled)
{
    public string EnabledName => IsDisabled
        ? Name[..^"_disable".Length]
        : Name;
}

/// <summary>Read-only listing plus toggle for the mods root directory the real
/// loader manages. Renaming is the loader's own disable mechanism — no file
/// content is ever modified.</summary>
public static class ModInstallService
{
    public static IReadOnlyList<InstalledModEntry> ListInstalled(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        if (!Directory.Exists(modsDirectory))
            throw new DirectoryNotFoundException($"模组目录不存在: {modsDirectory}");
        var entries = new List<InstalledModEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(modsDirectory))
        {
            var name = Path.GetFileName(path);
            if (name is null) continue;
            var isDirectory = Directory.Exists(path);
            entries.Add(new InstalledModEntry(name, path, isDirectory, IsDisabled(name)));
        }
        return entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static InstalledModEntry SetEnabled(string modsDirectory, InstalledModEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var current = entry.IsDisabled ? entry.EnabledName + "_disable" : entry.Name;
        if (!string.Equals(entry.Name, current, StringComparison.Ordinal))
            throw new InvalidOperationException("模组条目状态与名称不一致。");
        var targetName = enabled ? entry.EnabledName : entry.EnabledName + "_disable";
        var targetPath = Path.Combine(modsDirectory, targetName);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new InvalidOperationException($"目标名称已存在，无法切换: {targetName}");
        var sourcePath = Path.Combine(modsDirectory, current);
        if (entry.IsDirectory) Directory.Move(sourcePath, targetPath);
        else File.Move(sourcePath, targetPath);
        return new InstalledModEntry(targetName, targetPath, entry.IsDirectory, !enabled);
    }

    private static bool IsDisabled(string name) => name.EndsWith("_disable", StringComparison.Ordinal);
}
