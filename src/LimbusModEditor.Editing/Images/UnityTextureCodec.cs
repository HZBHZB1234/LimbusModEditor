using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Editing.Images;

public enum UnityTexturePixelFormat
{
    Rgb24 = 3,
    Rgba32 = 4,
    Dxt1 = 10,
    Dxt5 = 12,
    Bgra32 = 14
}

public sealed record UnityTextureInfo(int Width, int Height, UnityTexturePixelFormat Format, byte[] PixelData);

/// <summary>PNG conversion for Unity Texture2D formats. Supports uncompressed
/// RGB/RGBA/BGRA and managed DXT1/DXT5 block codecs.</summary>
public sealed class UnityTextureCodec
{
    public byte[] ToPng(UnityTextureInfo texture)
    {
        using var image = ToImage(texture); using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    public Image<Rgba32> ToImage(UnityTextureInfo texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0) throw new ArgumentOutOfRangeException(nameof(texture));
        if (texture.Format is UnityTexturePixelFormat.Dxt1 or UnityTexturePixelFormat.Dxt5) return DecodeDxt(texture);
        var channels = texture.Format == UnityTexturePixelFormat.Rgb24 ? 3 : 4;
        var expected = checked(texture.Width * texture.Height * channels);
        if (texture.PixelData.Length < expected) throw new InvalidDataException($"Texture2D pixel data is too short; expected {expected} bytes.");
        var image = new Image<Rgba32>(texture.Width, texture.Height); var offset = 0;
        for (var y = 0; y < texture.Height; y++) for (var x = 0; x < texture.Width; x++)
        {
            var r = texture.PixelData[offset++]; var g = texture.PixelData[offset++]; var b = texture.PixelData[offset++]; var a = channels == 4 ? texture.PixelData[offset++] : byte.MaxValue;
            if (texture.Format == UnityTexturePixelFormat.Bgra32) (r, b) = (b, r); image[x, y] = new Rgba32(r, g, b, a);
        }
        return image;
    }

    public UnityTextureInfo FromPng(ReadOnlySpan<byte> pngData, UnityTexturePixelFormat format = UnityTexturePixelFormat.Rgba32)
    {
        if (format is not (UnityTexturePixelFormat.Rgb24 or UnityTexturePixelFormat.Rgba32 or UnityTexturePixelFormat.Bgra32 or UnityTexturePixelFormat.Dxt1 or UnityTexturePixelFormat.Dxt5)) throw new NotSupportedException($"Unsupported Unity TextureFormat: {format}");
        using var image = Image.Load<Rgba32>(pngData);
        if (format is UnityTexturePixelFormat.Dxt1 or UnityTexturePixelFormat.Dxt5) return EncodeDxt(image, format);
        var channels = format == UnityTexturePixelFormat.Rgb24 ? 3 : 4; var pixels = new byte[checked(image.Width * image.Height * channels)]; var offset = 0;
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
        {
            var p = image[x, y]; var (r, g, b) = format == UnityTexturePixelFormat.Bgra32 ? (p.B, p.G, p.R) : (p.R, p.G, p.B); pixels[offset++] = r; pixels[offset++] = g; pixels[offset++] = b; if (channels == 4) pixels[offset++] = p.A;
        }
        return new(image.Width, image.Height, format, pixels);
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
            for (var py = 0; py < 4; py++) for (var px = 0; px < 4; px++) { var x = bx * 4 + px; var y = by * 4 + py; if (x < texture.Width && y < texture.Height) { var c = colors[py * 4 + px]; image[x, y] = new Rgba32(c.R, c.G, c.B, alphas[py * 4 + px]); } }
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
        for (var by = 0; by < bh; by++) for (var bx = 0; bx < bw; bx++) { for (var py = 0; py < 4; py++) for (var pxi = 0; pxi < 4; pxi++) px[py * 4 + pxi] = image[Math.Min(bx * 4 + pxi, image.Width - 1), Math.Min(by * 4 + py, image.Height - 1)]; if (format == UnityTexturePixelFormat.Dxt5) WriteAlphaBlock(writer, px); WriteColorBlock(writer, px, format == UnityTexturePixelFormat.Dxt1); }
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
