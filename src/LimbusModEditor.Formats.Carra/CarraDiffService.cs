using System.Security.Cryptography;

namespace LimbusModEditor.Formats.Carra;

public sealed record CarraChange(CarraObjectKey Key, byte[]? OriginalData, byte[] ModifiedData, bool IsAdded);

public static class CarraDiffService
{
    public static IReadOnlyList<CarraChange> Compare(CarraPackage original, CarraPackage modified)
    {
        var before = original.Entries.ToDictionary(x => x.Key.LogicalPath, StringComparer.Ordinal);
        var changes = new List<CarraChange>();
        foreach (var item in modified.Entries)
        {
            var data = item.ModifiedData ?? item.ReadData();
            if (!before.TryGetValue(item.Key.LogicalPath, out var old))
            {
                changes.Add(new(item.Key, null, data, true));
                continue;
            }
            var oldData = old.ReadData();
            if (!SHA256.HashData(oldData).AsSpan().SequenceEqual(SHA256.HashData(data)))
                changes.Add(new(item.Key, oldData, data, false));
        }
        return changes;
    }

    public static CarraPackage CreatePatch(CarraPackage original, CarraPackage modified)
    {
        var patch = new CarraPackage();
        foreach (var change in Compare(original, modified))
        {
            // A patch package can preserve the modified compressed bytes when the
            // editor has not re-encoded the object yet. Structured encoders can
            // later call ReplaceCompressedData before export.
            var source = modified.Entries.First(x => x.Key.LogicalPath == change.Key.LogicalPath);
            patch.Entries.Add(source);
        }
        patch.UnknownFiles.AddRange(modified.UnknownFiles);
        return patch;
    }
}
