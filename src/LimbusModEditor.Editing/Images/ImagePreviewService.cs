using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LimbusModEditor.Editing.Images;

public sealed record ImagePreviewResult(
    int Width,
    int Height,
    string Format,
    byte[] ThumbnailPng,
    bool HasAlpha);

/// <summary>
/// Image decoding and thumbnail generation boundary used by the editor UI.
/// Unity Texture2D/Sprite adapters can feed encoded image bytes here without
/// coupling the UI to ImageSharp or Unity internals.
/// </summary>
public sealed class ImagePreviewService
{
    public static bool IsSupportedExtension(string? extension)
        => extension is not null && extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga";

    public ImagePreviewResult CreatePreview(ReadOnlySpan<byte> data, int maxWidth = 480, int maxHeight = 320)
    {
        if (maxWidth <= 0 || maxHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        var format = Image.DetectFormat(data);
        using var image = Image.Load<Rgba32>(data);
        var width = image.Width;
        var height = image.Height;
        if (width > maxWidth || height > maxHeight)
        {
            var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
            image.Mutate(ctx => ctx.Resize(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale))));
        }
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return new(width, height, format.Name, output.ToArray(), HasAlpha(image));
    }

    public ImagePreviewResult CreatePreviewFromFile(string path, int maxWidth = 480, int maxHeight = 320)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CreatePreview(File.ReadAllBytes(path), maxWidth, maxHeight);
    }

    private static bool HasAlpha(Image<Rgba32> image)
    {
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image[x, y].A != byte.MaxValue) return true;
        return false;
    }
}
