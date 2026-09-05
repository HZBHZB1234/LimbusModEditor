using System.IO.Compression;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Format.Tests;

public class CarraDiffTests
{
    [Fact]
    public void CompareDetectsModifiedAndAddedEntries()
    {
        static byte[] Zip(params (string Path, byte[] Data)[] items)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var item in items) using (var target = zip.CreateEntry(item.Path).Open()) target.Write(item.Data);
            return stream.ToArray();
        }

        var original = CarraArchive.Read(new MemoryStream(Zip(("a/b/1.1", [1]))));
        var modified = CarraArchive.Read(new MemoryStream(Zip(("a/b/1.1", [2]), ("a/b/2.1", [3]))));
        var changes = CarraDiffService.Compare(original, modified);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, x => x.IsAdded);
        Assert.Contains(changes, x => !x.IsAdded);
    }
}
