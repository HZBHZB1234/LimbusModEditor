using LimbusModEditor.Application.Build;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P0.2: build outputs must be written through a transaction (temp
/// file + atomic move) that cleans up on success, failure and cancellation and
/// reports locked targets clearly.</summary>
public class AtomicOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicOutputTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Writes_content_atomically()
    {
        var target = Path.Combine(_root, "out.bin");
        await AtomicOutput.WriteAsync(target, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.lme-tmp-*"));
    }

    [Fact]
    public async Task Writer_failure_keeps_previous_target_and_cleans_temp()
    {
        var target = Path.Combine(_root, "out.bin");
        await File.WriteAllBytesAsync(target, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await AtomicOutput.WriteAsync(target, (_, _) => throw new InvalidDataException("坏数据"), CancellationToken.None));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.lme-tmp-*"));
    }

    [Fact]
    public async Task Cancellation_cleans_up_temp()
    {
        var target = Path.Combine(_root, "out.bin");
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await AtomicOutput.WriteAsync(target, (_, token) => throw new OperationCanceledException(token), cts.Token));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_root, "*.lme-tmp-*"));
    }

    [Fact]
    public async Task Locked_target_reports_actionable_error_and_no_temp()
    {
        var target = Path.Combine(_root, "locked.bin");
        await File.WriteAllBytesAsync(target, [1]);
        await using var exclusive = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AtomicOutput.WriteAsync(target, new byte[] { 2 }, CancellationToken.None));
        Assert.Contains("被游戏或其他程序占用", exception.Message);
        Assert.Contains(target, exception.Message);
        Assert.Empty(Directory.GetFiles(_root, "*.lme-tmp-*"));
    }

    [Fact]
    public async Task Copy_moves_source_bytes_through_transaction()
    {
        var source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
        var target = Path.Combine(_root, "nested", "dst.bin");
        await AtomicOutput.CopyAsync(source, target, CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, await File.ReadAllBytesAsync(target));
    }
}
