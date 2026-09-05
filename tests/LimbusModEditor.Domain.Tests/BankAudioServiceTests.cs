using System.Buffers.Binary;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Domain.Tests;

public sealed class BankAudioServiceTests
{
    [Fact]
    public async Task ExtractsIndexedFsbFromBank()
    {
        var root = Directory.CreateTempSubdirectory("lme-bank-audio-");
        try
        {
            var bank = Path.Combine(root.FullName, "base.bank");
            var fsb = "FSB5-demo"u8.ToArray();
            await File.WriteAllBytesAsync(bank, CreateBank(fsb));
            var asset = new AssetRecord { LogicalPath = "fsb/0", SourcePath = bank, Type = AssetType.Audio };
            var extracted = await new BankAudioService().ReadFsbAsync(asset);
            Assert.Equal(fsb, extracted);
        }
        finally { root.Delete(true); }
    }

    private static byte[] CreateBank(byte[] fsb)
    {
        var data = new byte[0x48 + fsb.Length]; "RIFF"u8.CopyTo(data); "FEV "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1); "LIST"u8.CopyTo(data.AsSpan(0x1C)); "PROJBNKI"u8.CopyTo(data.AsSpan(0x24));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), 0); "SNDH"u8.CopyTo(data.AsSpan(0x30)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x48); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), (uint)fsb.Length); fsb.CopyTo(data.AsSpan(0x48));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8)); return data;
    }
}
