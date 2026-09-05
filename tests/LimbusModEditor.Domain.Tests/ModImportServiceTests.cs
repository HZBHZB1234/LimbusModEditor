using System.IO.Compression;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public class ModImportServiceTests
{
    [Fact]
    public async Task ImportsCarraIntoProjectAndMergesDuplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lme-{Guid.NewGuid():N}.carra");
        try
        {
            await using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var entry = archive.CreateEntry("acct/bundle/1.28").Open();
                entry.Write([1, 2, 3]);
            }
            var project = new ModProject();
            var result = await new ModImportService(BuiltInFormatRegistry.Create()).ImportIntoProjectAsync(path, project);
            Assert.Equal(1, result.AddedAssets);
            Assert.Single(project.Assets);
            Assert.Equal("acct/bundle/1.28", project.Assets[0].LogicalPath);
        }
        finally { File.Delete(path); }
    }
}
