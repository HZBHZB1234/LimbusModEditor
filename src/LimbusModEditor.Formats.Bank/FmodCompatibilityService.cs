using System.Text.Json;
using System.Text.Json.Serialization;

namespace LimbusModEditor.Formats.Bank;

/// <summary>Cached probe result keyed by a fingerprint of the DLL directory
/// contents, so repeated launches do not re-parse the PE tables.</summary>
public sealed record FmodProbeCache(
    string Fingerprint,
    DateTimeOffset ProbedAt,
    IReadOnlyList<FmodDllReport> Reports);

/// <summary>P2.2: probes the user's FMOD DLL directory and caches the report
/// keyed by file sizes and timestamps. Probing never loads the DLLs.</summary>
public sealed class FmodCompatibilityService
{
    public FmodProbeCache Probe(string fmodDirectory, string? cacheFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fmodDirectory);
        var fingerprint = BuildFingerprint(fmodDirectory);
        if (cacheFilePath is not null)
        {
            var cached = TryRead(cacheFilePath);
            if (cached is not null && cached.Fingerprint == fingerprint) return cached;
        }
        var fresh = new FmodProbeCache(fingerprint, DateTimeOffset.UtcNow, FmodDllInspector.InspectDirectory(fmodDirectory));
        if (cacheFilePath is not null) Write(cacheFilePath, fresh);
        return fresh;
    }

    private const int SniffBytes = 16 * 1024;

    private static string BuildFingerprint(string directory)
    {
        var parts = new List<string>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
                parts.Add($"{Path.GetFileName(file)}:{new FileInfo(file).Length}:{File.GetLastWriteTimeUtc(file).Ticks}:{ContentSniff(file)}");
        parts.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    /// <summary>Hashes the head and tail of the file so a replaced DLL with a
    /// restored timestamp and identical size cannot reuse a stale cache entry
    /// (timestamps alone were not tamper-resistant).</summary>
    private static string ContentSniff(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[Math.Min(SniffBytes, stream.Length)];
            var headRead = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var tail = Array.Empty<byte>();
            var remaining = stream.Length - headRead;
            if (remaining > 0)
            {
                var tailLength = (int)Math.Min(SniffBytes, remaining);
                stream.Seek(-tailLength, SeekOrigin.End);
                tail = new byte[tailLength];
                var tailRead = stream.ReadAtLeast(tail, tailLength, throwOnEndOfStream: false);
                if (tailRead != tailLength) tail = tail[..tailRead];
            }
            var combined = new byte[headRead + tail.Length];
            head.AsSpan(0, headRead).CopyTo(combined);
            tail.CopyTo(combined, headRead);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(combined))[..16];
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static FmodProbeCache? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<FmodProbeCache>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception) { return null; }
    }

    private static void Write(string path, FmodProbeCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (Exception) { /* the cache is best-effort */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
