using System.Runtime.InteropServices;

namespace LimbusModEditor.Formats.Bank;

/// <summary>Runtime-only FMOD/FSBank adapter. The editor never ships these
/// proprietary DLLs; users provide a compatible installation directory.</summary>
public sealed class NativeFmodAudioCodec : IFmodAudioCodec, IDisposable
{
    private readonly FmodCodecLibrary _library = new();
    private readonly FmodDecodeApi? _decode;
    private readonly FsBankEncodeApi? _encode;

    public NativeFmodAudioCodec(string dllDirectory)
    {
        if (!_library.TryLoad(dllDirectory, out var status))
            throw new DllNotFoundException(status.Error ?? "无法加载 FMOD/FSBank DLL。");
        _decode = FmodDecodeApi.Create(_library);
        _encode = FsBankEncodeApi.Create(_library);
        if (_decode is null && _encode is null) throw new EntryPointNotFoundException("FMOD/FSBank DLL 缺少所需符号。");
    }

    public bool IsAvailable => _decode is not null || _encode is not null;

    public async Task<byte[]> DecodeFsbToWaveAsync(ReadOnlyMemory<byte> fsb, CancellationToken cancellationToken = default)
    {
        if (_decode is null) throw new NotSupportedException("FMOD DLL 未提供完整解码接口。");
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetTempPath(), "lme-fmod-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.fsb");
        try { await File.WriteAllBytesAsync(input, fsb.ToArray(), cancellationToken); return await Task.Run(() => _decode.Decode(input, cancellationToken), cancellationToken); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public async Task<byte[]> EncodeWaveToFsbAsync(ReadOnlyMemory<byte> wave, CancellationToken cancellationToken = default)
    {
        if (_encode is null) throw new NotSupportedException("FSBank DLL 未提供完整编码接口。");
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetTempPath(), "lme-fmod-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.wav"); var output = Path.Combine(root, "output.fsb");
        try { await File.WriteAllBytesAsync(input, wave.ToArray(), cancellationToken); await Task.Run(() => _encode.Encode(input, output, cancellationToken), cancellationToken); return await File.ReadAllBytesAsync(output, cancellationToken); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public void Dispose() => _library.Dispose();

    private sealed class FmodDecodeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSystem(out nint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitSystem(nint value, int channels, uint flags, nint extra);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReleaseSystem(nint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSound(nint system, [MarshalAs(UnmanagedType.LPStr)] string path, uint mode, nint exInfo, out nint sound);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReleaseSound(nint sound);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetNumSubSounds(nint sound, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSubSound(nint sound, int index, out nint subSound);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SeekData(nint sound, uint offset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDefaults(nint sound, out float frequency, out int priority);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFormat(nint sound, out int type, out int format, out int channels, out int bits);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetLength(nint sound, out uint length, uint unit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadData(nint sound, nint buffer, uint length, out uint read);
        private readonly CreateSystem _create; private readonly InitSystem _init; private readonly ReleaseSystem _release; private readonly CreateSound _createSound; private readonly ReleaseSound _releaseSound; private readonly GetNumSubSounds _num; private readonly GetSubSound _sub; private readonly SeekData _seek; private readonly GetDefaults _defaults; private readonly GetFormat _format; private readonly GetLength _length; private readonly ReadData _read;
        private FmodDecodeApi(FmodCodecLibrary l) { _create = l.Bind<CreateSystem>("FMOD_System_Create")!; _init = l.Bind<InitSystem>("FMOD_System_Init")!; _release = l.Bind<ReleaseSystem>("FMOD_System_Release")!; _createSound = l.Bind<CreateSound>("FMOD_System_CreateSound")!; _releaseSound = l.Bind<ReleaseSound>("FMOD_Sound_Release")!; _num = l.Bind<GetNumSubSounds>("FMOD_Sound_GetNumSubSounds")!; _sub = l.Bind<GetSubSound>("FMOD_Sound_GetSubSound")!; _seek = l.Bind<SeekData>("FMOD_Sound_SeekData")!; _defaults = l.Bind<GetDefaults>("FMOD_Sound_GetDefaults")!; _format = l.Bind<GetFormat>("FMOD_Sound_GetFormat")!; _length = l.Bind<GetLength>("FMOD_Sound_GetLength")!; _read = l.Bind<ReadData>("FMOD_Sound_ReadData")!; }
        public static FmodDecodeApi? Create(FmodCodecLibrary l) => new[] { "FMOD_System_Create", "FMOD_System_Init", "FMOD_System_CreateSound", "FMOD_Sound_ReadData" }.All(l.HasExport) ? new FmodDecodeApi(l) : null;
        public byte[] Decode(string path, CancellationToken token)
        {
            Check(_create(out var system), "FMOD_System_Create"); try { Check(_init(system, 1, 0, 0), "FMOD_System_Init"); Check(_createSound(system, path, 0x2000, 0, out var sound), "FMOD_System_CreateSound"); try { Check(_num(sound, out var count), "FMOD_Sound_GetNumSubSounds"); if (count < 1) count = 1; Check(_sub(sound, 0, out var sub), "FMOD_Sound_GetSubSound"); try { Check(_seek(sub, 0), "FMOD_Sound_SeekData"); Check(_defaults(sub, out var frequency, out _), "FMOD_Sound_GetDefaults"); Check(_format(sub, out _, out _, out var channels, out var bits), "FMOD_Sound_GetFormat"); Check(_length(sub, out var length, 4), "FMOD_Sound_GetLength"); var pcm = new byte[length]; var used = 0; while (used < pcm.Length) { token.ThrowIfCancellationRequested(); var chunk = Math.Min(262144, pcm.Length - used); var pin = GCHandle.Alloc(pcm, GCHandleType.Pinned); try { Check(_read(sub, pin.AddrOfPinnedObject() + used, (uint)chunk, out var got), "FMOD_Sound_ReadData"); if (got == 0) break; used += checked((int)got); } finally { pin.Free(); } } using var output = new MemoryStream(); WriteWaveHeader(output, (int)frequency, bits, channels, used); output.Write(pcm, 0, used); return output.ToArray(); } finally { _releaseSound(sub); } } finally { _releaseSound(sound); } } finally { _release(system); }
        }
    }

    private sealed class FsBankEncodeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(int version, uint flags, uint threads, [MarshalAs(UnmanagedType.LPStr)] string? tempDirectory);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Build(nint subsounds, uint count, int format, uint encryptionKey, uint quality, [MarshalAs(UnmanagedType.LPStr)] string? cacheDirectory, [MarshalAs(UnmanagedType.LPStr)] string output);
        [StructLayout(LayoutKind.Sequential)] private struct SubSound { public nint FileNames; public nint FileData; public nint FileDataLengths; public uint NumFiles; public uint OverrideFlags; public uint OverrideQuality; public float DesiredSampleRate; public float PercentOptimizedRate; }
        private readonly Init _init; private readonly Release _release; private readonly Build _build;
        private FsBankEncodeApi(FmodCodecLibrary l) { _init = l.Bind<Init>("FSBank_Init")!; _release = l.Bind<Release>("FSBank_Release")!; _build = l.Bind<Build>("FSBank_Build")!; }
        public static FsBankEncodeApi? Create(FmodCodecLibrary l) => new[] { "FSBank_Init", "FSBank_Build", "FSBank_Release" }.All(l.HasExport) ? new FsBankEncodeApi(l) : null;
        public void Encode(string wavPath, string outputPath, CancellationToken token)
        {
            Check(_init(0, 0, 1, null), "FSBank_Init"); try { token.ThrowIfCancellationRequested(); var file = Marshal.StringToCoTaskMemAnsi(wavPath); var names = Marshal.AllocHGlobal(IntPtr.Size); var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<SubSound>()); try { Marshal.WriteIntPtr(names, file); Marshal.StructureToPtr(new SubSound { FileNames = names, NumFiles = 1 }, descriptor, false); Check(_build(descriptor, 1, 5, 0, 0, null, outputPath), "FSBank_Build"); } finally { Marshal.FreeHGlobal(descriptor); Marshal.FreeHGlobal(names); Marshal.FreeCoTaskMem(file); } } finally { _release(); }
        }
    }

    private static void Check(int code, string operation) { if (code != 0) throw new InvalidOperationException($"{operation} 返回 FMOD 错误码 {code}。"); }
    private static void WriteWaveHeader(Stream stream, int sampleRate, int bits, int channels, int dataLength) { using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true); writer.Write("RIFF"u8.ToArray()); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(sampleRate); writer.Write(sampleRate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write((short)bits); writer.Write("data"u8.ToArray()); writer.Write(dataLength); }
}
