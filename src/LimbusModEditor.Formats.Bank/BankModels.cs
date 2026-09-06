using System.Buffers.Binary;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Bank;

public sealed record BankInfo(IReadOnlyList<uint> FsbOffsets, IReadOnlyList<uint> FsbSizes, bool IsEncrypted, int SndhTableOffset = -1)
{
    public int FsbCount => FsbOffsets.Count;
}

public sealed class BankPackage
{
    public byte[] OriginalData { get; init; } = [];
    public BankInfo Info { get; init; } = new([], [], false);
    public List<byte[]> FsbData { get; } = [];
    public bool HasModifications { get; set; }
}

public static class BankParser
{
    public static FormatProbeResult Probe(ReadOnlySpan<byte> data)
    {
        var info = TryParse(data);
        return info is null
            ? new(ModFormatKind.Bank, false, 0, "不是有效的 FMOD FEV bank", [])
            : new(ModFormatKind.Bank, true, 98, $"FMOD bank，包含 {info.FsbCount} 个 FSB", []);
    }

    public static BankInfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x24 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("FEV "u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]) == 0 || !data.Slice(0x1C, 4).SequenceEqual("LIST"u8)) return null;
        var pos = 0x24;
        if (!data.Slice(pos, 4).SequenceEqual("PROJ"u8) || !data.Slice(pos + 4, 4).SequenceEqual("BNKI"u8)) return null;
        pos += 8;
        if (pos + 4 > data.Length) return null;
        var projectSize = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        pos += checked((int)(4 + projectSize));
        while (pos + 8 <= data.Length)
        {
            var type = data.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            pos += 8;
            if (type.SequenceEqual("SNDH"u8))
            {
                // Real game event banks (<name>.bank, e.g. 1D101A.bank) ship an
                // EMPTY SNDH (size 0) because they carry no FSB payloads; the
                // audio banks (<name>.assets.bank) hold the (offset, size) table.
                if (size == 0) return new([], [], false, pos);
                if (size < 4 || size > int.MaxValue || pos + size > data.Length) return null;
                if ((size - 4) % 8 != 0) return null;
                var count = checked((int)((size - 4) / 8));
                var offsets = new uint[count]; var sizes = new uint[count];
                pos += 4;
                var tableOffset = pos;
                for (var i = 0; i < count; i++) { offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + i * 8)..]); sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + i * 8 + 4)..]); }
                if (count == 0 || offsets[0] == 0) return null;
                for (var i = 0; i < count; i++)
                    if ((ulong)offsets[i] + sizes[i] > (ulong)data.Length) return null;
                var encrypted = offsets[0] + 4 > data.Length || !data.Slice((int)offsets[0], 4).SequenceEqual("FSB5"u8);
                return new(offsets, sizes, encrypted, tableOffset);
            }
            if (size > int.MaxValue || pos + size > data.Length) return null;
            pos += (int)size;
        }
        return null;
    }

    public static IReadOnlyList<ReadOnlyMemory<byte>> Extract(ReadOnlyMemory<byte> data, BankInfo info)
        => info.FsbOffsets.Select((offset, i) => data.Slice((int)offset, (int)info.FsbSizes[i])).ToArray();
}

public sealed class BankFormatHandler : IModFormatHandler
{
    public FormatDescriptor Descriptor { get; } = new(ModFormatKind.Bank, "FMOD Bank", [".bank"], true, true);
    public ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream(); input.CopyTo(memory); return ValueTask.FromResult(BankParser.Probe(memory.ToArray()));
    }
    public async Task<ModPackage> ImportAsync(Stream input, ImportContext context)
    {
        using var memory = new MemoryStream(); await input.CopyToAsync(memory, context.CancellationToken);
        var bytes = memory.ToArray(); var info = BankParser.TryParse(bytes) ?? throw new InvalidDataException("无法解析 bank 文件。");
        var payload = new BankPackage { OriginalData = bytes, Info = info }; payload.FsbData.AddRange(BankParser.Extract(bytes, info).Select(x => x.ToArray()));
        var package = new ModPackage { SourceFormat = ModFormatKind.Bank, Payload = payload };
        for (var i = 0; i < info.FsbCount; i++) package.Project.Assets.Add(new LimbusModEditor.Domain.Assets.AssetRecord { LogicalPath = $"fsb/{i}", ContainerPath = $"fsb/{i}", Type = LimbusModEditor.Domain.Assets.AssetType.Audio, Size = payload.FsbData[i].LongLength });
        return package;
    }
    public Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default)
    { var report = new ValidationReport(); if (package.Payload is not BankPackage) report.Diagnostics.Add(new(DiagnosticSeverity.Error, "BANK_PAYLOAD", "不是有效的 Bank 数据模型。")); return Task.FromResult(report); }
    public Task ExportAsync(ModPackage package, Stream output, ExportContext context)
    {
        if (package.Payload is not BankPackage bank)
            return Task.FromException(new InvalidDataException("无效的 Bank 数据模型。"));
        if (!bank.HasModifications)
        {
            output.Write(bank.OriginalData);
            return Task.CompletedTask;
        }
        if (bank.Info.IsEncrypted)
            return Task.FromException(new NotSupportedException("加密 Bank 的 FSB 需要 FMOD/FSBANK 解码器，不能安全地进行原始重组。"));
        output.Write(BankAssembler.Rebuild(bank));
        return Task.CompletedTask;
    }
}

/// <summary>Rebuilds a Bank when callers supply complete raw FSB blobs. It
/// rewrites only the SNDH offset/size table and RIFF length, preserving the
/// original header, project chunks and any trailing bytes.</summary>
public static class BankAssembler
{
    public static byte[] Rebuild(BankPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var info = package.Info;
        if (info.SndhTableOffset < 0 || package.FsbData.Count != info.FsbCount)
            throw new InvalidDataException("Bank 的 FSB 索引不完整，无法重组。");
        var original = package.OriginalData;
        if ((long)info.SndhTableOffset + info.FsbCount * 8L > original.Length)
            throw new InvalidDataException("Bank 的 SNDH 索引表越界。");
        var ranges = info.FsbOffsets.Select((offset, i) =>
                (Start: checked((int)offset), End: checked((int)offset + (int)info.FsbSizes[i]), Index: i))
            .OrderBy(x => x.Start).ToArray();
        if (ranges.Any(x => x.Start < 0 || x.End > original.Length) ||
            ranges.Zip(ranges.Skip(1), (a, b) => a.End <= b.Start).Any(ok => !ok))
            throw new InvalidDataException("Bank 的 FSB 区间重叠或越界。");
        var first = ranges[0].Start;
        using var result = new MemoryStream(original.Length);
        result.Write(original, 0, first);
        var cursor = first;
        var newOffsets = new uint[info.FsbCount];
        var newSizes = new uint[info.FsbCount];
        foreach (var range in ranges)
        {
            if (range.Start > cursor) result.Write(original, cursor, range.Start - cursor);
            newOffsets[range.Index] = checked((uint)result.Position);
            var fsb = package.FsbData[range.Index] ?? throw new InvalidDataException("FSB 数据不能为 null。");
            newSizes[range.Index] = checked((uint)fsb.Length);
            result.Write(fsb);
            cursor = range.End;
        }
        if (cursor < original.Length) result.Write(original, cursor, original.Length - cursor);
        var rebuilt = result.ToArray();
        for (var i = 0; i < info.FsbCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(info.SndhTableOffset + i * 8), newOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(info.SndhTableOffset + i * 8 + 4), newSizes[i]);
        }
        if (rebuilt.Length >= 8) BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(4), checked((uint)(rebuilt.Length - 8)));
        return rebuilt;
    }
}
