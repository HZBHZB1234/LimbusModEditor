using LimbusModEditor.Formats.Lunartique;
using System.IO.Compression;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Application.Build;

namespace LimbusModEditor.Domain.Tests;

public sealed class LunartiqueConversionTests
{
    [Fact]
    public async Task InvalidSerializedFileProducesDiagnosticInsteadOfSilentCopy()
    {
        var package = new LunartiquePackage { Root = "Root" };
        package.Resources.Add(new LunartiqueResource("account/bundle", [1, 2, 3], [4, 5, 6]));
        var result = await new LunartiqueCarraConversionService().ConvertAsync(package);
        Assert.Empty(result.Package.Entries);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.True(diagnostic.IsError);
        Assert.Contains("SerializedFile", diagnostic.Message);
    }

    [Fact]
    public async Task UnsupportedResourcePathIsReportedAndSkipped()
    {
        var package = new LunartiquePackage { Root = "Root" };
        package.Resources.Add(new LunartiqueResource("something.bundle", [1], [2]));
        var result = await new LunartiqueCarraConversionService().ConvertAsync(package);
        Assert.Empty(result.Package.Entries);
        Assert.Contains(result.Diagnostics, x => !x.IsError && x.RelativePath == "something.bundle");
    }

    [Fact]
    public async Task ExportRejectsLunartiqueWithNoConvertibleObjects()
    {
        var root = Directory.CreateTempSubdirectory("lme-convert-");
        try
        {
            var source = Path.Combine(root.FullName, "source.zip");
            var output = Path.Combine(root.FullName, "out.carra2");
            await using (var file = File.Create(source))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var before = zip.CreateEntry("Root/Uninstallation/account/bundle/__data").Open()) before.Write([1, 2]);
                using (var after = zip.CreateEntry("Root/Installation/account/bundle/__data").Open()) after.Write([3, 4]);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => new ModExportService(BuiltInFormatRegistry.Create()).ExportWithEditsAsync(source, new ModProject(), output, ModFormatKind.Carra2));
        }
        finally { root.Delete(true); }
    }
}
