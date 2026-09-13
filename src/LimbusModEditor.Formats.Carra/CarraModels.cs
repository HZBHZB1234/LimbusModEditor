using System.Diagnostics;
using System.IO.Compression;
using LimbusModEditor.Domain.Diagnostics;
using NLog;
using SharpCompress.Compressors.Xz;

namespace LimbusModEditor.Formats.Carra;

/// <summary>One Carra2 entry key: "<outer>/<bundle>/<pathId>[.<typeIdx>]".
/// Per the real loader (LimbusModLoader patch.py), <paramref name="Account"/>
/// is the Unity cache OUTER key (a 32-hex directory, stable across game
/// versions, unrelated to player accounts), <paramref name="Bundle"/> the
/// inner cache key, and <paramref name="TypeId"/> — when present — the
/// target SerializedFile's TYPE TABLE index, never a global Unity class ID.</summary>
public sealed record CarraObjectKey(string Account, string Bundle, long PathId, int? TypeId)
{
    public string LogicalPath => TypeId is null
        ? $"{Account}/{Bundle}/{PathId}"
        : $"{Account}/{Bundle}/{PathId}.{TypeId}";
}

public sealed class CarraEntry
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static ReadOnlySpan<byte> XzMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0];
    internal CarraEntry(CarraObjectKey key, byte[] compressedData, string sourcePath)
    {
        Key = key;
        CompressedData = compressedData;
        SourcePath = sourcePath;
        Log.Debug("Carra 条目构造：{0}，压缩数据 {1} 字节，XZ {2}",
            sourcePath ?? "-", compressedData?.Length ?? 0, IsXzCompressed);
    }

    public CarraObjectKey Key { get; }
    public string SourcePath { get; }
    public byte[] CompressedData { get; private set; }
    public byte[]? ModifiedData { get; private set; }
    public bool RequiresXzCompression { get; private set; }
    public bool IsXzCompressed => CompressedData.Length >= XzMagic.Length && CompressedData.AsSpan(0, XzMagic.Length).SequenceEqual(XzMagic);

    public byte[] ReadData()
    {
        if (!IsXzCompressed)
        {
            Log.Debug("Carra 条目未 XZ 压缩，直接返回原始数据：{0}，{1} 字节", SourcePath ?? "-", CompressedData.Length);
            return CompressedData.ToArray();
        }
        var start = Stopwatch.GetTimestamp();
        Log.Debug("Carra 条目 XZ 解压开始：{0}，输入 {1} 字节", SourcePath ?? "-", CompressedData.Length);
        using var input = new MemoryStream(CompressedData, writable: false);
        using var xz = new XZStream(input);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        var result = output.ToArray();
        Log.Debug("Carra 条目 XZ 解压完成：{0}，输入 {1} 字节 → 输出 {2} 字节，耗时 {3} ms",
            SourcePath ?? "-", CompressedData.Length, result.Length, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }

    public void ReplaceData(byte[] data) => ModifiedData = data ?? throw new ArgumentNullException(nameof(data));
    public void ReplaceCompressedData(byte[] data) => CompressedData = data ?? throw new ArgumentNullException(nameof(data));

    public static CarraEntry CreateNew(CarraObjectKey key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        return new CarraEntry(key, [], key.LogicalPath)
        {
            ModifiedData = data.ToArray(),
            RequiresXzCompression = true
        };
    }
}

public sealed class CarraPackage
{
    public List<CarraEntry> Entries { get; } = [];
    public List<(string Path, byte[] Data)> UnknownFiles { get; } = [];
    public bool HasModifications => Entries.Any(x => x.ModifiedData is not null);

    public CarraEntry? Find(string logicalPath)
        => Entries.FirstOrDefault(x => string.Equals(x.Key.LogicalPath, logicalPath, StringComparison.Ordinal));

    public void Replace(string logicalPath, byte[] data)
    {
        var entry = Find(logicalPath) ?? throw new KeyNotFoundException($"未找到 Carra 对象: {logicalPath}");
        entry.ReplaceData(data);
    }
}
