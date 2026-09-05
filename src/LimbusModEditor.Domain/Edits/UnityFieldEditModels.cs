using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LimbusModEditor.Domain.Edits;

/// <summary>One versioned field edit on a Unity serialized object. Stored in
/// the project with field type, original value (and its hash), timestamp and
/// author so edits can be audited, diffed and re-applied safely.</summary>
public sealed record UnityFieldEdit(
    string Path,
    string FieldType,
    string? OriginalValue,
    string NewValue,
    string OriginalValueHash,
    DateTimeOffset EditedAt,
    string? Author,
    int Revision);

/// <summary>The versioned set of field edits stored for one asset.</summary>
public sealed record UnityFieldEditSet(int SchemaVersion, IReadOnlyList<UnityFieldEdit> Edits);

/// <summary>
/// Serializes field edit sets. Version 2 is the versioned model above; version
/// 1 was a flat path→value dictionary, still readable for projects saved by
/// older builds and re-encoded on the next save.
/// </summary>
public static class UnityFieldEditSetCodec
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(UnityFieldEditSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return JsonSerializer.Serialize(new EditSetDto(set.SchemaVersion, set.Edits), Options);
    }

    /// <summary>Reads a versioned set; falls back to the legacy flat dictionary.
    /// Returns null when the payload is absent or unreadable.</summary>
    public static UnityFieldEditSet? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<EditSetDto>(json, Options);
            if (dto?.Edits is { Count: > 0 }) return new UnityFieldEditSet(dto.SchemaVersion, dto.Edits);
        }
        catch (JsonException) { }
        try
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options);
            if (legacy is null || legacy.Count == 0) return null;
            var edits = legacy.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new UnityFieldEdit(
                x.Key.Trim(), string.Empty, null, x.Value ?? string.Empty,
                HashValue(null), DateTimeOffset.MinValue, null, 1)).ToArray();
            return new UnityFieldEditSet(1, edits);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Flattens a set into the path→value map the format backend
    /// applies. Duplicate paths keep the newest edit.</summary>
    public static IReadOnlyDictionary<string, string> ToPathValueMap(UnityFieldEditSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edit in set.Edits)
        {
            if (string.IsNullOrWhiteSpace(edit.Path)) continue;
            map[edit.Path.Trim()] = edit.NewValue;
        }
        return map;
    }

    public static string HashValue(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record EditSetDto(int SchemaVersion, IReadOnlyList<UnityFieldEdit> Edits);
}
