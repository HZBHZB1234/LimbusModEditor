using System.Buffers.Binary;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;
using System.Diagnostics;
using System.Text.Json;

namespace LimbusModEditor.Application.Tests;

public sealed class BankAudioServiceTests
{
    [Fact]
    public async Task Reads_only_the_selected_payload_from_a_large_bank()
    {
        var root = Directory.CreateTempSubdirectory("lme-bank-slice-");
        try
        {
            var path = Path.Combine(root.FullName, "large.bank");
            var fsb = "FSB5-selected"u8.ToArray();
            await File.WriteAllBytesAsync(path, CreateBank(fsb));
            using (var file = File.OpenWrite(path)) file.SetLength(64 * 1024 * 1024);
            var asset = new AssetRecord { SourcePath = path, LogicalPath = "fsb/0", Type = AssetType.Audio };
            var service = new BankAudioService();
            // 大数组会在首次异步读取前分配；只计本次调用的同步准备阶段，避免线程切换干扰。
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var read = service.ReadFsbAsync(asset);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1024 * 1024);
            Assert.Equal(fsb, await read);
            asset.LogicalPath = "fsb/1";
            await Assert.ThrowsAsync<IndexOutOfRangeException>(() => service.ReadFsbAsync(asset));
            asset.LogicalPath = "fsb/0";
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadFsbAsync(asset, new CancellationToken(true)));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Measure_large_bank_slice_allocations()
    {
        var output = Environment.GetEnvironmentVariable("LME_AUDIO_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "audio-fixture.bank");
        var fsb = "FSB5-selected"u8.ToArray();
        await File.WriteAllBytesAsync(path, CreateBank(fsb));
        using (var file = File.OpenWrite(path)) file.SetLength(64 * 1024 * 1024);
        var asset = new AssetRecord { SourcePath = path, LogicalPath = "fsb/0", Type = AssetType.Audio };
        var metrics = new Dictionary<string, object>();
        foreach (var legacy in new[] { true, false })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            byte[] actual;
            if (legacy)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var info = BankParser.TryParse(bytes)!;
                actual = bytes.AsSpan((int)info.FsbOffsets[0], (int)info.FsbSizes[0]).ToArray();
            }
            else actual = await new BankAudioService().ReadFsbAsync(asset);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var memory = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0;
            Assert.Equal(fsb, actual);
            metrics[legacy ? "Before" : "After"] = new { Ms = elapsed, AllocatedMiB = memory };
        }
        File.WriteAllText(Path.Combine(output, "audio.json"), JsonSerializer.Serialize(metrics,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(path);
    }

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
