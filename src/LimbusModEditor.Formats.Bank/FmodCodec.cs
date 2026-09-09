using System.Runtime.InteropServices;

namespace LimbusModEditor.Formats.Bank;

public sealed record FmodLibraryStatus(
    bool IsLoaded,
    string? LoadedPath,
    string? Error,
    int PointerSize);

/// <summary>
/// Runtime loader boundary for the optional FMOD/FSBANK native codecs.
/// Native symbols are intentionally not called until a vendor-compatible
/// adapter is supplied; this keeps the editor usable for inspection without
/// shipping or reverse engineering a codec implementation.
/// </summary>
public sealed class FmodCodecLibrary : IDisposable
{
    private readonly List<(nint Handle, string Path)> _handles = [];

    public FmodLibraryStatus Status => new(_handles.Count > 0, _handles.Count == 0 ? null : string.Join(";", _handles.Select(x => x.Path)), _handles.Count == 0 ? _lastError : null, IntPtr.Size);
    private string? _lastError;

    public bool TryLoad(string directory, out FmodLibraryStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!OperatingSystem.IsWindows())
        {
            _lastError = "FMOD 原生编解码仅支持 Windows。";
            status = Status;
            return false;
        }

        Dispose();
        _lastError = null;
        // 现代游戏随附的 FMOD 运行库常不带位数后缀（fmod.dll / fmodstudio.dll
        // 即 x64），同样导出 FMOD_System_*，一并作为加载候选。
        var candidates = IntPtr.Size == 8
            ? new[] { "fmod64.dll", "fmod.dll", "fsbank64.dll", "libfsbvorbis64.dll", "fmodstudio64.dll", "fmodstudio.dll" }
            : new[] { "fmod.dll", "fsbank.dll", "libfsbvorbis.dll", "fmodstudio.dll" };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                _handles.Add((NativeLibrary.Load(path), path));
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or SEHException)
            {
                _lastError = $"无法加载 {path}: {ex.Message}";
            }
        }

        _lastError ??= $"目录中未找到匹配的 FMOD/FSBANK DLL（{directory}）。";
        status = Status;
        return _handles.Count > 0;
    }

    public bool TryGetExport(string name, out nint address)
    {
        if (_handles.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            address = 0;
            return false;
        }
        foreach (var item in _handles)
            if (NativeLibrary.TryGetExport(item.Handle, name, out address)) return true;
        address = 0;
        return false;
    }

    public bool HasExport(string name) => TryGetExport(name, out _);

    /// <summary>找到导出指定符号的 DLL 路径（版本探测用：FMOD 2.x 的
    /// FMOD_System_Create 需要 headerversion 参数，值取自该 DLL 的文件版本）。</summary>
    public bool TryGetExportOwner(string name, out nint address, out string? path)
    {
        address = 0;
        path = null;
        if (_handles.Count == 0 || string.IsNullOrWhiteSpace(name)) return false;
        foreach (var item in _handles)
        {
            if (!NativeLibrary.TryGetExport(item.Handle, name, out address)) continue;
            path = item.Path;
            return true;
        }
        return false;
    }

    public T? Bind<T>(string name) where T : Delegate
        => TryGetExport(name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    public void Dispose()
    {
        foreach (var item in _handles) try { NativeLibrary.Free(item.Handle); } catch { }
        _handles.Clear();
    }
}

public interface IFmodAudioCodec
{
    bool IsAvailable { get; }
    Task<byte[]> DecodeFsbToWaveAsync(ReadOnlyMemory<byte> fsb, CancellationToken cancellationToken = default);

    /// <summary>解码 FSB 中指定子样本（plan-06：Bank 样本表试听）。默认实现只支持
    /// 索引 0（等价于无参重载）；解码器可以覆写以支持任意子样本。</summary>
    Task<byte[]> DecodeFsbToWaveAsync(ReadOnlyMemory<byte> fsb, int subsoundIndex, CancellationToken cancellationToken = default)
        => subsoundIndex == 0
            ? DecodeFsbToWaveAsync(fsb, cancellationToken)
            : Task.FromException<byte[]>(new NotSupportedException("当前 FMOD 解码器不支持指定子样本索引。"));

    Task<byte[]> EncodeWaveToFsbAsync(ReadOnlyMemory<byte> wave, CancellationToken cancellationToken = default);
}

public sealed class UnavailableFmodAudioCodec : IFmodAudioCodec
{
    public bool IsAvailable => false;
    public Task<byte[]> DecodeFsbToWaveAsync(ReadOnlyMemory<byte> fsb, CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(new NotSupportedException("FMOD/FSBANK DLL 尚未配置，无法解码 FSB。"));
    public Task<byte[]> EncodeWaveToFsbAsync(ReadOnlyMemory<byte> wave, CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(new NotSupportedException("FMOD/FSBANK DLL 尚未配置，无法编码 FSB。"));
}
