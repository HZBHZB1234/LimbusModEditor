using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LimbusModEditor.Editing.Images;

/// <summary>Unity Texture2D TextureFormat values (canonical numeric values
/// match UnityEngine.TextureFormat / battle-tested modding tooling).</summary>
public enum UnityTexturePixelFormat
{
    Alpha8 = 1,
    Argb4444 = 2,
    Rgb24 = 3,
    Rgba32 = 4,
    /// <summary>Serialized in A,R,G,B byte order (alpha first; UnityPy decodes
    /// it with Pillow rawmode "ARGB"). BGRA32 is the separate B,G,R,A format.</summary>
    Argb32 = 5,
    Rgb565 = 7,
    Bgr24 = 8,
    R16 = 9,
    Dxt1 = 10,
    Dxt5 = 12,
    Rgba4444 = 13,
    Bgra32 = 14,
    Rg16 = 62,
    R8 = 63
}

public sealed record UnityTextureInfo(int Width, int Height, UnityTexturePixelFormat Format, byte[] PixelData);

/// <summary>Per-format capability metadata surfaced in previews and docs:
/// lossiness, encode/decode support and mipmap behaviour.</summary>
public sealed record TextureFormatCapability(
    UnityTexturePixelFormat Format,
    string DisplayName,
    int BitsPerPixel,
    bool Lossless,
    bool EncodeSupported,
    string Notes);

/// <summary>PNG conversion for Unity Texture2D formats. Supports all
/// uncompressed formats (Alpha8, ARGB4444, RGB24, RGBA32, ARGB32, RGB565,
/// BGR24, R16, RGBA4444, BGRA32, RG16, R8) plus managed DXT1/DXT5 codecs.</summary>
///
/// <remarks>
/// <b>行序约定（上下颠倒的根因）</b>：Unity 的 Texture2D 像素负载<b>以左下角为原点</b>——
/// 负载里的第 0 行是图像<b>最下面</b>一行（与 GPU 纹理坐标一致），而 PNG / ImageSharp 的
/// 行序自上而下。两者相差一次上下翻转：
/// <list type="bullet">
/// <item>解码（<see cref="ToImage"/> / <see cref="ToPng"/> / <see cref="ToPngCropped"/>）
/// 必须翻一次，否则资源工作台里的纹理与 Sprite 子图都是倒的；</item>
/// <item>编码（<see cref="FromPng"/>）必须翻回去，保证「读出来 → 换图 → 写回去」的
/// 像素逐字节往返不变（写回后游戏里的朝向也就与原版一致）。</item>
/// </list>
/// 翻转只改变行序，不改变 <c>UnitySpriteCrop</c> 的坐标换算：那里的
/// <c>y = textureHeight - rectY - height</c> 正是「Unity 左下原点 → 图像左上原点」的换算，
/// 与这里的翻转互为配套（真实样本 <c>banner_MirrorDungeon7_en</c> 的口径）。
/// </remarks>
public sealed class UnityTextureCodec
{
    /// <summary>Unity 像素负载的行序以左下角为原点（第 0 行 = 图像最下面一行）。
    /// 保留成常量而不是散落的 <c>height - 1 - y</c>：这个约定是「图为什么是倒的」的唯一开关，
    /// 后续若要支持 <c>Texture2D</c> 的平台相关行序，只需在这里加分支。</summary>
    public const bool UnityPixelDataIsBottomUp = true;

    /// <summary>某一行（图像坐标，0 = 最上面一行）在负载字节里的行号。</summary>
    private static int StorageRow(int imageRow, int height)
        => UnityPixelDataIsBottomUp ? height - 1 - imageRow : imageRow;

    public byte[] ToPng(UnityTextureInfo texture)
    {
        using var image = ToImage(texture); using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    /// <summary>解码后按矩形裁剪再输出 PNG（Sprite 子图预览用）。坐标以图像
    /// 左上角为原点、已由调用方完成 Unity 左下原点换算与边界校验；
    /// 行序（自下而上 → 自上而下）由 <see cref="ToImage"/> 统一处理，调用方不必再翻。</summary>
    public byte[] ToPngCropped(UnityTextureInfo texture, int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        using var image = ToImage(texture);
        if (x < 0 || y < 0 || x + width > image.Width || y + height > image.Height)
            throw new ArgumentOutOfRangeException(nameof(x),
                $"裁剪区域 {x},{y} {width}×{height} 超出图像 {image.Width}×{image.Height}。");
        using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        using var output = new MemoryStream();
        crop.SaveAsPng(output);
        return output.ToArray();
    }

    /// <summary>解码为图像。<b>行序已翻正</b>（负载自下而上 → 图像自上而下，见类注释）。</summary>
    public Image<Rgba32> ToImage(UnityTextureInfo texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0) throw new ArgumentOutOfRangeException(nameof(texture));
        if (texture.Format is UnityTexturePixelFormat.Dxt1 or UnityTexturePixelFormat.Dxt5) return DecodeDxt(texture);
        var bytesPerPixel = BytesPerPixel(texture.Format);
        var expected = checked(texture.Width * texture.Height * bytesPerPixel);
        if (texture.PixelData.Length < expected) throw new InvalidDataException($"Texture2D pixel data is too short; expected {expected} bytes, got {texture.PixelData.Length}.");
        var image = new Image<Rgba32>(texture.Width, texture.Height);
        for (var y = 0; y < texture.Height; y++)
        {
            var row = StorageRow(y, texture.Height) * texture.Width * bytesPerPixel;
            for (var x = 0; x < texture.Width; x++)
                image[x, y] = ReadPixel(texture.Format, texture.PixelData, row + x * bytesPerPixel);
        }
        return image;
    }

    /// <summary>把图像编码回 Unity 负载。<b>行序翻回自下而上</b>，与 <see cref="ToImage"/> 对称。</summary>
    public UnityTextureInfo FromPng(ReadOnlySpan<byte> pngData, UnityTexturePixelFormat format = UnityTexturePixelFormat.Rgba32)
    {
        if (!IsEncodeSupported(format)) throw new NotSupportedException($"Unsupported Unity TextureFormat: {format}");
        using var image = Image.Load<Rgba32>(pngData);
        if (format is UnityTexturePixelFormat.Dxt1 or UnityTexturePixelFormat.Dxt5) return EncodeDxt(image, format);
        var bytesPerPixel = BytesPerPixel(format);
        var pixels = new byte[checked(image.Width * image.Height * bytesPerPixel)];
        for (var y = 0; y < image.Height; y++)
        {
            var row = StorageRow(y, image.Height) * image.Width * bytesPerPixel;
            for (var x = 0; x < image.Width; x++)
                WritePixel(format, pixels, row + x * bytesPerPixel, image[x, y]);
        }
        return new(image.Width, image.Height, format, pixels);
    }

    /// <summary>Whether this tool can both decode and encode the format.</summary>
    public static bool IsEncodeSupported(UnityTexturePixelFormat format) => Enum.IsDefined(format);

    public static int BytesPerPixel(UnityTexturePixelFormat format) => format switch
    {
        UnityTexturePixelFormat.Alpha8 or UnityTexturePixelFormat.R8 => 1,
        UnityTexturePixelFormat.Argb4444 or UnityTexturePixelFormat.Rgb565 or
            UnityTexturePixelFormat.R16 or UnityTexturePixelFormat.Rgba4444 or UnityTexturePixelFormat.Rg16 => 2,
        UnityTexturePixelFormat.Rgb24 or UnityTexturePixelFormat.Bgr24 => 3,
        UnityTexturePixelFormat.Rgba32 or UnityTexturePixelFormat.Argb32 or UnityTexturePixelFormat.Bgra32 => 4,
        _ => throw new NotSupportedException($"Unsupported Unity TextureFormat: {format}")
    };

    private static Rgba32 ReadPixel(UnityTexturePixelFormat format, byte[] data, int offset) => format switch
    {
        UnityTexturePixelFormat.Alpha8 => Gray(data[offset]),
        UnityTexturePixelFormat.R8 => Gray(data[offset]),
        UnityTexturePixelFormat.Argb4444 => Expand4444(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)), argbOrder: true),
        UnityTexturePixelFormat.Rgba4444 => Expand4444(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)), argbOrder: false),
        UnityTexturePixelFormat.Rgb24 => new Rgba32(data[offset], data[offset + 1], data[offset + 2], 255),
        UnityTexturePixelFormat.Rgba32 => new Rgba32(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]),
        // ARGB32 stores bytes in A,R,G,B order (UnityPy decodes it with Pillow
        // rawmode "ARGB"; BGRA32 is the separate B,G,R,A format).
        UnityTexturePixelFormat.Argb32 => new Rgba32(data[offset + 1], data[offset + 2], data[offset + 3], data[offset]),
        UnityTexturePixelFormat.Rgb565 => Expand565(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))),
        UnityTexturePixelFormat.Bgr24 => new Rgba32(data[offset + 2], data[offset + 1], data[offset], 255),
        UnityTexturePixelFormat.R16 => Gray(Scale16To8(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)))),
        // RG16 stores two 8-bit channels in one 16-bit pixel (2 bytes total).
        UnityTexturePixelFormat.Rg16 => new Rgba32(data[offset], data[offset + 1], 0, 255),
        UnityTexturePixelFormat.Bgra32 => new Rgba32(data[offset + 2], data[offset + 1], data[offset], data[offset + 3]),
        _ => throw new NotSupportedException($"Unsupported Unity TextureFormat: {format}")
    };

    private static void WritePixel(UnityTexturePixelFormat format, byte[] data, int offset, Rgba32 pixel)
    {
        switch (format)
        {
            case UnityTexturePixelFormat.Alpha8:
                data[offset] = pixel.A; break;
            case UnityTexturePixelFormat.R8:
                data[offset] = pixel.R; break; // R8 is a red-channel format, not alpha
            case UnityTexturePixelFormat.Argb4444:
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2),
                    (ushort)(((pixel.A / 17) << 12) | ((pixel.R / 17) << 8) | ((pixel.G / 17) << 4) | (pixel.B / 17)));
                break;
            case UnityTexturePixelFormat.Rgba4444:
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2),
                    (ushort)(((pixel.R / 17) << 12) | ((pixel.G / 17) << 8) | ((pixel.B / 17) << 4) | (pixel.A / 17)));
                break;
            case UnityTexturePixelFormat.Rgb24:
                data[offset] = pixel.R; data[offset + 1] = pixel.G; data[offset + 2] = pixel.B; break;
            case UnityTexturePixelFormat.Rgba32:
                data[offset] = pixel.R; data[offset + 1] = pixel.G; data[offset + 2] = pixel.B; data[offset + 3] = pixel.A; break;
            case UnityTexturePixelFormat.Argb32:
                data[offset] = pixel.A; data[offset + 1] = pixel.R; data[offset + 2] = pixel.G; data[offset + 3] = pixel.B; break;
            case UnityTexturePixelFormat.Rgb565:
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), Pack565(pixel)); break;
            case UnityTexturePixelFormat.Bgr24:
                data[offset] = pixel.B; data[offset + 1] = pixel.G; data[offset + 2] = pixel.R; break;
            case UnityTexturePixelFormat.R16:
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), (ushort)(Luminance(pixel) * 257)); break;
            case UnityTexturePixelFormat.Rg16:
                data[offset] = pixel.R; data[offset + 1] = pixel.G; break;
            case UnityTexturePixelFormat.Bgra32:
                data[offset] = pixel.B; data[offset + 1] = pixel.G; data[offset + 2] = pixel.R; data[offset + 3] = pixel.A; break;
            default: throw new NotSupportedException($"Unsupported Unity TextureFormat: {format}");
        }
    }

    private static byte Scale16To8(ushort value) => (byte)((value * 255 + 32767) / 65535);
    private static Rgba32 Gray(byte value) => new(value, value, value, 255);
    private static byte Luminance(Rgba32 p) => (byte)((p.R * 299 + p.G * 587 + p.B * 114) / 1000);
    private static Rgba32 Expand565(ushort value) => new(
        (byte)(((value >> 11) & 31) * 255 / 31),
        (byte)(((value >> 5) & 63) * 255 / 63),
        (byte)((value & 31) * 255 / 31),
        255);
    private static Rgba32 Expand4444(ushort value, bool argbOrder)
    {
        const int scale = 255 / 15;
        return argbOrder
            ? new Rgba32((byte)(((value >> 8) & 15) * scale), (byte)(((value >> 4) & 15) * scale), (byte)((value & 15) * scale), (byte)((value >> 12) * scale))
            : new Rgba32((byte)((value >> 12) * scale), (byte)(((value >> 8) & 15) * scale), (byte)(((value >> 4) & 15) * scale), (byte)((value & 15) * scale));
    }

    private static Image<Rgba32> DecodeDxt(UnityTextureInfo texture)
    {
        var blockBytes = texture.Format == UnityTexturePixelFormat.Dxt1 ? 8 : 16; var bw = (texture.Width + 3) / 4; var bh = (texture.Height + 3) / 4; var expected = checked(bw * bh * blockBytes);
        if (texture.PixelData.Length < expected) throw new InvalidDataException($"DXT data is too short; expected {expected} bytes.");
        var image = new Image<Rgba32>(texture.Width, texture.Height); var offset = 0;
        var colors = new Rgba32[16]; var alphas = new byte[16]; var ap = new byte[8];
        for (var by = 0; by < bh; by++) for (var bx = 0; bx < bw; bx++)
        {
            Array.Fill(alphas, (byte)255);
            if (texture.Format == UnityTexturePixelFormat.Dxt5)
            {
                var a0 = texture.PixelData[offset++]; var a1 = texture.PixelData[offset++]; ulong abits = 0; for (var i = 0; i < 6; i++) abits |= (ulong)texture.PixelData[offset++] << (8 * i);
                ap[0] = a0; ap[1] = a1; if (a0 > a1) { for (var i = 1; i < 7; i++) ap[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7); } else { for (var i = 1; i < 5; i++) ap[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); ap[6] = 0; ap[7] = 255; }
                for (var i = 0; i < 16; i++) alphas[i] = ap[(int)((abits >> (3 * i)) & 7)];
            }
            DecodeColorBlock(texture.PixelData, ref offset, colors);
            // 块行序同样是自下而上：负载里的块行 0 是块网格的最下面一行（见类注释）。
            for (var py = 0; py < 4; py++) for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px;
                var y = texture.Height - 1 - (by * 4 + py);
                if (x < texture.Width && y >= 0 && y < texture.Height)
                {
                    var c = colors[py * 4 + px];
                    image[x, y] = new Rgba32(c.R, c.G, c.B, alphas[py * 4 + px]);
                }
            }
        }
        return image;
    }

    private static void DecodeColorBlock(byte[] data, ref int offset, Span<Rgba32> output)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)); var c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2)); var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4)); offset += 8;
        var p0 = Rgb565(c0, 255); var p1 = Rgb565(c1, 255); var palette = new Rgba32[4] { p0, p1, c0 > c1 ? Mix(p0, p1, 2, 1) : Mix(p0, p1, 1, 1), c0 > c1 ? Mix(p0, p1, 1, 2) : new Rgba32(0, 0, 0, 0) };
        for (var i = 0; i < 16; i++) output[i] = palette[(int)((bits >> (2 * i)) & 3)];
    }

    private static UnityTextureInfo EncodeDxt(Image<Rgba32> image, UnityTexturePixelFormat format)
    {
        var bw = (image.Width + 3) / 4; var bh = (image.Height + 3) / 4; using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var px = new Rgba32[16];
        for (var by = 0; by < bh; by++) for (var bx = 0; bx < bw; bx++)
        {
            for (var py = 0; py < 4; py++) for (var pxi = 0; pxi < 4; pxi++)
            {
                // 块行序自下而上（与 DecodeDxt 对称）：先算负载行号（超界按边缘行复制），
                // 再换算成图像行号取像素。
                var x = Math.Min(bx * 4 + pxi, image.Width - 1);
                var storageRow = Math.Min(by * 4 + py, image.Height - 1);
                px[py * 4 + pxi] = image[x, image.Height - 1 - storageRow];
            }
            if (format == UnityTexturePixelFormat.Dxt5) WriteAlphaBlock(writer, px); WriteColorBlock(writer, px, format == UnityTexturePixelFormat.Dxt1);
        }
        return new(image.Width, image.Height, format, stream.ToArray());
    }

    private static void WriteColorBlock(BinaryWriter w, Span<Rgba32> px, bool allowAlpha)
    {
        var min = px[0]; var max = px[0]; var hasAlpha = false; foreach (var p in px) { hasAlpha |= p.A < 128; min = new Rgba32(Math.Min(min.R, p.R), Math.Min(min.G, p.G), Math.Min(min.B, p.B), 255); max = new Rgba32(Math.Max(max.R, p.R), Math.Max(max.G, p.G), Math.Max(max.B, p.B), 255); }
        var c0 = Pack565(max); var c1 = Pack565(min); if (allowAlpha && hasAlpha) { if (c0 >= c1) { (c0, c1) = (c1, c0); if (c0 == c1) c1 = c0 == ushort.MaxValue ? (ushort)(c0 - 1) : (ushort)(c0 + 1); } } else if (c0 <= c1) (c0, c1) = (c1, c0);
        var p0 = Rgb565(c0, 255); var p1 = Rgb565(c1, 255); var palette = c0 > c1 ? new[] { p0, p1, Mix(p0, p1, 2, 1), Mix(p0, p1, 1, 2) } : new[] { p0, p1, Mix(p0, p1, 1, 1), new Rgba32(0, 0, 0, 0) }; uint bits = 0;
        for (var i = 0; i < 16; i++) { var best = 3; if (!(allowAlpha && hasAlpha && px[i].A < 128)) { var bestD = uint.MaxValue; for (var j = 0; j < (c0 > c1 ? 4 : 3); j++) { var d = Dist(px[i], palette[j]); if (d < bestD) { bestD = d; best = j; } } } bits |= (uint)best << (2 * i); }
        w.Write(c0); w.Write(c1); w.Write(bits);
    }

    private static void WriteAlphaBlock(BinaryWriter w, Span<Rgba32> px)
    { w.Write((byte)0); w.Write(byte.MaxValue); ulong bits = 0; for (var i = 0; i < 16; i++) bits |= (ulong)Math.Clamp((px[i].A * 7 + 127) / 255, 0, 7) << (3 * i); for (var i = 0; i < 6; i++) w.Write((byte)(bits >> (8 * i))); }
    private static uint Dist(Rgba32 a, Rgba32 b) => (uint)((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));
    private static ushort Pack565(Rgba32 p) => (ushort)((((p.R * 31 + 127) / 255) << 11) | (((p.G * 63 + 127) / 255) << 5) | ((p.B * 31 + 127) / 255));
    private static Rgba32 Rgb565(ushort c, byte a) => new((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), a);
    private static Rgba32 Mix(Rgba32 a, Rgba32 b, int wa, int wb) => new((byte)((a.R * wa + b.R * wb) / (wa + wb)), (byte)((a.G * wa + b.G * wb) / (wa + wb)), (byte)((a.B * wa + b.B * wb) / (wa + wb)), 255);
}

/// <summary>Static capability notes for every supported texture format, used
/// by previews and docs so users see lossiness and mipmap behaviour up front.</summary>
public static class TextureFormatCatalog
{
    public static TextureFormatCapability Describe(UnityTexturePixelFormat format) => format switch
    {
        UnityTexturePixelFormat.Alpha8 => new(format, "Alpha8", 8, true, true, "单通道透明度；预览以灰度显示，替换时写入 PNG 的 Alpha 通道值。"),
        UnityTexturePixelFormat.Argb4444 => new(format, "ARGB4444", 16, false, true, "每通道 4 位量化（有损）；字节序为小端 16 位，A 高 4 位、B 低 4 位。"),
        UnityTexturePixelFormat.Rgb24 => new(format, "RGB24", 24, true, true, "无 Alpha。"),
        UnityTexturePixelFormat.Rgba32 => new(format, "RGBA32", 32, true, true, "无压缩，最常用。"),
        UnityTexturePixelFormat.Argb32 => new(format, "ARGB32", 32, true, true, "无压缩；字节序 A,R,G,B（alpha 在前，与 BGRA32 的 B,G,R,A 是两种格式）。"),
        UnityTexturePixelFormat.Rgb565 => new(format, "RGB565", 16, false, true, "R5/G6/B5 量化（有损）；无 Alpha。"),
        UnityTexturePixelFormat.Bgr24 => new(format, "BGR24", 24, true, true, "字节序 B,G,R。"),
        UnityTexturePixelFormat.R16 => new(format, "R16", 16, true, true, "单通道 16 位；预览以灰度显示。"),
        UnityTexturePixelFormat.Dxt1 => new(format, "DXT1 (BC1)", 4, false, true, "块压缩量化（有损）；无 Alpha（或 1 位穿透）。每 4×4 块 8 字节。"),
        UnityTexturePixelFormat.Dxt5 => new(format, "DXT5 (BC3)", 8, false, true, "块压缩量化（有损）。每 4×4 块 16 字节。"),
        UnityTexturePixelFormat.Rgba4444 => new(format, "RGBA4444", 16, false, true, "每通道 4 位量化（有损）；R 高 4 位、A 低 4 位。"),
        UnityTexturePixelFormat.Bgra32 => new(format, "BGRA32", 32, true, true, "无压缩；字节序 B,G,R,A。"),
        UnityTexturePixelFormat.Rg16 => new(format, "RG16", 16, true, true, "双通道 8 位；蓝通道显示为 0。"),
        _ => new(format, format.ToString(), 0, true, true, "未收录说明。")
    };

    public static IReadOnlyList<TextureFormatCapability> All() =>
    [
        Describe(UnityTexturePixelFormat.Alpha8), Describe(UnityTexturePixelFormat.Argb4444),
        Describe(UnityTexturePixelFormat.Rgb24), Describe(UnityTexturePixelFormat.Rgba32),
        Describe(UnityTexturePixelFormat.Argb32), Describe(UnityTexturePixelFormat.Rgb565),
        Describe(UnityTexturePixelFormat.Bgr24), Describe(UnityTexturePixelFormat.R16),
        Describe(UnityTexturePixelFormat.Dxt1), Describe(UnityTexturePixelFormat.Dxt5),
        Describe(UnityTexturePixelFormat.Rgba4444), Describe(UnityTexturePixelFormat.Bgra32),
        Describe(UnityTexturePixelFormat.Rg16), Describe(UnityTexturePixelFormat.R8)
    ];
}

/// <summary>Mipmap layout math for Unity Texture2D payload data: level 0 is
/// full size, each following level halves both dimensions. Block-compressed
/// formats keep whole 4×4 blocks per level.</summary>
public static class UnityTextureMipmaps
{
    public sealed record MipLevel(int Level, int Width, int Height, int Offset, int Size);

    public static int BytesPerPixelOrBlockBits(UnityTexturePixelFormat format, int width, int height)
    {
        if (format is UnityTexturePixelFormat.Dxt1) return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
        if (format is UnityTexturePixelFormat.Dxt5) return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
        return width * height * UnityTextureCodec.BytesPerPixel(format);
    }

    /// <summary>Builds the per-level layout for a mip chain; the offset/size of
    /// each level is deterministic from width/height/format.</summary>
    public static MipLevel[] CalculateLevels(int width, int height, UnityTexturePixelFormat format, int mipCount)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (mipCount <= 0) throw new ArgumentOutOfRangeException(nameof(mipCount));
        var levels = new MipLevel[mipCount];
        var offset = 0;
        var w = width; var h = height;
        for (var level = 0; level < mipCount; level++)
        {
            var size = BytesPerPixelOrBlockBits(format, w, h);
            levels[level] = new MipLevel(level, w, h, offset, size);
            offset += size;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        return levels;
    }

    /// <summary>Infers how many mip levels fit in the given payload (at least 1).</summary>
    public static int EstimateMipCount(int width, int height, UnityTexturePixelFormat format, int dataLength)
    {
        var maxMips = 1 + (int)Math.Floor(Math.Log2((double)Math.Max(width, height)));
        var count = 1;
        while (count < maxMips)
        {
            var total = CalculateLevels(width, height, format, count + 1).Sum(l => (long)l.Size);
            if (total > dataLength) break;
            count++;
        }
        return count;
    }

    /// <summary>Slices one mip level into its own decodable texture.</summary>
    public static UnityTextureInfo Slice(UnityTextureInfo texture, int level)
    {
        var count = EstimateMipCount(texture.Width, texture.Height, texture.Format, texture.PixelData.Length);
        if (level < 0 || level >= count) throw new ArgumentOutOfRangeException(nameof(level), $"mipmap 层 {level} 超出范围（0..{count - 1}）。");
        var levels = CalculateLevels(texture.Width, texture.Height, texture.Format, count);
        var mip = levels[level];
        var data = new byte[mip.Size];
        Array.Copy(texture.PixelData, mip.Offset, data, 0, mip.Size);
        return new UnityTextureInfo(mip.Width, mip.Height, texture.Format, data);
    }
}
