using System.Buffers.Binary;
using System.Text;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Unity;

public sealed record UnityBundleInfo(string Signature, int FormatVersion, string UnityVersion, long DeclaredSize, bool IsValid);

public static class UnityBundleInspector
{
    public static UnityBundleInfo? Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return null;
        var signature = ReadCString(data, 0, 8);
        if (signature is not ("UnityFS" or "UnityRaw" or "UnityWeb")) return null;
        var version = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        var unityVersionStart = 12;
        var (unityVersion, next) = ReadCStringWithEnd(data, unityVersionStart);
        var (_, headerEnd) = ReadCStringWithEnd(data, next);
        var size = headerEnd + 8 <= data.Length ? BinaryPrimitives.ReadInt64BigEndian(data[headerEnd..]) : data.Length;
        return new UnityBundleInfo(signature, version, unityVersion, size, true);
    }

    public static FormatProbeResult Probe(ReadOnlySpan<byte> data)
    {
        var info = Inspect(data);
        return info is null
            ? new(ModFormatKind.Directory, false, 0, "不是 Unity AssetBundle", [])
            : new(ModFormatKind.Directory, true, 90, $"Unity {info.Signature} v{info.FormatVersion} ({info.UnityVersion})", []);
    }

    private static string ReadCString(ReadOnlySpan<byte> data, int offset, int maxLength)
    {
        var end = offset;
        var limit = Math.Min(data.Length, offset + maxLength);
        while (end < limit && data[end] != 0) end++;
        return Encoding.UTF8.GetString(data[offset..end]);
    }

    private static (string Value, int Next) ReadCStringWithEnd(ReadOnlySpan<byte> data, int offset)
    {
        var end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return (Encoding.UTF8.GetString(data[offset..end]), Math.Min(data.Length, end + 1));
    }
}
