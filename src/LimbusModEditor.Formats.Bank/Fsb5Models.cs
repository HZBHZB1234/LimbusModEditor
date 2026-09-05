using System.Buffers.Binary;
using System.Text;

namespace LimbusModEditor.Formats.Bank;

/// <summary>One parsed FSB5 sub-sound header entry. Values the format does not
/// define unambiguously are reported as raw bytes instead of guesses.</summary>
public sealed record Fsb5SampleInfo(
    int Index,
    string? Name,
    uint DataSize,
    uint SampleRate,
    string Channels,
    string Format,
    uint HeaderEntrySize,
    long HeaderEntryOffset,
    long DataOffset);

/// <summary>Structural view of one FSB5 blob: header fields, per-sample
/// entries, the name table and where sample data starts, plus any diagnostics
/// for parts that could not be interpreted.</summary>
public sealed record Fsb5Info(
    uint Version,
    int SampleCount,
    uint SampleHeaderSize,
    long NameOffset,
    uint Mode,
    IReadOnlyList<Fsb5SampleInfo> Samples,
    long DataStartOffset,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Read-only FSB5 structure prober. It walks only the documented file header
/// and sample-header entries; anything unexpected becomes a diagnostic instead
/// of a guess. No data is rewritten and no codec is invoked.
/// </summary>
public static class Fsb5Parser
{
    public static Fsb5Info? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20 || !data[..4].SequenceEqual("FSB5"u8)) return null;
        try { return ParseCore(data); }
        catch (Exception) { return null; }
    }

    public static Fsb5Info Parse(ReadOnlySpan<byte> data)
        => TryParse(data) ?? throw new InvalidDataException(
            "无法识别 FSB5 结构：数据可能已加密、不是 FSB5 或已被截断（不做猜测性解析）。");

    private static Fsb5Info ParseCore(ReadOnlySpan<byte> data)
    {
        var diagnostics = new List<string>();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var sampleCount32 = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var sampleHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var nameOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);

        var samples = new List<Fsb5SampleInfo>();
        if (sampleCount32 > 0x10000)
            diagnostics.Add($"样本数量 {sampleCount32} 超出合理范围（>65536），仅解析前 65536 个条目。");
        var count = (int)Math.Min(sampleCount32, 0x10000);

        var cursor = 0x20;
        var end = cursor + sampleHeaderSize;
        if (end > data.Length)
        {
            diagnostics.Add($"样本头区域大小 0x{sampleHeaderSize:X} 超出数据长度，样本信息可能不完整。");
            end = data.Length;
        }

        for (var i = 0; i < count && cursor + 4 <= end; i++)
        {
            var entryOffset = (long)cursor;
            var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            cursor += 4;
            // Entry payload: u32 dataSize, u16 frequency, u8 channel flags,
            // u8 format, then optional loop/codec chunks.
            if (entrySize < 8)
            {
                diagnostics.Add($"样本 {i} 的头部条目长度 {entrySize} 小于最小值 8，后续条目不再解析。");
                break;
            }
            if (cursor + entrySize > end)
            {
                diagnostics.Add($"样本 {i} 的头部条目越界（声明 0x{entrySize:X} 字节），样本信息可能不完整。");
                break;
            }
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 4)..]);
            var channelByte = data[cursor + 6];
            var formatByte = data[cursor + 7];
            var channels = DescribeChannels(channelByte, diagnostics, i);
            samples.Add(new Fsb5SampleInfo(
                i, null, dataSize, sampleRate, channels, DescribeFormat(formatByte),
                entrySize, entryOffset, DataOffset: 0));
            cursor += (int)entrySize;
        }
        if (samples.Count < count)
            diagnostics.Add($"只解析出 {samples.Count}/{count} 个样本条目。");

        // Name table: numSamples null-terminated strings starting at nameOffset.
        long dataStart = 0x20 + sampleHeaderSize;
        if (nameOffset != 0 && nameOffset < (ulong)Math.Max(0, data.Length))
        {
            var nameCursor = (long)nameOffset;
            var nameEnd = Math.Min(data.Length, nameCursor + 0x100000);
            for (var i = 0; i < samples.Count; i++)
            {
                var start = nameCursor;
                while (nameCursor < nameEnd && data[(int)nameCursor] != 0) nameCursor++;
                if (nameCursor >= nameEnd)
                {
                    diagnostics.Add($"样本 {i} 的名称超出名称表读取范围，剩余名称不再解析。");
                    break;
                }
                var length = (int)(nameCursor - start);
                samples[i] = samples[i] with
                {
                    Name = length > 0 && length <= 260
                        ? Encoding.UTF8.GetString(data[(int)start..(int)nameCursor])
                        : null
                };
                nameCursor++; // skip terminator
            }
            dataStart = Math.Max(dataStart, nameCursor);
        }
        else if (nameOffset != 0)
        {
            diagnostics.Add($"名称表偏移 0x{nameOffset:X} 越界，忽略名称表。");
        }

        // Sample data begins right after headers/names; report each sample's
        // absolute data offset by accumulating declared data sizes.
        long running = dataStart;
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = samples[i] with { DataOffset = running };
            running += samples[i].DataSize;
        }

        return new Fsb5Info(version, count, sampleHeaderSize, (long)nameOffset, mode, samples, dataStart, diagnostics);
    }

    private static string DescribeChannels(byte channelByte, List<string> diagnostics, int sampleIndex)
    {
        if ((channelByte & 0xF0) != 0)
            diagnostics.Add($"样本 {sampleIndex} 的声道标记 0x{channelByte:X2} 含未定义的高位，声道数按低位保守解释。");
        var low = channelByte & 0x03;
        return low == 0 ? "1（推测，标记 0）" : $"{low + 1}（推测，标记 0x{channelByte:X2}）";
    }

    /// <summary>FMOD_SOUND_FORMAT values used by FSB5 sample headers.</summary>
    private static string DescribeFormat(byte format) => format switch
    {
        0 => "PCM8",
        1 => "PCM16",
        2 => "PCM24",
        3 => "PCM32",
        4 => "PCMFLOAT",
        5 => "GCADPCM",
        6 => "IMAADPCM",
        7 => "VAG",
        8 => "HEVAG",
        9 => "XMA",
        10 => "MPEG",
        11 => "CELT",
        12 => "ATRAC9",
        13 => "xWMA",
        14 => "Vorbis",
        15 => "FADPCM",
        17 => "Opus",
        _ => $"未知(0x{format:X2})"
    };
}
