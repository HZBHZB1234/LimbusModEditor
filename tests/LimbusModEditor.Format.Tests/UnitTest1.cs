using System.IO.Compression;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Bank;
using System.Buffers.Binary;

namespace LimbusModEditor.Format.Tests;

public class FormatTests
{
    [Fact]
    public void CarraProbeRecognizesObjectPath()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var entry = archive.CreateEntry("account/bundle/123.28").Open();
            entry.Write([1, 2, 3]);
        }
        stream.Position = 0;
        var result = CarraArchive.Probe(stream);
        Assert.True(result.IsMatch);
        Assert.Equal(123, CarraArchive.Read(new MemoryStream(stream.ToArray())).Entries.Single().Key.PathId);
    }

    [Fact]
    public async Task CarraHandlerKeepsPayloadAndCanExportUnmodifiedPackage()
    {
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, true))
        {
            using var entry = archive.CreateEntry("account/bundle/7.28").Open();
            entry.Write([8, 9]);
        }
        source.Position = 0;
        var handler = new CarraFormatHandler();
        var package = await handler.ImportAsync(source, new());
        Assert.NotNull(package.Payload);
        using var output = new MemoryStream();
        await handler.ExportAsync(package, output, new(ModFormatKind.Carra2));
        var roundTrip = CarraArchive.Read(new MemoryStream(output.ToArray()));
        Assert.Equal([8, 9], roundTrip.Entries.Single().ReadData());
    }

    [Fact]
    public void LunartiqueProbeRequiresBothFolders()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("Root/Installation/a/__data");
            archive.CreateEntry("Root/Uninstallation/a/__data");
        }
        stream.Position = 0;
        Assert.True(LunartiqueArchive.IsMatch(stream));
        var package = LunartiqueArchive.Read(new MemoryStream(stream.ToArray()));
        Assert.Single(package.Resources);
        Assert.Empty(LunartiqueArchive.ChangedResources(package));
    }

    [Fact]
    public void LunartiqueWriteRoundTripsInstallationAndUninstallation()
    {
        var package = new LunartiquePackage { Root = "Root" };
        package.Resources.Add(new LunartiqueResource("assets/a.bundle", [1, 2], [3, 4]));
        package.PreservedFiles.Add(("Root/readme.txt", [9]));
        using var stream = new MemoryStream();
        LunartiqueArchive.Write(package, stream);
        var roundTrip = LunartiqueArchive.Read(new MemoryStream(stream.ToArray()));
        var resource = Assert.Single(roundTrip.Resources);
        Assert.Equal([1, 2], resource.Uninstallation);
        Assert.Equal([3, 4], resource.Installation);
        Assert.Contains(roundTrip.PreservedFiles, x => x.Path == "Root/readme.txt");
        Assert.Single(LunartiqueArchive.ChangedResources(roundTrip));
    }
}
