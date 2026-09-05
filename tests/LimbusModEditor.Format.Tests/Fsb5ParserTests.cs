using System.Buffers.Binary;
using System.Text;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

/// <summary>P2.1 tests over synthetic FSB5 blobs built with the layout
/// cross-checked against vgmstream's src/meta/fsb5.c and python-fsb5:
/// 0x3C/0x40 base header, packed-u64 sample entries with metadata chunks,
/// offset-based name table, and neighbor-derived data sizes.</summary>
public class Fsb5ParserTests
{
    /// <summary>Builds a version-1 FSB5: header 0x3C bytes, one entry per
    /// sample spec, optional LOOP/CHANNELS chunks, name table and data.</summary>
    private static byte[] BuildFsb5((uint Samples, int RateIndex, int ChannelBits, uint DataOffset, (int Type, byte[] Payload)[] Chunks)[] samples, uint version = 1)
    {
        // --- sample entries first to learn their total size ---
        var entries = new MemoryStream();
        using (var w = new BinaryWriter(entries))
        foreach (var sample in samples)
        {
            ulong packed = 0;
            if (sample.Chunks.Length > 0) packed |= 0x1;
            packed |= (ulong)(uint)sample.RateIndex << 1;
            packed |= (ulong)(uint)sample.ChannelBits << 5;
            packed |= (ulong)(sample.DataOffset >> 5) << 7; // 25-bit field, 32-byte aligned
            packed |= (ulong)sample.Samples << 34;
            w.Write(packed);
            foreach (var (type, payload) in sample.Chunks)
            {
                var header = (uint)(type << 25) | (uint)(payload.Length << 1) | 0x1; // continue bit; cleared by caller if last
                w.Write(header);
                w.Write(payload);
            }
        }
        var entryBytes = entries.ToArray();
        // clear the continue bit of each entry's final chunk (writer set it on every chunk)
        ClearLastChunkContinue(entryBytes, samples);

        // --- name table: u32 offsets then strings ---
        var names = samples.Select((s, i) => $"sample_{i:D3}").ToArray();
        var nameBlob = new MemoryStream();
        using (var w = new BinaryWriter(nameBlob))
        {
            var at = 4 * names.Length;
            foreach (var name in names) { w.Write((uint)at); at += name.Length + 1; }
            foreach (var name in names) w.Write(Encoding.ASCII.GetBytes(name + "\0"));
        }
        var nameBytes = nameBlob.ToArray();

        // --- data section ---
        var dataSize = (uint)samples.Max(s => s.DataOffset + 0x40);
        var data = new byte[dataSize];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        var baseHeader = version == 1 ? 0x3C : 0x40;
        var blob = new MemoryStream();
        using (var w = new BinaryWriter(blob))
        {
            w.Write("FSB5"u8.ToArray());
            w.Write(version);
            w.Write((uint)samples.Length);
            w.Write((uint)entryBytes.Length);   // sampleHeadersSize
            w.Write((uint)nameBytes.Length);    // nameTableSize
            w.Write(dataSize);                  // dataSize
            w.Write(15u);                       // codec = Vorbis
            w.Write(0u);                        // 0x1C zero
            if (version == 1)
            {
                w.Write(0u);                    // flags@0x20
                w.Write(new byte[16]);          // hash@0x24
                w.Write(new byte[8]);           // sub-hash@0x34
            }
            else
            {
                w.Write(new byte[8]);           // zero/flags@0x20 (version-0 layout)
                w.Write(new byte[16]);          // hash@0x28
                w.Write(new byte[8]);           // sub-hash@0x38
            }
            w.Write(entryBytes);
            w.Write(nameBytes);
            w.Write(data);
        }
        return blob.ToArray();
    }

    private static void ClearLastChunkContinue(byte[] entryBytes, (uint, int, int, uint, (int Type, byte[] Payload)[])[] samples)
    {
        var cursor = 0;
        foreach (var sample in samples)
        {
            cursor += 8; // packed u64
            var chunks = sample.Item5;
            for (var c = 0; c < chunks.Length; c++)
            {
                if (c == chunks.Length - 1)
                {
                    var header = BinaryPrimitives.ReadUInt32LittleEndian(entryBytes.AsSpan(cursor));
                    BinaryPrimitives.WriteUInt32LittleEndian(entryBytes.AsSpan(cursor), header & 0xFFFFFFFE);
                }
                cursor += 4 + chunks[c].Payload.Length;
            }
        }
    }

    [Fact]
    public void Parses_two_samples_with_names_and_derived_sizes()
    {
        // two entries: 44100 Hz (idx 8) mono, 48000 Hz (idx 9) with CHANNELS=6 chunk
        var loopPayload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(loopPayload, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(loopPayload.AsSpan(4), 100);
        var blob = BuildFsb5(
        [
            (Samples: 1000, RateIndex: 8, ChannelBits: 0, DataOffset: 0x00, Chunks: [(3, loopPayload)]),
            (Samples: 2000, RateIndex: 9, ChannelBits: 0, DataOffset: 0x100, Chunks: [(1, new byte[] { 6 })])
        ]);
        var info = Fsb5Parser.Parse(blob);

        Assert.Equal(1u, info.Version);
        Assert.Equal(0x3Cu, info.BaseHeaderSize);
        Assert.Equal(2, info.SampleCount);
        Assert.Equal("Vorbis", info.CodecName);
        Assert.True(info.SizeConsistent);
        Assert.Equal(0x3Cu + info.SampleHeaderSize + info.NameTableSize, (ulong)info.DataStartOffset);

        var first = info.Samples[0];
        Assert.Equal(44100, first.SampleRate);
        Assert.Equal(1, first.Channels);
        Assert.Equal(1000u, first.SampleCount);
        Assert.True(first.HasLoop);
        Assert.Equal(12u, first.LoopStart);
        Assert.Equal(100u, first.LoopEnd);
        Assert.Equal("sample_000", first.Name);
        Assert.Equal(0x100, first.DataSize);

        var second = info.Samples[1];
        Assert.Equal(48000, second.SampleRate);
        Assert.Equal(6, second.Channels); // CHANNELS chunk overrides packed bits
        Assert.Equal(2000u, second.SampleCount);
        Assert.Equal("sample_001", second.Name);
        Assert.Equal(info.DataSize - 0x100, (ulong)second.DataSize!);
        Assert.Contains(second.Chunks, c => c.Type == 1 && c.TypeName == "CHANNELS");
    }

    [Fact]
    public void Rejects_non_fsb5_and_unknown_versions()
    {
        Assert.Null(Fsb5Parser.TryParse([1, 2, 3, 4, 5]));
        var blob = BuildFsb5([(Samples: 10, RateIndex: 8, ChannelBits: 0, DataOffset: 0, Chunks: [])], version: 2);
        Assert.Throws<InvalidDataException>(() => Fsb5Parser.Parse(blob));
    }

    [Fact]
    public void Records_size_mismatch_as_diagnostic()
    {
        var blob = BuildFsb5([(Samples: 10, RateIndex: 8, ChannelBits: 0, DataOffset: 0, Chunks: [])]);
        var truncated = blob[..^8]; // chop the data section
        var info = Fsb5Parser.Parse(truncated);
        Assert.False(info.SizeConsistent);
        Assert.Contains(info.Diagnostics, d => d.Contains("长度不一致"));
    }

    [Fact]
    public void Invalid_rate_index_is_rejected_not_guessed()
    {
        var blob = BuildFsb5([(Samples: 10, RateIndex: 13, ChannelBits: 0, DataOffset: 0, Chunks: [])]);
        var ex = Assert.Throws<InvalidDataException>(() => Fsb5Parser.Parse(blob));
        Assert.Contains("采样率索引", ex.Message);
    }

    [Fact]
    public void Version0_uses_0x40_base_header()
    {
        var blob = BuildFsb5([(Samples: 10, RateIndex: 8, ChannelBits: 1, DataOffset: 0, Chunks: [])], version: 0);
        var info = Fsb5Parser.Parse(blob);
        Assert.Equal(0x40u, info.BaseHeaderSize);
        Assert.True(info.SizeConsistent);
        Assert.Equal(2, info.Samples[0].Channels);
    }
}
