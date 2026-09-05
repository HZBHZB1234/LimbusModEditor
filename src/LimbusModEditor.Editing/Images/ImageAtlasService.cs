using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LimbusModEditor.Editing.Images;

public sealed class ImageAtlasService
{
    public ImageSplitResult SplitGrid(ReadOnlySpan<byte> imageData, int columns, int rows, string idPrefix = "region")
    {
        if (columns <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        using var image = Image.Load<Rgba32>(imageData);
        var regions = new List<ImageRegion>(columns * rows);
        var result = new ImageSplitResult { Layout = new ImageLayout(image.Width, image.Height, regions) };
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = column * image.Width / columns;
                var y = row * image.Height / rows;
                var right = (column + 1) * image.Width / columns;
                var bottom = (row + 1) * image.Height / rows;
                var region = new ImageRegion($"{idPrefix}_{row}_{column}", x, y, right - x, bottom - y);
                regions.Add(region);
                using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(region.X, region.Y, region.Width, region.Height)));
                using var output = new MemoryStream();
                crop.Save(output, new PngEncoder());
                result.Regions.Add(region.Id, output.ToArray());
            }
        return result;
    }

    public ImageSplitResult Split(ReadOnlySpan<byte> imageData, IEnumerable<ImageRegion> regions)
    {
        using var image = Image.Load<Rgba32>(imageData);
        var list = regions.ToList();
        ValidateRegions(image.Width, image.Height, list);
        var result = new ImageSplitResult { Layout = new ImageLayout(image.Width, image.Height, list) };
        foreach (var region in list)
        {
            using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(region.X, region.Y, region.Width, region.Height)));
            using var output = new MemoryStream(); crop.Save(output, new PngEncoder()); result.Regions.Add(region.Id, output.ToArray());
        }
        return result;
    }

    public byte[] Repack(ImageLayout layout, IReadOnlyDictionary<string, byte[]> regions, Rgba32? background = null)
    {
        if (layout.Width <= 0 || layout.Height <= 0) throw new ArgumentOutOfRangeException(nameof(layout));
        using var canvas = new Image<Rgba32>(layout.Width, layout.Height, background ?? new Rgba32(0, 0, 0, 0));
        foreach (var region in layout.Regions)
        {
            if (!regions.TryGetValue(region.Id, out var data)) throw new InvalidDataException($"缺少图像区域: {region.Id}");
            using var source = Image.Load<Rgba32>(data);
            if (source.Width != region.Width || source.Height != region.Height) throw new InvalidDataException($"图像区域尺寸不匹配: {region.Id}");
            canvas.Mutate(ctx => ctx.DrawImage(source, new Point(region.X, region.Y), 1f));
        }
        using var output = new MemoryStream(); canvas.Save(output, new PngEncoder()); return output.ToArray();
    }

    private static void ValidateRegions(int width, int height, IReadOnlyList<ImageRegion> regions)
    {
        foreach (var region in regions)
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X + region.Width > width || region.Y + region.Height > height)
                throw new ArgumentOutOfRangeException(nameof(regions), $"区域超出图像范围: {region.Id}");
    }
}
