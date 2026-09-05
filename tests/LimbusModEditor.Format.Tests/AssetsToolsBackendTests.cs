using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Format.Tests;

public class AssetsToolsBackendTests
{
    [Fact]
    public void BackendCanBeConstructedWithoutGameAssets()
    {
        using var backend = new AssetsToolsBackend();
        Assert.NotNull(backend);
    }
}
