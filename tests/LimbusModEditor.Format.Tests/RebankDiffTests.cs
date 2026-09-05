using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Rebank;

namespace LimbusModEditor.Format.Tests;

public class RebankDiffTests
{
    [Fact]
    public void RebankPreservesUnknownFiles()
    {
        var package = new RebankPackage();
        package.Metadata["format"] = System.Text.Json.JsonSerializer.SerializeToElement("rebank");
        package.PreservedFiles.Add(("docs/readme.txt", [7, 8]));
        using var stream = new MemoryStream();
        RebankArchive.Write(package, stream);
        var roundTrip = RebankArchive.Read(new MemoryStream(stream.ToArray()));
        var preserved = Assert.Single(roundTrip.PreservedFiles, x => x.Path == "docs/readme.txt");
        Assert.Equal(new byte[] { 7, 8 }, preserved.Data);
    }

    [Fact]
    public void CreateFromBankIncludesOnlyChangedAndAddedFsbEntries()
    {
        var original = new BankPackage();
        original.FsbData.AddRange([[1], [2]]);
        var modified = new BankPackage();
        modified.FsbData.AddRange([[1], [9], [3]]);

        var package = RebankDiffService.CreateFromBank("base.bank", "demo", "1.0", "author", "desc", original, modified);
        Assert.Equal(2, package.Files.Count);
        Assert.Contains(package.Files, x => x.Index == 1 && x.Status == "modified");
        Assert.Contains(package.Files, x => x.Index == 2 && x.Status == "added");
    }
}
