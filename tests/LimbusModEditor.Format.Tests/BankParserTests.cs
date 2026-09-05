using System.Buffers.Binary;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

public class BankParserTests
{
    [Fact]
    public void ParsesMinimalFevBank()
    {
        var data = new byte[0x50];
        "RIFF"u8.CopyTo(data); "FEV "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
        "LIST"u8.CopyTo(data.AsSpan(0x1C));
        "PROJBNKI"u8.CopyTo(data.AsSpan(0x24));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), 0);
        var sndh = 0x30; "SNDH"u8.CopyTo(data.AsSpan(sndh)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sndh + 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sndh + 12), 0x48); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sndh + 16), 4);
        "FSB5"u8.CopyTo(data.AsSpan(0x48));
        var info = BankParser.TryParse(data);
        Assert.NotNull(info);
        Assert.Equal(1, info.FsbCount);
        Assert.False(info.IsEncrypted);
    }
}
