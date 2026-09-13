using System.Diagnostics;
using System.Security.Cryptography;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public async Task<DebugApplySession> ApplyAsync(ModProject project, string overlayDirectory, string gameDirectory, CancellationToken cancellationToken = default)
    {
        using var scope = Log.Scope("铺盘调试文件（overlay → 游戏目录）");
        var overlay = Path.GetFullPath(overlayDirectory);
        var game = Path.GetFullPath(gameDirectory);
        Log.Info("铺盘开始：模组 {0}，游戏目录 {1}，源目录 {2}，取消已请求 {3}",
            project.Name ?? "-", game, overlay, cancellationToken.IsCancellationRequested);
        if (!Directory.Exists(overlay)) throw new DirectoryNotFoundException(overlay);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException(game);
        var backupRoot = Path.Combine(projectDirectory(project), "backups");
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var suffix = 1;
        while (Directory.Exists(backup)) backup = Path.Combine(backupRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{suffix++}");
        Directory.CreateDirectory(backup);
        Log.Debug("铺盘备份目录已确定：{0}", backup);
        var session = new DebugApplySession { BackupDirectory = backup };
        try
        {
            var files = Directory.EnumerateFiles(overlay, "*", SearchOption.AllDirectories).ToList();
            Log.Debug("铺盘文件枚举完成：{0} 个文件，源目录 {1}", files.Count, overlay);
            var processed = 0;
            foreach (var source in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                if (cancellationToken.CanBeCanceled && processed % 50 == 1)
                    Log.Debug("正在处理第 {0}/{1} 个文件：{2}（取消只会在文件之间响应）",
                        processed, files.Count, source);
                var relative = Path.GetRelativePath(overlay, source);
                var target = SafeCombine(game, relative);
                var backupPath = SafeCombine(backup, relative);
                var existed = File.Exists(target);
                var fileStopwatch = Stopwatch.StartNew();
                string? hash = existed ? await Sha256Async(target, cancellationToken) : null;
                session.Changes.Add(new(source, target, backupPath, existed, hash));
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(target, backupPath, true);
                    if (Log.IsDebugEnabled)
                        Log.Debug("已备份原文件：{0} → {1}，{2} 字节", target, backupPath, FileLengthOrMinusOne(backupPath));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await LimbusModEditor.Application.Build.AtomicOutput.CopyAsync(source, target, cancellationToken);
                var appliedHash = await Sha256Async(target, cancellationToken);
                var changeIndex = session.Changes.Count - 1;
                session.Changes[changeIndex] = session.Changes[changeIndex] with { AppliedHash = appliedHash };
                if (Log.IsDebugEnabled)
                    Log.Debug("铺盘文件完成：{0} → {1}，{2} 字节，耗时 {3} ms",
                        source, target, FileLengthOrMinusOne(target), fileStopwatch.ElapsedMilliseconds);
            }
            session.IsApplied = true;
            Log.Info("铺盘完成：备份目录 {0}，共 {1} 个文件，其中覆盖 {2} 个、新增 {3} 个",
                backup, session.Changes.Count,
                session.Changes.Count(x => x.ExistsBefore), session.Changes.Count(x => !x.ExistsBefore));
            return session;
        }
        catch (OperationCanceledException ex)
        {
            Log.Info("铺盘已取消：备份目录 {0}，已记录 {1} 个文件，准备回滚（{2}）", backup, session.Changes.Count, ex.Message);
            // Rollback is a safety operation: it must complete even when the
            // user cancelled the forward copy operation.
            try { await RestoreAsync(session, CancellationToken.None); } catch (Exception rollbackEx) { Log.Error(rollbackEx, "铺盘取消后的回滚失败：备份目录 {0}", backup); }
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "铺盘失败：游戏目录 {0}，备份目录 {1}，已处理 {2} 个条目", game, backup, session.Changes.Count);
            // Rollback is a safety operation: it must complete even when the
            // user cancelled the forward copy operation.
            try { await RestoreAsync(session, CancellationToken.None); } catch (Exception rollbackEx) { Log.Error(rollbackEx, "铺盘失败后的回滚失败：备份目录 {0}", backup); }
            throw;
        }
    }

    public async Task RestoreAsync(DebugApplySession session, CancellationToken cancellationToken = default)
    {
        using var scope = Log.Scope("回滚铺盘（按备份还原）");
        Log.Info("铺盘回滚开始：备份目录 {0}，条目 {1}，取消已请求 {2}",
            session.BackupDirectory, session.Changes.Count, cancellationToken.IsCancellationRequested);
        var restored = 0;
        var removed = 0;
        foreach (var change in session.Changes.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.AppliedHash is not null && File.Exists(change.TargetPath))
            {
                var currentHash = await Sha256Async(change.TargetPath, cancellationToken);
                if (!string.Equals(currentHash, change.AppliedHash, StringComparison.OrdinalIgnoreCase))
                {
                    session.RestoreConflicts.Add(change.TargetPath);
                    Log.Warn("回滚冲突：目标已被外部修改，保留现状不覆盖（目标 {0}，当前 {1}，铺盘时 {2}）",
                        change.TargetPath, currentHash, change.AppliedHash);
                    continue;
                }
            }
            var fileStopwatch = Stopwatch.StartNew();
            if (change.ExistsBefore && File.Exists(change.BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.TargetPath)!);
                File.Copy(change.BackupPath, change.TargetPath, true);
                restored++;
                if (Log.IsDebugEnabled)
                    Log.Debug("已还原原文件：{0} → {1}，{2} 字节，耗时 {3} ms",
                        change.BackupPath, change.TargetPath, FileLengthOrMinusOne(change.TargetPath), fileStopwatch.ElapsedMilliseconds);
            }
            else if (!change.ExistsBefore && File.Exists(change.TargetPath))
            {
                File.Delete(change.TargetPath);
                removed++;
                if (Log.IsDebugEnabled)
                    Log.Debug("已删除铺盘新增的文件：{0}，耗时 {1} ms", change.TargetPath, fileStopwatch.ElapsedMilliseconds);
            }
            else
            {
                Log.Warn("回滚跳过条目：目标与备份都不在预期状态（目标 {0}，改动前存在 {1}，备份存在 {2}）",
                    change.TargetPath, change.ExistsBefore, File.Exists(change.BackupPath));
            }
        }
        session.IsApplied = false;
        Log.Info("铺盘回滚完成：还原 {0} 个文件、删除 {1} 个新增文件、冲突 {2} 个（备份目录 {3}）",
            restored, removed, session.RestoreConflicts.Count, session.BackupDirectory);
    }

    private static string projectDirectory(ModProject project)
    {
        if (string.IsNullOrWhiteSpace(project.SourceDirectory))
        {
            var temp = Path.Combine(Path.GetTempPath(), "LimbusModEditor");
            Log.Warn("备份位置回退：模组 {0} 没有源目录（SourceDirectory 为空），原本想用 <项目>/backups，实际改用临时目录 {1}",
                project.Name ?? "-", temp);
            return temp;
        }
        var source = Path.GetFullPath(project.SourceDirectory);
        // ProjectService stores imported sources in <project>/sources. Keep
        // backups beside that folder, never inside the immutable source copy.
        var directory = string.Equals(Path.GetFileName(source), "sources", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(source)!.FullName
            : source;
        Log.Debug("备份位置：{0}（模组 {1}，源目录 {2}）", directory, project.Name ?? "-", source);
        return directory;
    }
    private static string SafeCombine(string root, string relative) { var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var result = Path.GetFullPath(Path.Combine(fullRoot, relative)); if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出范围"); return result; }
    private static async Task<string> Sha256Async(string path, CancellationToken token) { await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, token); return Convert.ToHexString(hash); }

    /// <summary>只给日志用的文件大小探测：失败返回 -1，绝不影响铺盘流程。</summary>
    private static long FileLengthOrMinusOne(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }
}
