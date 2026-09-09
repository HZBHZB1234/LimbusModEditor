using System.Buffers.Binary;
using System.Text;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Application.Assets;

/// <summary>plan-06 判定出的 bank 类型。</summary>
public enum BankKind
{
    /// <summary>事件 bank：RIFF/FEV 结构、SNDH 表为空、无 FSB 负载。</summary>
    Event,
    /// <summary>音频 bank：SNDH 表携带指向 FSB5 负载的 (offset, size) 条目。</summary>
    Audio,
    /// <summary>加密 bank：FSB 负载不以 FSB5 魔数开头（BankParser 同款判定）。</summary>
    Encrypted,
    /// <summary>无法识别：不是受支持的 RIFF/FEV bank，或结构损坏。</summary>
    Unknown,
}

/// <summary>扫描得到的一条 bank 文件记录（引用模式，不复制文件）。</summary>
public sealed record BankFileEntry(
    string FileName,
    string FullPath,
    long FileSizeBytes,
    BankKind Kind,
    int FsbCount,
    string? Detail = null)
{
    /// <summary>中文类型标签（UI 显示用）。</summary>
    public string KindLabel => Kind switch
    {
        BankKind.Event => "事件 bank",
        BankKind.Audio => "音频 bank",
        BankKind.Encrypted => "加密 bank",
        _ => "无法识别",
    };
}

/// <summary>事件 bank 的一个 RIFF 顶层块（LIST/SNDH/DEL 等，不做嵌套展开）。</summary>
public sealed record RiffChunkInfo(int Index, string FourCc, long HeaderOffset, uint PayloadSize);

/// <summary>音频 bank 中第 <see cref="FsbIndex"/> 个 FSB 负载的解析结果；
/// 结构解析失败时 <see cref="Fsb"/> 为 null 并给出中文原因（不猜测内容）。</summary>
public sealed record BankFsbTable(int FsbIndex, long Offset, long Size, Fsb5Info? Fsb, string? UnresolvedReason);

/// <summary><see cref="BankDirectoryService.ReadSampleTable"/> 的结果：
/// 音频 bank 填 <see cref="FsbTables"/>（样本表经 Fsb5Parser，codec 为 bank 级字段），
/// 事件 bank 填 <see cref="RiffChunks"/> 与说明，二者互斥。</summary>
public sealed record BankSampleTable(
    string BankPath,
    BankKind Kind,
    long FileSizeBytes,
    IReadOnlyList<BankFsbTable> FsbTables,
    IReadOnlyList<RiffChunkInfo> RiffChunks,
    string? Note)
{
    /// <summary>音频 bank 的全部样本（跨 FSB 展平），供 UI 直接绑定。</summary>
    public IEnumerable<Fsb5SampleInfo> Samples => FsbTables.SelectMany(t => t.Fsb?.Samples ?? []);
}

/// <summary>
/// plan-06 后端服务：FMOD bank 目录解析 / 引用模式扫描 / 样本表读取。
/// 全部方法 UI 无关；只读磁盘，从不写游戏目录。
/// 扫描采用「BankParser.TryParse 前缀快路径 + 流式 SNDH 探测回退」：
/// BankParser.TryParse 会用传入缓冲区长度校验 FSB 表项边界，因此大文件只读
/// 头部前缀直接喂它会把合法音频 bank 误判为 null，此时回退到流式探测
/// （每文件仅读 0x30 头 + 少量块头 + SNDH 表 + 4 字节魔数，1531 个文件秒级）。
/// 流式探测逐门复刻 BankParser.TryParse 的校验语义，保证两路径判定一致。
/// </summary>
public sealed class BankDirectoryService
{
    private const int PrefixProbeBytes = 64 * 1024;
    private const int MaxTopLevelChunks = 4096;
    private const int MaxSndhTableBytes = 4 * 1024 * 1024;

    /// <summary>游戏目录到 FMOD bank 目录的固定相对路径。</summary>
    public static readonly string[] BankRelativePath =
        ["LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMODBuilds", "Desktop"];

    /// <summary>解析 &lt;游戏目录&gt;/LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop；
    /// 游戏目录为空或该目录不存在时返回 null（UI 据此提示引导设置）。</summary>
    public string? ResolveBankDirectory(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        var root = gameDirectory.Trim().Trim('"');
        if (root.Length == 0) return null;
        var path = Path.Combine([root, .. BankRelativePath]);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>引用模式扫描目录下全部 *.bank（仅顶层）。每个文件只读头部做
    /// BankParser.TryParse（不全量解析），损坏文件标记为 <see cref="BankKind.Unknown"/>
    /// 并带原因，绝不抛异常。结果按文件名排序。</summary>
    public IReadOnlyList<BankFileEntry> ScanDirectory(string bankDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankDirectory);
        if (!Directory.Exists(bankDirectory))
            throw new DirectoryNotFoundException($"bank 目录不存在：{bankDirectory}");
        var files = Directory.EnumerateFiles(bankDirectory, "*.bank", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f).Equals(".bank", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        return files.Select(ScanOne).ToList();
    }

    /// <summary>读取单个 bank 的样本表/块清单：
    /// 音频 bank → 逐 FSB 经 Fsb5Parser 给出样本（名称/codec/采样率/声道/样本数/偏移/大小）；
    /// 事件 bank → RIFF 顶层块清单与说明；加密/无法识别 → 抛出明确中文异常，不解码。</summary>
    public BankSampleTable ReadSampleTable(string bankPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankPath);
        var fileInfo = new FileInfo(bankPath);
        if (!fileInfo.Exists) throw new FileNotFoundException("bank 文件不存在。", bankPath);

        var outcome = ProbeBankFile(bankPath, collectChunks: true);
        if (!outcome.IsBank)
            throw new InvalidOperationException($"无法识别的 bank 文件（{outcome.FailReason}），拒绝解析。");
        if (outcome.Kind == BankKind.Encrypted)
            throw new InvalidOperationException("该 bank 已加密（FSB 负载缺少 FSB5 魔数），不解码加密内容。");

        if (outcome.Kind == BankKind.Event)
        {
            return new BankSampleTable(bankPath, BankKind.Event, fileInfo.Length, [],
                outcome.Chunks, $"事件 bank（无 FSB 负载），共 {outcome.Chunks.Count} 个 RIFF 顶层块。");
        }

        using var stream = OpenRead(bankPath);
        var tables = new List<BankFsbTable>(outcome.FsbEntries.Count);
        for (var i = 0; i < outcome.FsbEntries.Count; i++)
        {
            var (offset, size) = outcome.FsbEntries[i];
            tables.Add(ReadFsbTable(stream, fileInfo.Length, i, offset, size));
        }
        return new BankSampleTable(bankPath, BankKind.Audio, fileInfo.Length, tables, [],
            $"音频 bank，共 {tables.Count} 个 FSB，{tables.Sum(t => t.Fsb?.SampleCount ?? 0)} 个样本。");
    }

    private static BankFileEntry ScanOne(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            var info = TryParsePrefix(path, length);
            if (info is not null)
            {
                var kind = info.IsEncrypted ? BankKind.Encrypted
                    : info.FsbCount > 0 ? BankKind.Audio
                    : BankKind.Event;
                return new BankFileEntry(Path.GetFileName(path), path, length, kind, info.FsbCount);
            }
            // 前缀内校验不通过：文件头部损坏，或 FSB 负载/深层块超出前缀。
            // 流式探测复刻 TryParse 全部校验门并使用真实文件长度，能正确分类后者。
            var outcome = ProbeBankFile(path, collectChunks: false);
            return outcome.IsBank
                ? new BankFileEntry(Path.GetFileName(path), path, length, outcome.Kind, outcome.FsbCount)
                : new BankFileEntry(Path.GetFileName(path), path, length, BankKind.Unknown, 0, outcome.FailReason);
        }
        catch (Exception ex)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception) { /* 尺寸不可知则记 0 */ }
            return new BankFileEntry(Path.GetFileName(path), path, size, BankKind.Unknown, 0, $"扫描异常：{ex.Message}");
        }
    }

    /// <summary>快路径：读文件头部前缀喂 BankParser.TryParse。TryParse 可能对恶意
    /// 尺寸抛 OverflowException（checked 转型），这里一并吞掉视为不匹配。</summary>
    private static BankInfo? TryParsePrefix(string path, long fileLength)
    {
        try
        {
            var toRead = (int)Math.Min(fileLength, PrefixProbeBytes);
            if (toRead < 0x24) return null;
            var prefix = new byte[toRead];
            using var stream = OpenRead(path);
            if (!ReadExactly(stream, prefix)) return null;
            return BankParser.TryParse(prefix);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BankFsbTable ReadFsbTable(Stream stream, long fileLength, int index, uint offset, uint size)
    {
        if (offset >= (ulong)fileLength || (long)offset + size > fileLength)
            return new BankFsbTable(index, offset, size, null, "FSB 区域越界（文件可能在扫描后被改动）。");
        if (size > int.MaxValue)
            return new BankFsbTable(index, offset, size, null, "FSB 大小超出单次可读范围（> 2GB）。");
        stream.Seek(offset, SeekOrigin.Begin);
        var region = new byte[size];
        if (!ReadExactly(stream, region))
            return new BankFsbTable(index, offset, size, null, "FSB 区域读取被截断。");

        var fsb = Fsb5Parser.TryParse(region);
        if (fsb is not null) return new BankFsbTable(index, offset, size, fsb, null);
        // 借严格解析给出具体原因（与 BankAudioService.InspectFsbAsync 同模式）
        try
        {
            Fsb5Parser.Parse(region);
            return new BankFsbTable(index, offset, size, null, "FSB5 头部无法解析（文件可能被截断）。");
        }
        catch (Exception ex)
        {
            return new BankFsbTable(index, offset, size, null, $"FSB5 结构解析失败：{ex.Message}");
        }
    }

    /// <summary>流式探测：逐门复刻 BankParser.TryParse 的校验语义（RIFF/FEV 魔数、
    /// 0x14 非 0、LIST/PROJ/BNKI、PROJ 尺寸、SNDH 表格式与边界、FSB5 魔数加密判定），
    /// 但只做定向读取，不要求整个文件进内存。</summary>
    private static ProbeOutcome ProbeBankFile(string path, bool collectChunks)
    {
        using var stream = OpenRead(path);
        return ProbeBank(stream, collectChunks);
    }

    private static ProbeOutcome ProbeBank(Stream stream, bool collectChunks)
    {
        var outcome = new ProbeOutcome();
        var fileLength = stream.Length;
        if (fileLength < 0x30) return outcome.Fail("文件过小（< 0x30 字节），不构成 RIFF/FEV bank。");

        Span<byte> header = stackalloc byte[0x30];
        if (!ReadExactly(stream, header)) return outcome.Fail("头部读取被截断。");
        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("FEV "u8))
            return outcome.Fail("缺少 RIFF/FEV 魔数，不是 FMOD bank。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[0x14..]) == 0)
            return outcome.Fail("0x14 处头部字段为 0（与 BankParser 校验门一致），拒绝识别。");
        if (!header.Slice(0x1C, 4).SequenceEqual("LIST"u8) ||
            !header.Slice(0x24, 4).SequenceEqual("PROJ"u8) ||
            !header.Slice(0x28, 4).SequenceEqual("BNKI"u8))
            return outcome.Fail("缺少 LIST/PROJ/BNKI 结构块，不是受支持的 FEV bank。");

        var projectSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
        if (projectSize > int.MaxValue - 4) return outcome.Fail("PROJ 块尺寸超出可处理范围。");
        var pos = 0x30L + projectSize;

        if (collectChunks)
        {
            // 序言两个顶层块：0x0C 头块与 0x1C 的 LIST（门校验已确认存在），供事件 bank 块清单
            outcome.Chunks.Add(new RiffChunkInfo(0, Encoding.ASCII.GetString(header.Slice(0x0C, 4)), 0x0C,
                BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..])));
            outcome.Chunks.Add(new RiffChunkInfo(1, "LIST", 0x1C,
                BinaryPrimitives.ReadUInt32LittleEndian(header[0x20..])));
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        var walked = 0;
        while (pos + 8 <= fileLength)
        {
            if (++walked > MaxTopLevelChunks)
            {
                if (outcome.Kind == BankKind.Event) break;
                return outcome.Fail($"顶层块数量超过 {MaxTopLevelChunks}，遍历中止。");
            }
            stream.Seek(pos, SeekOrigin.Begin);
            if (!ReadExactly(stream, chunkHeader))
            {
                if (outcome.Kind == BankKind.Event) break;
                return outcome.Fail("块头读取被截断。");
            }
            var fourCc = Encoding.ASCII.GetString(chunkHeader[..4]);
            var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var payloadOffset = pos + 8;
            if (collectChunks)
                outcome.Chunks.Add(new RiffChunkInfo(outcome.Chunks.Count, fourCc, pos, payloadSize));

            if (fourCc == "SNDH")
            {
                if (payloadSize == 0)
                {
                    // 事件 bank：SNDH 为空即无 FSB 负载（BankParser 同款判定），继续收集剩余块
                    outcome.MarkEvent();
                    pos = payloadOffset;
                    continue;
                }
                if (payloadSize < 4 || (payloadSize - 4) % 8 != 0)
                    return outcome.Fail("SNDH 表尺寸非法（必须为 4 + 8*N 字节）。");
                if (payloadSize > MaxSndhTableBytes || payloadOffset + payloadSize > fileLength)
                    return outcome.Fail("SNDH 表越界或被截断。");
                var table = new byte[payloadSize];
                stream.Seek(payloadOffset, SeekOrigin.Begin);
                if (!ReadExactly(stream, table)) return outcome.Fail("SNDH 表读取被截断。");
                var count = (int)((payloadSize - 4) / 8);
                if (count == 0) return outcome.Fail("SNDH 表条目数为 0。");
                var entries = new (uint Offset, uint Size)[count];
                for (var i = 0; i < count; i++)
                {
                    entries[i] = (
                        BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 + i * 8)),
                        BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 + i * 8 + 4)));
                }
                if (entries[0].Offset == 0) return outcome.Fail("SNDH 表首个 FSB 偏移为 0。");
                foreach (var (offset, size) in entries)
                    if ((ulong)offset + size > (ulong)fileLength)
                        return outcome.Fail($"FSB 表项越界（0x{offset:X}+0x{size:X} 超出文件长度 0x{fileLength:X}）。");

                // 加密判定与 BankParser 一致：首个 FSB 必须以 FSB5 魔数开头
                var magic = new byte[4];
                stream.Seek(entries[0].Offset, SeekOrigin.Begin);
                var encrypted = (long)entries[0].Offset + 4 > fileLength
                    || !ReadExactly(stream, magic)
                    || !magic.AsSpan().SequenceEqual("FSB5"u8);
                if (encrypted) outcome.MarkEncrypted(count, entries);
                else outcome.MarkAudio(count, entries);
                return outcome;
            }

            if (payloadSize > int.MaxValue)
            {
                if (outcome.Kind == BankKind.Event) break;
                return outcome.Fail($"块 {fourCc} 尺寸超出 int 范围。");
            }
            if (payloadOffset + payloadSize > fileLength)
            {
                if (outcome.Kind == BankKind.Event) break;
                return outcome.Fail($"块 {fourCc} 越界（可能被截断或损坏）。");
            }
            pos = payloadOffset + payloadSize;
        }

        return outcome.Kind == BankKind.Event
            ? outcome
            : outcome.Fail("未找到 SNDH 表（已遍历到文件末尾）。");
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    /// <summary>探测结果内部载体。</summary>
    private sealed class ProbeOutcome
    {
        public string? FailReason { get; private set; }
        public BankKind Kind { get; private set; } = BankKind.Unknown;
        public int FsbCount { get; private set; }
        public List<(uint Offset, uint Size)> FsbEntries { get; } = [];
        public List<RiffChunkInfo> Chunks { get; } = [];
        public bool IsBank => FailReason is null;

        public ProbeOutcome Fail(string reason)
        {
            FailReason = reason;
            Kind = BankKind.Unknown;
            return this;
        }

        public void MarkEvent() => Kind = BankKind.Event;

        public void MarkEncrypted(int count, IReadOnlyList<(uint Offset, uint Size)> entries)
        {
            Kind = BankKind.Encrypted;
            FsbCount = count;
            FsbEntries.AddRange(entries);
        }

        public void MarkAudio(int count, IReadOnlyList<(uint Offset, uint Size)> entries)
        {
            Kind = BankKind.Audio;
            FsbCount = count;
            FsbEntries.AddRange(entries);
        }
    }
}
