using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

public sealed class ProjectPersistenceTests
{
    [Fact]
    public async Task SourcesAndWindowsSettingsRoundTrip()
    {
        var root = Directory.CreateTempSubdirectory("lme-project-");
        try
        {
            var file = Path.Combine(root.FullName, "demo.lmeproj");
            var service = new ProjectService();
            var project = await service.CreateAsync(root.FullName, "demo");
            project.GameDirectory = Path.Combine(root.FullName, "game");
            project.GameExecutablePath = Path.Combine(project.GameDirectory, "LimbusCompany.exe");
            project.UnityCacheDirectory = Path.Combine(root.FullName, "unity-cache");
            project.FmodLibraryDirectory = Path.Combine(root.FullName, "fmod");
            project.Sources.Add(new ProjectSource { DisplayName = "base.carra2", Path = "sources/base.carra2", Format = ModFormatKind.Carra2 });
            var asset = new AssetRecord { LogicalPath = "acct/bundle/1.28", Type = AssetType.Texture };
            asset.Metadata["replacementPath"] = "edits/assets/texture.png";
            project.Assets.Add(asset);
            await service.SaveAsync(project, file);
            var loaded = await service.LoadAsync(file);
            Assert.Equal(project.GameDirectory, loaded.GameDirectory);
            Assert.Equal(project.GameExecutablePath, loaded.GameExecutablePath);
            Assert.Equal(project.UnityCacheDirectory, loaded.UnityCacheDirectory);
            Assert.Equal(project.FmodLibraryDirectory, loaded.FmodLibraryDirectory);
            Assert.Equal("sources/base.carra2", Assert.Single(loaded.Sources).Path);
            Assert.Equal("edits/assets/texture.png", Assert.Single(loaded.Assets).Metadata["replacementPath"]);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task LegacySchemaIsMigratedAndFutureSchemaIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("lme-project-");
        try
        {
            var service = new ProjectService();
            var legacy = Path.Combine(root.FullName, "legacy.lmeproj");
            await File.WriteAllTextAsync(legacy, "{\"name\":\"legacy\",\"schemaVersion\":1}");
            var loaded = await service.LoadAsync(legacy);
            Assert.Equal(ModProject.CurrentSchemaVersion, loaded.SchemaVersion);
            var future = Path.Combine(root.FullName, "future.lmeproj");
            await File.WriteAllTextAsync(future, "{\"name\":\"future\",\"schemaVersion\":999}");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(future));
        }
        finally { root.Delete(true); }
    }
}
