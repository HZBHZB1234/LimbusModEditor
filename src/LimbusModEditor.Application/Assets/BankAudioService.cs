using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Application.Assets;

public sealed record BankFsbInspection(long FsbSize, Fsb5Info? Fsb, string? UnresolvedReason, bool MagicMissing);

public sealed class BankAudioService
{
    public async Task<byte[]> ReadFsbAsync(AssetRecord asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath))
            throw new FileNotFoundException("Bank 源文件不存在。", asset.SourcePath);
        var index = ParseIndex(asset.LogicalPath);
        if (index < 0) throw new InvalidDataException("当前资源不是 fsb/<index> Bank 资源。");
        var data = await File.ReadAllBytesAsync(asset.SourcePath, cancellationToken);
        var info = BankParser.TryParse(data) ?? throw new InvalidDataException("无法解析 Bank 文件。");
        if (index >= info.FsbCount) throw new IndexOutOfRangeException($"FSB 索引超出范围: {index}");
        return data.AsSpan((int)info.FsbOffsets[index], (int)info.FsbSizes[index]).ToArray();
    }

    /// <summary>P2.1: read-only structural view of the selected FSB blob.
    /// Unknown or encrypted payloads are reported with a clear reason instead
    /// of guessed content.</summary>
    public async Task<BankFsbInspection> InspectFsbAsync(AssetRecord asset, CancellationToken cancellationToken = default)
    {
        var fsb = await ReadFsbAsync(asset, cancellationToken);
        if (fsb.Length >= 4 && !fsb.AsSpan()[..4].SequenceEqual("FSB5"u8))
            return new BankFsbInspection(fsb.LongLength, null,
                "该 FSB 不以 FSB5 魔数开头：可能是加密数据、旧版 FSB 或非音频负载。不会猜测其内容。", MagicMissing: true);
        var info = Fsb5Parser.TryParse(fsb);
        if (info is not null) return new BankFsbInspection(fsb.LongLength, info, null, MagicMissing: false);
        // surface the parser's specific layout objection instead of a generic message
        try
        {
            Fsb5Parser.Parse(fsb);
            return new BankFsbInspection(fsb.LongLength, null, "FSB5 头部无法解析（文件可能被截断）。", MagicMissing: false);
        }
        catch (InvalidDataException ex)
        {
            return new BankFsbInspection(fsb.LongLength, null, $"FSB5 结构解析失败：{ex.Message}", MagicMissing: false);
        }
    }

    public async Task<byte[]> DecodeToWaveAsync(AssetRecord asset, IFmodAudioCodec codec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!codec.IsAvailable) throw new NotSupportedException("FMOD/FSBANK 编解码器不可用，请在项目中配置合法 DLL 目录。");
        return await codec.DecodeFsbToWaveAsync(await ReadFsbAsync(asset, cancellationToken), cancellationToken);
    }

    private static int ParseIndex(string logicalPath)
    {
        var parts = logicalPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("fsb", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var index) ? index : -1;
    }
}
