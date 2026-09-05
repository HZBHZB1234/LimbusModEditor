using Joveler.Compression.XZ;

namespace LimbusModEditor.Formats.Carra;

public interface IXzCodec
{
    bool CanEncode { get; }
    byte[] Encode(ReadOnlySpan<byte> data);
}

/// <summary>Explicit fallback used when no XZ encoder is installed. Reads are
/// available through SharpCompress, but writes must not silently emit invalid
/// Carra objects.</summary>
public sealed class UnavailableXzCodec : IXzCodec
{
    public bool CanEncode => false;
    public byte[] Encode(ReadOnlySpan<byte> data)
        => throw new NotSupportedException("当前环境没有可用的 XZ/LZMA2 编码器，无法写回 Carra 对象。");
}

/// <summary>
/// XZ/LZMA2 encoder backed by the native liblzma distribution shipped by
/// Joveler.Compression.XZ.  Keeping this adapter behind IXzCodec means the
/// archive layer remains testable and callers can still provide an enterprise
/// codec when their deployment policy requires one.
/// </summary>
public sealed class JovelerXzCodec : IXzCodec
{
    private static readonly bool NativeInitialized = InitializeNative();

    private readonly LzmaCompLevel _level;
    private readonly LzmaCheck _check;
    private readonly bool _extreme;

    public JovelerXzCodec()
        : this(LzmaCompLevel.Level6, LzmaCheck.Crc64, false)
    {
    }

    private JovelerXzCodec(LzmaCompLevel level, LzmaCheck check, bool extreme)
    {
        _level = level;
        _check = check;
        _extreme = extreme;
    }

    public bool CanEncode => NativeInitialized &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (!CanEncode)
            throw new PlatformNotSupportedException("当前平台没有可用的 liblzma 运行时。");

        using var output = new MemoryStream();
        var options = new XZCompressOptions
        {
            Level = _level,
            Check = _check,
            ExtremeFlag = _extreme,
            LeaveOpen = true
        };
        using (var stream = new XZStream(output, options))
        {
            stream.Write(data);
        }
        return output.ToArray();
    }

    private static bool InitializeNative()
    {
        try
        {
            // With a RuntimeIdentifier the native asset is placed under
            // <base>/win-x64 (or the corresponding RID); development hosts
            // may still load managed assemblies from <base>.
            var basePath = AppContext.BaseDirectory;
            var names = OperatingSystem.IsWindows()
                ? new[] { "liblzma.dll", Path.Combine("win-x64", "liblzma.dll") }
                : OperatingSystem.IsLinux()
                    ? new[] { "liblzma.so", Path.Combine("linux-x64", "native", "liblzma.so") }
                    : new[] { "liblzma.dylib", Path.Combine("osx-x64", "native", "liblzma.dylib") };
            foreach (var name in names)
            {
                var path = Path.IsPathRooted(name) ? name : Path.Combine(basePath, name);
                if (!File.Exists(path)) continue;
                XZInit.GlobalInit(path);
                return true;
            }
            // Native search paths are useful for published/self-contained
            // deployments where the loader has already registered the RID.
            XZInit.GlobalInit();
            return true;
        }
        catch (Exception) when (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return false;
        }
    }
}

public static class CarraCodec
{
    public static byte[] EncodeOrThrow(ReadOnlySpan<byte> data, IXzCodec? codec)
    {
        if (codec is null || !codec.CanEncode)
            throw new NotSupportedException("Carra 修改对象需要配置 IXzCodec（XZ/LZMA2 编码器）。");
        return codec.Encode(data);
    }
}
