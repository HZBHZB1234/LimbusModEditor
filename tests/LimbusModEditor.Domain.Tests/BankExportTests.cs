using System.Buffers.Binary;
using System.IO.Compression;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Domain.Tests;

public sealed class BankExportTests
{
    [Fact]
    public async Task RawFsbReplacementCanBeExportedWithoutFmodDll()
    {
        var root = Directory.CreateTempSubdirectory("lme-bank-");
        try
        {
            var source = Path.Combine(root.FullName, "base.bank");
            var replacement = Path.Combine(root.FullName, "replacement.fsb");
            var output = Path.Combine(root.FullName, "out.bank");
            var bytes = CreateBank("FSB5-old"u8.ToArray());
            await File.WriteAllBytesAsync(source, bytes);
            await File.WriteAllBytesAsync(replacement, "FSB5-new"u8.ToArray());
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "fsb/0", Type = AssetType.Audio };
            asset.Metadata["replacementPath"] = replacement;
            project.Assets.Add(asset);
            var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(source, project, output);
            Assert.Equal(ModFormatKind.Bank, result.Format);
            var parsed = BankParser.TryParse(await File.ReadAllBytesAsync(output));
            Assert.NotNull(parsed);
            var extracted = BankParser.Extract(await File.ReadAllBytesAsync(output), parsed!);
            Assert.Equal("FSB5-new"u8.ToArray(), extracted[0].ToArray());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task WaveReplacementUsesInjectedCodec()
    {
        var root = Directory.CreateTempSubdirectory("lme-bank-");
        try
        {
            var source = Path.Combine(root.FullName, "base.bank");
            var replacement = Path.Combine(root.FullName, "replacement.wav");
            var output = Path.Combine(root.FullName, "out.bank");
            await File.WriteAllBytesAsync(source, CreateBank("FSB5-old"u8.ToArray()));
            await File.WriteAllBytesAsync(replacement, "RIFFxxxxWAVE"u8.ToArray());
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "fsb/0", Type = AssetType.Audio };
            asset.Metadata["replacementPath"] = replacement; project.Assets.Add(asset);
            await new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(source, project, output, codec: new FakeCodec());
            var parsed = BankParser.TryParse(await File.ReadAllBytesAsync(output));
            Assert.Equal("FSB5-from-wave"u8.ToArray(), BankParser.Extract(await File.ReadAllBytesAsync(output), parsed!)[0].ToArray());
        }
        finally { root.Delete(true); }
    }

    private sealed class FakeCodec : IFmodAudioCodec
    {
        public bool IsAvailable => true;
        public Task<byte[]> DecodeFsbToWaveAsync(ReadOnlyMemory<byte> fsb, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> EncodeWaveToFsbAsync(ReadOnlyMemory<byte> wave, CancellationToken cancellationToken = default) => Task.FromResult("FSB5-from-wave"u8.ToArray());
    }

    private static byte[] CreateBank(byte[] fsb)
    {
        var data = new byte[0x48 + fsb.Length];
        "RIFF"u8.CopyTo(data); "FEV "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 1);
        "LIST"u8.CopyTo(data.AsSpan(0x1C)); "PROJBNKI"u8.CopyTo(data.AsSpan(0x24));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), 0);
        "SNDH"u8.CopyTo(data.AsSpan(0x30)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x48); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), (uint)fsb.Length);
        fsb.CopyTo(data.AsSpan(0x48));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        return data;
    }
}
