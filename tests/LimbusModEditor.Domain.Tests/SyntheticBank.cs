using System.Buffers.Binary;
using System.Text;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 合成 FMOD bank 构造器（从 <c>BankDirectoryServiceTests</c> 提取为共享夹具，plan-11 复用）：
/// 布局与真实 FEV bank 的结构门一致，因此能被 <c>BankParser</c> / <c>BankDirectoryService</c>
/// 正常判定；不依赖本机游戏目录，可在任何机器上跑。
/// </summary>
internal static class SyntheticBank
{
    /// <summary>最小合法 FSB5（版本 1、0 样本、可指定 codec）：
    /// 0x3C 头 = 魔数/版本/样本数/条目区/名称区/数据区/codec + flags + hash + 8B 尾。</summary>
    public static byte[] MinimalFsb5(uint codec = 16)
    {
        var fsb = new byte[0x3C];
        Encoding.ASCII.GetBytes("FSB5").CopyTo(fsb, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fsb.AsSpan(0x04), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fsb.AsSpan(0x18), codec);
        return fsb;
    }

    /// <summary>合成 bank：RIFF(FEV ){ FAKE[8B] LIST(PROJ/BNKI[4B]) SNDH{...} [DEL ] [FSB] }。
    /// <paramref name="fsb"/> 为 null → 事件 bank（空 SNDH + DEL 块）；否则音频 bank，FSB 附在 0x48。</summary>
    public static byte[] Create(byte[]? fsb)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(0u);                              // 0x00 RIFF 尺寸稍后回填
        bw.Write("FEV "u8);                                            // 0x08
        bw.Write("FAKE"u8); bw.Write(8u); bw.Write(1u); bw.Write(0u);  // 0x0C chunk0（0x14 处非 0，过 BankParser 门）
        bw.Write("LIST"u8); bw.Write(16u);                             // 0x1C
        bw.Write("PROJ"u8); bw.Write("BNKI"u8); bw.Write(4u); bw.Write(0x6B6E6942u); // 0x24 起
        var offsetFieldPos = 0L;
        if (fsb is null)
        {
            bw.Write("SNDH"u8); bw.Write(0u);                          // 0x34 空 SNDH → 事件 bank
            bw.Write("DEL "u8); bw.Write(0u);                          // 0x3C
        }
        else
        {
            bw.Write("SNDH"u8); bw.Write(12u);                         // 0x34
            bw.Write(1u);                                              // 0x3C 被解析器跳过的 u32
            offsetFieldPos = bw.Seek(0, SeekOrigin.Current);           // 0x40 FSB 偏移字段
            bw.Write(0u); bw.Write((uint)fsb.Length);                  // (offset, size)
            bw.Write(fsb);                                             // 0x48
        }
        var bytes = ms.ToArray();
        if (fsb is not null)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)offsetFieldPos), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x04), (uint)(bytes.Length - 8)); // RIFF 尺寸
        return bytes;
    }

    /// <summary>写一个音频 bank 文件（<paramref name="codec"/> 默认 16 = Vorbis）。</summary>
    public static string WriteAudioBank(string directory, string fileName, uint codec = 16)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Create(MinimalFsb5(codec)));
        return path;
    }

    /// <summary>写一个事件 bank 文件（空 SNDH，无 FSB 负载）。</summary>
    public static string WriteEventBank(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Create(null));
        return path;
    }

    /// <summary>写一个不是 bank 的垃圾文件（判定应为「无法识别」）。</summary>
    public static string WriteGarbageFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("NOT-A-BANK-AT-ALL"));
        return path;
    }
}
