using System.Diagnostics;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Application.Build;

/// <summary>
/// P0.2: single write-transaction helper for every build output. Content is
/// written to a unique sibling temp file, then atomically moved onto the
/// target, so a crash or cancellation never leaves a half-written mod in
/// place. Temp files are cleaned on success, failure and cancellation, and a
/// target locked by another process (usually the game) produces an explicit,
/// actionable error instead of a raw IOException.
/// </summary>
public static class AtomicOutput
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Writes content through a temp file and moves it onto
    /// <paramref name="targetPath"/>. The temp file is always removed.</summary>
    public static async Task WriteAsync(string targetPath, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("输出路径缺少目录。", nameof(targetPath));
        Directory.CreateDirectory(directory);
        RemoveStaleTemps(directory, Path.GetFileName(target));
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.lme-tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
        using var scope = Log.Scope("原子写出");
        Log.Debug("原子写出开始：目标 {0}，临时文件 {1}", target, temp);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using (var stream = File.Create(temp))
            {
                await write(stream, cancellationToken);
            }
            MoveOntoTarget(temp, target);
            if (Log.IsDebugEnabled)
            {
                stopwatch.Stop();
                var bytes = -1L;
                try { bytes = new FileInfo(target).Length; }
                catch (Exception sizeEx) when (sizeEx is IOException or UnauthorizedAccessException)
                {
                    Log.Debug(sizeEx, "原子写出后读取产物大小失败（不影响已写出的文件）：{0}", target);
                }
                Log.Debug("原子写出成功：目标 {0}，{1} 字节，耗时 {2} ms", target, bytes, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("原子写出已取消：目标 {0}（临时文件将被删除）", target);
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "原子写出失败：目标 {0}，耗时 {1} ms", target, stopwatch.ElapsedMilliseconds);
            TryDelete(temp);
            throw new InvalidOperationException(
                $"无法写出 {target}：{Describe(ex)}（文件可能被游戏或其他程序占用；请关闭游戏或以管理员权限重试。）", ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Byte-array convenience overload.</summary>
    public static Task WriteAsync(string targetPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => WriteAsync(targetPath, async (stream, token) => await stream.WriteAsync(content, token), cancellationToken);

    /// <summary>File-copy convenience overload (used by debug-apply).</summary>
    public static async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        using var scope = Log.Scope("原子复制文件");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var source = File.OpenRead(sourcePath);
            var bytes = source.Length;
            await WriteAsync(targetPath, async (stream, token) => await source.CopyToAsync(stream, token), cancellationToken);
            if (Log.IsDebugEnabled)
                Log.Debug("原子复制完成：{0} → {1}，{2} 字节，耗时 {3} ms",
                    sourcePath, targetPath, bytes, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            Log.Info("原子复制已取消：{0} → {1}", sourcePath, targetPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "原子复制失败：{0} → {1}，耗时 {2} ms", sourcePath, targetPath, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static void MoveOntoTarget(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: true);
            if (Log.IsDebugEnabled) Log.Debug("原子写出替换成功：{0} → {1}", temp, target);
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070005))
        {
            Log.Error(ex, "原子写出替换失败（目标被占用）：{0}", target);
            throw new IOException($"目标被占用: {target}", ex);
        }
    }

    private static string Describe(Exception ex) =>
        ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("被另一", StringComparison.Ordinal)
            ? "目标文件被另一进程占用"
            : ex.Message;

    private static void RemoveStaleTemps(string directory, string targetName)
    {
        try
        {
            var prefix = $".{targetName}.lme-tmp-";
            foreach (var stale in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                // only remove files old enough not to belong to a live write
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-6))
                {
                    if (Log.IsDebugEnabled) Log.Debug("清理上次遗留的临时文件：{0}", stale);
                    TryDelete(stale);
                }
            }
        }
        catch (Exception ex) { /* stale cleanup is best effort */ Log.Debug(ex, "清理临时文件失败（尽力而为，忽略）：{0}", directory); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { /* leaving a recoverable temp is acceptable */ Log.Debug(ex, "删除临时文件失败（保留可恢复的临时文件）：{0}", path); }
    }
}
