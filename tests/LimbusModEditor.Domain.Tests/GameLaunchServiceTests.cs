using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

public class GameLaunchServiceTests
{
    [Fact]
    public void MissingGameDirectoryReturnsDiagnostic()
    {
        var result = new GameLaunchService().TryLaunch(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.False(result.Started);
        Assert.Contains("不存在", result.Message);
    }
}
