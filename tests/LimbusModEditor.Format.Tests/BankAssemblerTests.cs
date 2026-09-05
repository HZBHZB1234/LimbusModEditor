using System.Buffers.Binary;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

public sealed class BankAssemblerTests
{
    [Fact]
    public void RebuildRewritesOffsetsSizesAndRiffLengthForRawFsb()
    {
        var original = new byte[0x4C];
        "RIFF"u8.CopyTo(original);
        "FEV "u8.CopyTo(original.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(0x14), 1);
        "LIST"u8.CopyTo(original.AsSpan(0x1C));
        "PROJBNKI"u8.CopyTo(original.AsSpan(0x24));
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(0x2C), 0);
        "SNDH"u8.CopyTo(original.AsSpan(0x30));
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(0x34), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(0x3C), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(0x40), 4);
        "FSB5"u8.CopyTo(original.AsSpan(0x48));
        var package = new BankPackage { OriginalData = original, Info = BankParser.TryParse(original)! };
        package.FsbData.Add("FSB5-new"u8.ToArray());
        package.HasModifications = true;
        var rebuilt = BankAssembler.Rebuild(package);
        Assert.Equal(0x50, rebuilt.Length);
        Assert.Equal((uint)0x48, BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(0x3C)));
        Assert.Equal((uint)8, BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(0x40)));
        Assert.Equal("FSB5-new"u8.ToArray(), rebuilt[^8..]);
        Assert.Equal((uint)(rebuilt.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(4)));
    }
}
