using System.Security.Cryptography;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Debugging;

public sealed record DebugFileChange(string SourcePath, string TargetPath, string BackupPath, bool ExistsBefore, string? BeforeHash, string? AppliedHash = null);
public sealed class DebugApplySession
{
    public string BackupDirectory { get; init; } = string.Empty;
    public List<DebugFileChange> Changes { get; } = [];
    public List<string> RestoreConflicts { get; } = [];
    public bool IsApplied { get; internal set; }
}

public sealed class DebugApplyService
{
    public async Task<DebugApplySession> ApplyAsync(ModProject project, string overlayDirectory, string gameDirectory, CancellationToken cancellationToken = default)
    {
        var overlay = Path.GetFullPath(overlayDirectory);
        var game = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(overlay)) throw new DirectoryNotFoundException(overlay);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException(game);
        var backupRoot = Path.Combine(projectDirectory(project), "backups");
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var suffix = 1;
        while (Directory.Exists(backup)) backup = Path.Combine(backupRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{suffix++}");
        Directory.CreateDirectory(backup);
        var session = new DebugApplySession { BackupDirectory = backup };
        try
        {
            foreach (var source in Directory.EnumerateFiles(overlay, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(overlay, source);
                var target = SafeCombine(game, relative);
                var backupPath = SafeCombine(backup, relative);
                var existed = File.Exists(target);
                string? hash = existed ? await Sha256Async(target, cancellationToken) : null;
                session.Changes.Add(new(source, target, backupPath, existed, hash));
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!); File.Copy(target, backupPath, true); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await LimbusModEditor.Application.Build.AtomicOutput.CopyAsync(source, target, cancellationToken);
                var appliedHash = await Sha256Async(target, cancellationToken);
                var changeIndex = session.Changes.Count - 1;
                session.Changes[changeIndex] = session.Changes[changeIndex] with { AppliedHash = appliedHash };
            }
            session.IsApplied = true;
            return session;
        }
        catch
        {
            // Rollback is a safety operation: it must complete even when the
            // user cancelled the forward copy operation.
            try { await RestoreAsync(session, CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task RestoreAsync(DebugApplySession session, CancellationToken cancellationToken = default)
    {
        foreach (var change in session.Changes.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.AppliedHash is not null && File.Exists(change.TargetPath))
            {
                var currentHash = await Sha256Async(change.TargetPath, cancellationToken);
                if (!string.Equals(currentHash, change.AppliedHash, StringComparison.OrdinalIgnoreCase))
                {
                    session.RestoreConflicts.Add(change.TargetPath);
                    continue;
                }
            }
            if (change.ExistsBefore && File.Exists(change.BackupPath)) { Directory.CreateDirectory(Path.GetDirectoryName(change.TargetPath)!); File.Copy(change.BackupPath, change.TargetPath, true); }
            else if (!change.ExistsBefore && File.Exists(change.TargetPath)) File.Delete(change.TargetPath);
        }
        session.IsApplied = false;
    }

    private static string projectDirectory(ModProject project)
    {
        if (string.IsNullOrWhiteSpace(project.SourceDirectory))
            return Path.Combine(Path.GetTempPath(), "LimbusModEditor");
        var source = Path.GetFullPath(project.SourceDirectory);
        // ProjectService stores imported sources in <project>/sources. Keep
        // backups beside that folder, never inside the immutable source copy.
        return string.Equals(Path.GetFileName(source), "sources", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(source)!.FullName
            : source;
    }
    private static string SafeCombine(string root, string relative) { var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var result = Path.GetFullPath(Path.Combine(fullRoot, relative)); if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出范围"); return result; }
    private static async Task<string> Sha256Async(string path, CancellationToken token) { await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, token); return Convert.ToHexString(hash); }
}
