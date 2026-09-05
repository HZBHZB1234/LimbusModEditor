using System.Buffers.Binary;
using System.Text;

namespace LimbusModEditor.Formats.Bank;

/// <summary>One metadata chunk attached to a sample entry (type per the FSB5
/// spec: 1=channels, 2=frequency, 3=loop, 6=XMA seek, 7=DSP coefficients,
/// 10=XWMA, 11=Vorbis setup).</summary>
public sealed record Fsb5Chunk(int Type, string TypeName, uint PayloadSize);

/// <summary>One parsed FSB5 sub-sound header entry. All values come from the
/// documented packed-u64 + metadata-chunk layout (cross-checked against
/// vgmstream's src/meta/fsb5.c and python-fsb5); the bank-wide codec lives in
/// the header, per-sample data size is derived from the next entry's offset —
/// never invented.</summary>
public sealed record Fsb5SampleInfo(
    int Index,
    string? Name,
    int SampleRate,
    int Channels,
    uint SampleCount,
    uint LoopStart,
    uint LoopEnd,
    bool HasLoop,
    uint DataOffset,
    long? DataSize,
    uint EntrySize,
    long EntryOffset,
    IReadOnlyList<Fsb5Chunk> Chunks);

/// <summary>Structural view of one FSB5 blob. The header is
/// magic@0x00, version@0x04, numSamples@0x08, sampleHeadersSize@0x0C,
/// nameTableSize@0x10, dataSize@0x14, codec@0x18; base header is 0x3C for
/// version 1 and 0x40 for version 0 (flags/hash/sub-hash tail).</summary>
public sealed record Fsb5Info(
    uint Version,
    int SampleCount,
    uint SampleHeaderSize,
    uint NameTableSize,
    uint DataSize,
    int Codec,
    string CodecName,
    uint BaseHeaderSize,
    long DataStartOffset,
    bool SizeConsistent,
    IReadOnlyList<Fsb5SampleInfo> Samples,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Read-only FSB5 structure prober following the layout documented by
/// vgmstream (src/meta/fsb5.c) and python-fsb5. Anything outside the verified
/// layout (unknown version, invalid rate index, chunk overrun) raises a clear
/// error instead of guessing. No data is rewritten and no codec is invoked.
/// </summary>
public static class Fsb5Parser
{
    private const int MaxSamples = 0x10000;

    /// <summary>Lenient probe: null when the blob does not match the verified
    /// FSB5 layout at all. Use <see cref="Parse"/> for the specific reason.</summary>
    public static Fsb5Info? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x3C || !data[..4].SequenceEqual("FSB5"u8)) return null;
        try { return ParseCore(data); }
        catch (Exception) { return null; }
    }

    /// <summary>Strict parse: throws with the specific layout problem
    /// (unknown version, invalid rate index, chunk overrun, truncation).</summary>
    public static Fsb5Info Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x3C || !data[..4].SequenceEqual("FSB5"u8))
            throw new InvalidDataException(
                "无法识别 FSB5 结构：数据可能已加密、不是 FSB5、版本不受支持或已被截断（不做猜测性解析）。");
        return ParseCore(data);
    }

    private static Fsb5Info ParseCore(ReadOnlySpan<byte> data)
    {
        var diagnostics = new List<string>();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[0x04..]);
        if (version is not (0 or 1))
            throw new InvalidDataException($"FSB5 版本 {version} 超出已验证范围（0 或 1），拒绝猜测性解析。");
        var sampleCount32 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x08..]);
        var sampleHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x0C..]);
        var nameTableSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var codec = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);

        // Base header: version 1 = 0x3C (flags@0x20 + 128-bit hash@0x24 + 8B),
        // version 0 = 0x40 (zero/flags + 128-bit hash@0x28 + 8B).
        var baseHeader = version == 1 ? 0x3C : 0x40;

        var declaredTotal = (long)baseHeader + sampleHeaderSize + nameTableSize + dataSize;
        var sizeConsistent = declaredTotal == data.Length;
        if (!sizeConsistent)
            diagnostics.Add($"长度不一致：头 0x{baseHeader:X} + 条目 0x{sampleHeaderSize:X} + 名称 0x{nameTableSize:X} + " +
                            $"数据 0x{dataSize:X} = 0x{declaredTotal:X}，实际 0x{data.Length:X}（文件可能被重新封装或截断）。");

        if (sampleCount32 > MaxSamples)
            diagnostics.Add($"样本数量 {sampleCount32} 超出合理范围（>{MaxSamples}），仅解析前 {MaxSamples} 个条目。");
        var count = (int)Math.Min(sampleCount32, MaxSamples);

        var entriesEnd = (long)baseHeader + sampleHeaderSize;
        if (entriesEnd > data.Length)
            throw new InvalidDataException($"样本条目区结束位置 0x{entriesEnd:X} 超出数据长度 0x{data.Length:X}（文件被截断）。");

        var samples = new List<Fsb5SampleInfo>(count);
        var cursor = (long)baseHeader;
        for (var i = 0; i < count; i++)
        {
            if (cursor + 8 > entriesEnd)
                throw new InvalidDataException($"样本 {i} 的条目起始 0x{cursor:X} 超出条目区结束 0x{entriesEnd:X}。");
            var entryOffset = cursor;
            var packed = BinaryPrimitives.ReadUInt64LittleEndian(data[(int)cursor..]);
            cursor += 8;

            var nextChunk = (packed & 0x1) != 0;
            var rateIndex = (int)((packed >> 1) & 0xF);
            var channelBits = (int)((packed >> 5) & 0x3);
            var dataOffset = (uint)(((packed >> 7) & 0x7FF_FFFF) << 5); // 25 bits, 32-byte aligned
            var sampleCount = (uint)((packed >> 34) & 0x3FFF_FFFF);     // 30 bits

            var sampleRate = rateIndex switch
            {
                0 => 4000, 1 => 8000, 2 => 11000, 3 => 11025, 4 => 16000, 5 => 22050,
                6 => 24000, 7 => 32000, 8 => 44100, 9 => 48000, 10 => 96000,
                _ => throw new InvalidDataException(
                    $"样本 {i} 的采样率索引 {rateIndex} 无效（0..10 之外且未提供 FREQUENCY 块前的裸值不可信），拒绝猜测性解析。")
            };
            var channels = channelBits switch
            {
                0 => 1, 1 => 2, 2 => 6, 3 => 8,
                _ => throw new InvalidOperationException("unreachable")
            };

            var chunks = new List<Fsb5Chunk>();
            var chunkCursor = cursor;
            while (nextChunk)
            {
                if (chunkCursor + 4 > entriesEnd)
                    throw new InvalidDataException($"样本 {i} 的元数据块头 0x{chunkCursor:X} 越出条目区 0x{entriesEnd:X}。");
                var chunkHeader = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)chunkCursor..]);
                nextChunk = (chunkHeader & 0x1) != 0;
                var chunkSize = (chunkHeader >> 1) & 0xFF_FFFF;
                var chunkType = (int)((chunkHeader >> 25) & 0x7F);
                chunks.Add(new Fsb5Chunk(chunkType, DescribeChunkType(chunkType), chunkSize));
                chunkCursor += 4 + chunkSize;
                if (chunkCursor > entriesEnd)
                    throw new InvalidDataException($"样本 {i} 的元数据块（类型 {chunkType}，长度 0x{chunkSize:X}）越出条目区 0x{entriesEnd:X}。");
            }
            var entrySize = (uint)(chunkCursor - entryOffset);
            cursor = chunkCursor;

            uint loopStart = 0, loopEnd = 0;
            var hasLoop = false;
            var loopChunk = chunks.FindIndex(x => x.Type == 3);
            if (loopChunk >= 0)
            {
                if (loopChunk < chunks.Count - 1 || chunks[loopChunk].PayloadSize < 8)
                    diagnostics.Add($"样本 {i} 的 LOOP 块长度 0x{chunks[loopChunk].PayloadSize:X} 不足以容纳 start+end，循环信息忽略。");
                else
                {
                    var payloadAt = ChunkPayloadOffset(entryOffset, chunks, loopChunk);
                    loopStart = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)payloadAt..]);
                    loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(payloadAt + 4)..]);
                    hasLoop = true;
                }
            }
            // CHANNELS chunk (type 1, single byte) overrides the packed value.
            var channelsChunkIndex = chunks.FindIndex(x => x.Type == 1);
            if (channelsChunkIndex >= 0)
            {
                if (chunks[channelsChunkIndex].PayloadSize < 1)
                    diagnostics.Add($"样本 {i} 的 CHANNELS 块长度为 0，保留 packed 位解码的 {channels}。");
                else
                {
                    var payloadAt = ChunkPayloadOffset(entryOffset, chunks, channelsChunkIndex);
                    var overrideChannels = data[(int)payloadAt];
                    if (overrideChannels is > 0 and <= 64) channels = overrideChannels;
                    else diagnostics.Add($"样本 {i} 的 CHANNELS 块值 {overrideChannels} 不在 1..64 内，保留 packed 位解码的 {channels}。");
                }
            }

            samples.Add(new Fsb5SampleInfo(i, null, sampleRate, channels, sampleCount,
                loopStart, loopEnd, hasLoop, dataOffset, null, entrySize, entryOffset, chunks));
        }

        // Name table: numSamples u32 offsets, then null-terminated strings.
        var namesStart = entriesEnd;
        if (nameTableSize > 0)
        {
            if (namesStart + nameTableSize > data.Length)
                throw new InvalidDataException($"名称表区域 0x{namesStart:X}..0x{namesStart + nameTableSize:X} 超出数据长度。");
            var tableEnd = (int)(namesStart + nameTableSize);
            var offsetsEnd = namesStart + 4L * samples.Count;
            if (offsetsEnd > tableEnd)
                throw new InvalidDataException($"名称偏移数组（{samples.Count}×4 字节）超出名称表 0x{nameTableSize:X} 字节。");
            for (var i = 0; i < samples.Count; i++)
            {
                var nameAt = namesStart + BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(namesStart + 4 * i)..]);
                if (nameAt < offsetsEnd || nameAt >= tableEnd)
                {
                    diagnostics.Add($"样本 {i} 的名称偏移 0x{nameAt:X} 不在名称字符串区内，保留空名。");
                    continue;
                }
                var end = nameAt;
                while (end < tableEnd && data[(int)end] != 0) end++;
                var length = (int)(end - nameAt);
                samples[i] = samples[i] with
                {
                    Name = length > 0 && length <= 260 ? Encoding.UTF8.GetString(data[(int)nameAt..(int)end]) : null
                };
            }
        }

        // Data section: per-sample size derived from the next entry's offset;
        // the last sample extends to the declared data-section end.
        var dataStart = namesStart + nameTableSize;
        for (var i = 0; i < samples.Count; i++)
        {
            var nextOffset = i + 1 < samples.Count ? samples[i + 1].DataOffset : dataSize;
            long? size = nextOffset >= samples[i].DataOffset ? nextOffset - samples[i].DataOffset : null;
            if (size is null) diagnostics.Add($"样本 {i} 的数据偏移 0x{samples[i].DataOffset:X} 大于下一条目/数据区上限，数据大小不可知。");
            samples[i] = samples[i] with { DataSize = size };
        }

        return new Fsb5Info(version, count, sampleHeaderSize, nameTableSize, dataSize,
            (int)codec, DescribeCodec(codec), (uint)baseHeader, dataStart, sizeConsistent, samples, diagnostics);
    }

    /// <summary>Payload offset of chunk <paramref name="index"/>: u64 packed
    /// header, then per preceding chunk a u32 header plus its payload, plus
    /// this chunk's own u32 header.</summary>
    private static long ChunkPayloadOffset(long entryOffset, IReadOnlyList<Fsb5Chunk> chunks, int index)
    {
        long at = entryOffset + 8;
        for (var j = 0; j < index; j++) at += 4 + chunks[j].PayloadSize;
        return at + 4;
    }

    /// <summary>FMOD_SOUND_FORMAT values carried by the bank-wide codec field.</summary>
    public static string DescribeCodec(uint codec) => codec switch
    {
        0 => "NONE(0)",
        1 => "PCM8", 2 => "PCM16", 3 => "PCM24", 4 => "PCM32", 5 => "PCMFLOAT",
        6 => "GCADPCM", 7 => "IMAADPCM", 8 => "VAG", 9 => "HEVAG", 10 => "XMA",
        11 => "MPEG", 12 => "CELT", 13 => "AT9", 14 => "XWMA", 15 => "Vorbis",
        16 => "FADPCM", 17 => "Opus(未验证)",
        _ => $"未知(0x{codec:X})"
    };

    private static string DescribeChunkType(int type) => type switch
    {
        1 => "CHANNELS", 2 => "FREQUENCY", 3 => "LOOP", 4 => "COMMENT", 5 => "未知(5)",
        6 => "XMASEEK", 7 => "DSPCOEFF", 10 => "XWMADATA", 11 => "VORBISDATA",
        _ => $"未知({type})"
    };
}
