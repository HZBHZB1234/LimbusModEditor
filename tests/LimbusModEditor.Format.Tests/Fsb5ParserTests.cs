using System.Buffers.Binary;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

/// <summary>P2.1 FSB5 structure probing over synthetic FSB5 blobs with a
/// documented layout: header, sample header entries, name table.</summary>
public class Fsb5ParserTests
{
    private static byte[] BuildFsb5(uint version, (string? Name, uint DataSize, ushort Rate, byte Channels, byte Format, byte[] Extra)[] samples)
    {
        var entries = new List<byte>();
        foreach (var sample in samples)
        {
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(sample.DataSize));
            payload.AddRange(BitConverter.GetBytes(sample.Rate));
            payload.Add(sample.Channels);
            payload.Add(sample.Format);
            payload.AddRange(sample.Extra);
            entries.AddRange(BitConverter.GetBytes(payload.Count));
            entries.AddRange(payload);
        }
        var headerRegion = entries.ToArray();
        // name table directly after the header entries
        var names = new List<byte>();
        foreach (var sample in samples)
        {
            if (sample.Name is null) continue;
            names.AddRange(System.Text.Encoding.UTF8.GetBytes(sample.Name));
            names.Add(0);
        }
        var nameOffset = samples.Any(s => s.Name is not null) ? (ulong)(0x20 + headerRegion.Length) : 0UL;

        var data = new byte[0x20 + headerRegion.Length + names.Count];
        "FSB5"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)samples.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)headerRegion.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0);
        headerRegion.CopyTo(data, 0x20);
        names.CopyTo(data, 0x20 + headerRegion.Length);
        return data;
    }

    [Fact]
    public void ParsesTwoSampleFsb5WithNameTable()
    {
        var data = BuildFsb5(0x00050000,
        [
            ("music01", 0x1000, 44100, 0x01, 14, [0x01, 0x02, 0x03, 0x04]),
            ("amb_loop", 0x0800, 22050, 0x00, 1, [])
        ]);
        var info = Fsb5Parser.Parse(data);
        Assert.Equal(0x00050000u, info.Version);
        Assert.Equal(2, info.SampleCount);
        Assert.Equal(2, info.Samples.Count);
        Assert.Equal("music01", info.Samples[0].Name);
        Assert.Equal("amb_loop", info.Samples[1].Name);
        Assert.Equal(44100u, info.Samples[0].SampleRate);
        Assert.Equal(22050u, info.Samples[1].SampleRate);
        Assert.Equal("Vorbis", info.Samples[0].Format);
        Assert.Equal("PCM16", info.Samples[1].Format);
        Assert.Equal(0x1000u, info.Samples[0].DataSize);
        Assert.Equal(0x0800u, info.Samples[1].DataSize);
        Assert.Equal(info.DataStartOffset, info.Samples[0].DataOffset);
        Assert.Equal(info.DataStartOffset + 0x1000, info.Samples[1].DataOffset);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void Rejects_non_fsb5_data()
    {
        Assert.Null(Fsb5Parser.TryParse("RIFFxxxx Vorbis data"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Fsb5Parser.Parse([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void Reports_diagnostics_for_undersized_entries()
    {
        var data = BuildFsb5(0x00010000, [("tiny", 16, 44100, 0x01, 14, [])]);
        // shrink the declared entry size below the 8-byte minimum
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), 4);
        var info = Fsb5Parser.Parse(data);
        Assert.Empty(info.Samples);
        Assert.Contains(info.Diagnostics, d => d.Contains("小于最小值"));
    }

    [Fact]
    public void Reports_diagnostics_for_entries_beyond_region()
    {
        var data = BuildFsb5(0x00010000, [("big", 16, 44100, 0x01, 14, [1, 2, 3, 4, 5, 6, 7, 8])]);
        // inflate the declared entry size past the sample-header region
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), entrySize + 64);
        var info = Fsb5Parser.Parse(data);
        Assert.Empty(info.Samples);
        Assert.Contains(info.Diagnostics, d => d.Contains("越界"));
    }

    [Fact]
    public void Detects_wrong_magic_as_encrypted_or_unknown()
    {
        // a bank marks such an FSB as encrypted when the magic is not FSB5
        var data = new byte[64];
        "FSB4"u8.CopyTo(data);
        Assert.Null(Fsb5Parser.TryParse(data));
    }
}
