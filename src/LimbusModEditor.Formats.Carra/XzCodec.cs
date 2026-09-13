using System.Diagnostics;
using System.Runtime.InteropServices;
using Joveler.Compression.XZ;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public bool CanEncode => false;
    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        Log.Error("XZ 编码失败：当前环境没有可用的 XZ/LZMA2 编码器（输入 {0} 字节）", data.Length);
        throw new NotSupportedException("当前环境没有可用的 XZ/LZMA2 编码器，无法写回 Carra 对象。");
    }
}

/// <summary>
/// XZ/LZMA2 encoder backed by the native liblzma distribution shipped by
/// Joveler.Compression.XZ.  Keeping this adapter behind IXzCodec means the
/// archive layer remains testable and callers can still provide an enterprise
/// codec when their deployment policy requires one.
/// </summary>
public sealed class JovelerXzCodec : IXzCodec
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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
        Log.Debug("XZ 编码器就绪：级别 {0}，校验 {1}，极致模式 {2}，liblzma 初始化 {3}",
            level, check, extreme, NativeInitialized ? "成功" : "失败");
    }

    public bool CanEncode => NativeInitialized &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (!CanEncode)
        {
            Log.Error("XZ 编码失败：当前平台没有可用的 liblzma 运行时（输入 {0} 字节，RID {1}）",
                data.Length, RuntimeInformation.RuntimeIdentifier);
            throw new PlatformNotSupportedException("当前平台没有可用的 liblzma 运行时。");
        }

        Log.Debug("XZ 压缩开始：输入 {0} 字节，级别 {1}，校验 {2}，极致模式 {3}", data.Length, _level, _check, _extreme);
        var start = Stopwatch.GetTimestamp();
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
        var result = output.ToArray();
        Log.Debug("XZ 压缩完成：输入 {0} 字节 → 输出 {1} 字节，耗时 {2} ms",
            data.Length, result.Length, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
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
            Log.Debug("查找 liblzma 原生库：基准目录 {0}，候选 {1} 个", basePath, names.Length);
            foreach (var name in names)
            {
                var path = Path.IsPathRooted(name) ? name : Path.Combine(basePath, name);
                if (!File.Exists(path))
                {
                    if (Log.IsTraceEnabled) Log.Trace("候选 liblzma 不存在：{0}", path);
                    continue;
                }
                Log.Debug("使用 liblzma 原生库：{0}", path);
                XZInit.GlobalInit(path);
                return true;
            }
            // Native search paths are useful for published/self-contained
            // deployments where the loader has already registered the RID.
            Log.Debug("未在基准目录找到 liblzma，改用系统加载器搜索路径");
            XZInit.GlobalInit();
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Log.Error(ex, "liblzma 原生库初始化失败，XZ 编码不可用（基准目录 {0}）", AppContext.BaseDirectory);
            return false;
        }
    }
}

public static class CarraCodec
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static byte[] EncodeOrThrow(ReadOnlySpan<byte> data, IXzCodec? codec)
    {
        if (codec is null || !codec.CanEncode)
        {
            Log.Error("Carra 对象压缩不可用：编码器 {0}（输入 {1} 字节）",
                codec is null ? "未配置" : "不具备编码能力", data.Length);
            throw new NotSupportedException("Carra 修改对象需要配置 IXzCodec（XZ/LZMA2 编码器）。");
        }
        return codec.Encode(data);
    }
}
