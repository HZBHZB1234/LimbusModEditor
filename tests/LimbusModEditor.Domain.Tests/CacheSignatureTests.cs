using LimbusModEditor.Application.Caching;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-09：缓存签名（length, mtimeUtcTicks）——相等判定与「签名变 = 重解析」这条唯一失效规则。</summary>
public class CacheSignatureTests : IDisposable
{
    private readonly string _root;

    public CacheSignatureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-sig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    [Fact]
    public void File_signature_tracks_length_and_mtime()
    {
        var path = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(path, new byte[10]);
        var first = CacheSignature.FromFile(path);
        Assert.Equal(10, first.Length);
        Assert.True(first.MTimeUtcTicks > 0);

        // 长度变化 → 签名变化 → 必须重解析
        File.WriteAllBytes(path, new byte[20]);
        var second = CacheSignature.FromFile(path);
        Assert.NotEqual(first, second);
        Assert.False(CacheSignature.Matches(first.Format(), second));

        // 长度不变但 mtime 变 → 仍然要重解析（唯一规则：签名变 = 重解析）
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var third = CacheSignature.FromFile(path);
        Assert.NotEqual(second, third);

        // 同一内容重读 → 签名一致 → 命中缓存
        Assert.True(CacheSignature.Matches(third.Format(), CacheSignature.FromFile(path)));
    }

    [Fact]
    public void Directory_signature_uses_zero_length_and_directory_mtime()
    {
        var directory = Path.Combine(_root, "sub");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "inner.json"), "{}");
        var signature = CacheSignature.FromDirectory(directory);
        Assert.Equal(0, signature.Length);
        Assert.True(signature.MTimeUtcTicks > 0);
        Assert.Equal(signature, CacheSignature.FromPath(directory));
    }

    [Fact]
    public void Format_round_trips()
    {
        var signature = new CacheSignature(123456789, 638_000_000_000_000_000);
        Assert.Equal("123456789:638000000000000000", signature.Format());
        Assert.True(CacheSignature.TryParse(signature.Format(), out var parsed));
        Assert.Equal(signature, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是签名")]
    [InlineData("12:")]
    [InlineData(":34")]
    [InlineData("a:b")]
    [InlineData("1:2:3")]
    public void Unparsable_signature_never_matches(string? stored)
    {
        // 读不出来 = 不知道新鲜度 = 一律重解析（绝不「乐观命中」）。
        Assert.False(CacheSignature.TryParse(stored, out _));
        Assert.False(CacheSignature.Matches(stored, new CacheSignature(1, 2)));
        Assert.False(CacheSignature.Matches(stored, "hash"));
    }

    [Fact]
    public void Text_signature_compares_content_hashes()
    {
        // plan-12 的 source signature 是内容哈希文本，不是 length:mtime
        Assert.True(CacheSignature.Matches("abc123", "abc123"));
        Assert.False(CacheSignature.Matches("abc123", "abc124"));
        Assert.False(CacheSignature.Matches(null, "abc123"));
    }

    [Fact]
    public void TryFromPath_returns_null_for_missing_path()
    {
        Assert.Null(CacheSignature.TryFromPath(Path.Combine(_root, "missing.bin")));
        Assert.Throws<FileNotFoundException>(() => CacheSignature.FromFile(Path.Combine(_root, "missing.bin")));
        Assert.Throws<DirectoryNotFoundException>(() => CacheSignature.FromDirectory(Path.Combine(_root, "missing-dir")));
    }
}
