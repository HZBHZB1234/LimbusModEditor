using System.IO.Compression;
using SharpCompress.Compressors.Xz;

namespace LimbusModEditor.Formats.Carra;

public sealed record CarraObjectKey(string Account, string Bundle, long PathId, int? TypeId)
{
    public string LogicalPath => TypeId is null
        ? $"{Account}/{Bundle}/{PathId}"
        : $"{Account}/{Bundle}/{PathId}.{TypeId}";
}

public sealed class CarraEntry
{
    private static ReadOnlySpan<byte> XzMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0];
    internal CarraEntry(CarraObjectKey key, byte[] compressedData, string sourcePath)
    {
        Key = key;
        CompressedData = compressedData;
        SourcePath = sourcePath;
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
            return CompressedData.ToArray();
        using var input = new MemoryStream(CompressedData, writable: false);
        using var xz = new XZStream(input);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        return output.ToArray();
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
