using System.IO;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<InstalledModEntry> ListInstalled(string modsDirectory)
    {
        using var scope = Log.Scope("列出已装模组");
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        if (!Directory.Exists(modsDirectory))
        {
            Log.Error("模组目录不存在：{0}", modsDirectory);
            throw new DirectoryNotFoundException($"模组目录不存在: {modsDirectory}");
        }
        Log.Info("列出已装模组：目录 {0}", modsDirectory);
        var entries = new List<InstalledModEntry>();
        var skipped = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(modsDirectory))
        {
            var name = Path.GetFileName(path);
            if (name is null)
            {
                skipped++;
                if (Log.IsTraceEnabled) Log.Trace("跳过无法取名的条目：{0}", path);
                continue;
            }
            var isDirectory = Directory.Exists(path);
            entries.Add(new InstalledModEntry(name, path, isDirectory, IsDisabled(name)));
            if (Log.IsTraceEnabled) Log.Trace("条目：{0}（目录 {1}，已禁用 {2}）", name, isDirectory, IsDisabled(name));
        }
        if (skipped > 0) Log.Debug("列出已装模组：跳过 {0} 个无法取名的条目", skipped);
        var ordered = entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Log.Info("列出已装模组完成：目录 {0}，{1} 个条目（其中已禁用 {2} 个）",
            modsDirectory, ordered.Count, ordered.Count(x => x.IsDisabled));
        return ordered;
    }

    public static InstalledModEntry SetEnabled(string modsDirectory, InstalledModEntry entry, bool enabled)
    {
        using var scope = Log.Scope("切换模组启用状态");
        ArgumentNullException.ThrowIfNull(entry);
        Log.Info("切换模组启用状态：目录 {0}，条目 {1}，目标 enabled={2}", modsDirectory, entry.Name, enabled);
        var current = entry.IsDisabled ? entry.EnabledName + "_disable" : entry.Name;
        if (!string.Equals(entry.Name, current, StringComparison.Ordinal))
        {
            Log.Error("模组条目状态与名称不一致：条目 {0}，按状态推出的名称 {1}", entry.Name, current);
            throw new InvalidOperationException("模组条目状态与名称不一致。");
        }
        var targetName = enabled ? entry.EnabledName : entry.EnabledName + "_disable";
        var targetPath = Path.Combine(modsDirectory, targetName);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            Log.Error("切换模组启用状态失败（目标名称已存在）：{0}", targetName);
            throw new InvalidOperationException($"目标名称已存在，无法切换: {targetName}");
        }
        var sourcePath = Path.Combine(modsDirectory, current);
        Log.Debug("模组重命名开始：{0} → {1}（{2}）", sourcePath, targetPath, entry.IsDirectory ? "目录" : "文件");
        if (entry.IsDirectory) Directory.Move(sourcePath, targetPath);
        else File.Move(sourcePath, targetPath);
        Log.Info("模组重命名完成：{0} → {1}", sourcePath, targetPath);
        return new InstalledModEntry(targetName, targetPath, entry.IsDirectory, !enabled);
    }

    private static bool IsDisabled(string name) => name.EndsWith("_disable", StringComparison.Ordinal);
}
