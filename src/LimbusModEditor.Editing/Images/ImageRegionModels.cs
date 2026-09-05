namespace LimbusModEditor.Editing.Images;

public sealed record ImageRegion(string Id, int X, int Y, int Width, int Height)
;

public sealed record ImageLayout(int Width, int Height, IReadOnlyList<ImageRegion> Regions);

public sealed class ImageSplitResult
{
    public ImageLayout Layout { get; init; } = new(0, 0, []);
    public Dictionary<string, byte[]> Regions { get; } = new(StringComparer.Ordinal);
}
