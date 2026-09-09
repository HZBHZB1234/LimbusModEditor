using LimbusModEditor.Application.AppConfig;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-04：UI 状态（&lt;程序目录&gt;/config/ui-state.json）的读写、
/// 缺失/损坏回退与越界钳制。UI 状态绝不写进项目文件。</summary>
public class UiStateServiceTests : IDisposable
{
    private readonly string _root;

    public UiStateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-uistate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private string StateFile => Path.Combine(_root, "config", "ui-state.json");

    [Fact]
    public void Missing_file_returns_defaults()
    {
        var state = UiStateService.Load(StateFile);
        Assert.Equal(360, state.AssetsPreviewColumnWidth);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public void Round_trips_saved_width_and_creates_config_directory()
    {
        UiStateService.Save(new UiStateService { AssetsPreviewColumnWidth = 480 }, StateFile);
        Assert.True(File.Exists(StateFile));
        Assert.Contains("config", StateFile);
        var reloaded = UiStateService.Load(StateFile);
        Assert.Equal(480, reloaded.AssetsPreviewColumnWidth);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_backed_up()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, "{ not json");
        var state = UiStateService.Load(StateFile);
        Assert.Equal(360, state.AssetsPreviewColumnWidth);
        Assert.True(File.Exists(StateFile + ".bad"));
    }

    [Theory]
    [InlineData(10, 260)]      // 小于预览列 MinWidth → 抬到 260
    [InlineData(99999, 2000)]  // 过大 → 压到上限
    [InlineData(300, 300)]     // 合法值原样保留
    public void Out_of_range_width_is_clamped(double stored, double expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, $"{{\"assetsPreviewColumnWidth\":{stored.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        var state = UiStateService.Load(StateFile);
        Assert.Equal(expected, state.AssetsPreviewColumnWidth);
    }
}
