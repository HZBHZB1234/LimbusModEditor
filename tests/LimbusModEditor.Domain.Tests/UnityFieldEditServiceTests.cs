using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public sealed class UnityFieldEditServiceTests
{
    [Fact]
    public void StoresAndReadsPrimitiveFieldEdits()
    {
        var project = new ModProject();
        var asset = new AssetRecord
        {
            LogicalPath = "main.assets/10.114",
            UnityPathId = 10,
            UnityTypeId = 114,
            Metadata = { ["unitySerializedFile"] = "true" }
        };
        project.Assets.Add(asset);
        var service = new UnityFieldEditService();
        service.Set(project, asset, new Dictionary<string, string> { ["Base.m_Enabled"] = "false", ["Base.m_Speed"] = "1.25" });
        var stored = service.ReadStored(asset);
        Assert.NotNull(stored);
        Assert.Equal("false", stored!["Base.m_Enabled"]);
        Assert.Equal("1.25", stored["Base.m_Speed"]);
        Assert.Single(project.Edits);
        Assert.Equal(AssetEditState.Modified, asset.EditState);
    }
}
