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

    private static string BuildFingerprint(string directory)
    {
        var parts = new List<string>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
                parts.Add($"{Path.GetFileName(file)}:{new FileInfo(file).Length}:{File.GetLastWriteTimeUtc(file).Ticks}");
        parts.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts))));
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
