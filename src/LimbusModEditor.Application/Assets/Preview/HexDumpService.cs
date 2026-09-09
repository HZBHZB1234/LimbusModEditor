using System.Buffers.Binary;
using System.Text;

namespace LimbusModEditor.Application.Assets.Preview;

/// <summary>十六进制转储（plan-05 第 5 步：从 HexPreviewWindow 抽出的服务，
/// 弹窗与内嵌预览共用同一份逻辑）。只读前 <paramref name="MaxBytes"/> 字节。</summary>
public static class HexDumpService
{
    public const int MaxBytes = 4096;

    public sealed record HexDump(string Text, int ShownBytes, long TotalBytes, bool Truncated)
    {
        /// <summary>文件/负载大小的人类可读描述。</summary>
        public string Describe() => Truncated
            ? $"只读预览：显示前 {ShownBytes:N0} 字节（共 {TotalBytes:N0} 字节）"
            : $"完整显示 {ShownBytes:N0} 字节";
    }

    /// <summary>16 字节/行：偏移 + HEX + ASCII 边栏。</summary>
    public static HexDump Dump(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = new StringBuilder();
        var shown = Math.Min(data.Length, MaxBytes);
        for (var offset = 0; offset < shown; offset += 16)
        {
            text.Append(offset.ToString("X8"));
            text.Append("  ");
            var rowEnd = Math.Min(offset + 16, shown);
            for (var i = offset; i < offset + 16; i++)
            {
                if (i < rowEnd) text.Append(data[i].ToString("X2")).Append(' ');
                else text.Append("   ");
                if ((i - offset + 1) % 8 == 0) text.Append(' ');
            }
            text.Append(' ');
            for (var i = offset; i < rowEnd; i++)
            {
                var b = data[i];
                text.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }
            text.AppendLine();
        }
        if (data.Length > MaxBytes)
            text.AppendLine($"\n… 已截断，共 {data.Length:N0} 字节（只读预览，显示前 {MaxBytes} 字节）。");
        return new HexDump(text.ToString(), shown, data.Length, data.Length > MaxBytes);
    }

    /// <summary>读取文件头部并转储（文件不存在/读取失败返回 null）。</summary>
    public static HexDump? DumpFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(MaxBytes, Math.Max(0, (int)Math.Min(stream.Length, MaxBytes)))];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            var data = read == buffer.Length ? buffer : buffer[..read];
            var dump = Dump(data);
            return dump with { TotalBytes = stream.Length, Truncated = stream.Length > MaxBytes };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// WAV（PCM 16-bit / 8-bit）→ 波形包络（plan-05 第 3 步：解码产物绘制用）。
/// 非 PCM 或结构损坏时返回空包络并给出原因，不做猜测性解码。
/// </summary>
public static class AudioWaveform
{
    public sealed record Result(IReadOnlyList<float> Envelope, int SampleRate, int Channels, double DurationSeconds, string? UnavailableReason)
    {
        public bool HasEnvelope => Envelope.Count > 0;
    }

    /// <summary>把 WAV 的 PCM 数据按桶取峰值（0..1）。<paramref name="buckets"/> 通常取绘制宽度。</summary>
    public static Result BuildEnvelope(byte[]? wav, int buckets = 400)
    {
        if (wav is null || wav.Length == 0)
            return new Result([], 0, 0, 0, "没有解码后的 WAV 数据（需要 FMOD DLL 才能试听/绘制波形）。");
        if (buckets <= 0) buckets = 400;
        try
        {
            var (pcm, sampleRate, channels, bitsPerSample) = ParsePcm16(wav);
            if (pcm.Length == 0 || channels <= 0)
                return new Result([], sampleRate, channels, 0, "WAV 没有 PCM 数据。");
            var frameCount = pcm.Length / (channels * 2);
            if (frameCount == 0) return new Result([], sampleRate, channels, 0, "WAV 帧数为 0。");
            var envelope = new float[Math.Min(buckets, frameCount)];
            var framesPerBucket = (double)frameCount / envelope.Length;
            for (var bucket = 0; bucket < envelope.Length; bucket++)
            {
                var start = (int)(bucket * framesPerBucket);
                var end = Math.Min(frameCount, (int)((bucket + 1) * framesPerBucket));
                var peak = 0f;
                for (var frame = start; frame < end; frame++)
                {
                    // 逐声道取绝对值峰值（只看第一个声道之外的也覆盖，避免相位抵消）。
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var index = (frame * channels + channel) * 2;
                        if (index + 1 >= pcm.Length) break;
                        var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index, 2));
                        var value = Math.Abs(sample / 32768f);
                        if (value > peak) peak = value;
                    }
                }
                envelope[bucket] = peak;
            }
            var duration = sampleRate > 0 ? (double)frameCount / sampleRate : 0;
            return new Result(envelope, sampleRate, channels, duration, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return new Result([], 0, 0, 0, $"WAV 解析失败：{ex.Message}");
        }
    }

    /// <summary>最小 RIFF/WAVE 解析：定位 fmt（采样率/声道/位深）与 data 块。
    /// 只接受 16-bit PCM；其余形态明确报错（不做猜测性解码）。</summary>
    private static (byte[] Pcm, int SampleRate, int Channels, int BitsPerSample) ParsePcm16(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("不是 RIFF/WAVE 结构。");
        var offset = 12;
        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        while (offset + 8 <= wav.Length)
        {
            var fourCc = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            var payload = offset + 8;
            if (size < 0 || payload + size > wav.Length) size = wav.Length - payload;
            if (fourCc == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("fmt 块过短。");
                var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payload, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payload + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(payload + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(payload + 14, 2));
                if (format != 1) throw new InvalidDataException($"WAV 不是 PCM（format={format}），波形只支持 PCM。");
            }
            else if (fourCc == "data")
            {
                if (bits != 16) throw new InvalidDataException($"WAV 位深 {bits} 不是 16-bit，波形只支持 16-bit PCM。");
                var pcm = new byte[size];
                Array.Copy(wav, payload, pcm, 0, size);
                return (pcm, sampleRate, channels, bits);
            }
            offset = payload + size + (size % 2);
        }
        throw new InvalidDataException("WAV 缺少 data 块。");
    }
}
