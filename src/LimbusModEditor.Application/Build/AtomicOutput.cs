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
        try
        {
            await using (var stream = File.Create(temp))
            {
                await write(stream, cancellationToken);
            }
            MoveOntoTarget(temp, target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        await using var source = File.OpenRead(sourcePath);
        await WriteAsync(targetPath, async (stream, token) => await source.CopyToAsync(stream, token), cancellationToken);
    }

    private static void MoveOntoTarget(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070005))
        {
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
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-6)) TryDelete(stale);
            }
        }
        catch (Exception) { /* stale cleanup is best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* leaving a recoverable temp is acceptable */ }
    }
}
