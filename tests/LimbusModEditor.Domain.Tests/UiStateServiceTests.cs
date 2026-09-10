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

    // ── plan-09：每页列宽（WorkbenchPreviewWidths）+ 旧字段迁移兼容 ──────

    [Fact]
    public void Legacy_assets_width_migrates_into_per_page_widths()
    {
        // plan-04 时期的配置文件只有 assetsPreviewColumnWidth，没有字典字段。
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, "{\"schemaVersion\":1,\"assetsPreviewColumnWidth\":480}");
        var state = UiStateService.Load(StateFile);
        Assert.Equal(480, state.AssetsPreviewColumnWidth);
        Assert.True(state.TryGetPreviewWidth(WorkbenchPageKeys.Assets, out var width));
        Assert.Equal(480, width);
    }

    [Fact]
    public void Per_page_widths_round_trip_and_keep_legacy_field_in_sync()
    {
        var state = new UiStateService();
        state.SetPreviewWidth(WorkbenchPageKeys.Assets, 480);
        state.SetPreviewWidth(WorkbenchPageKeys.Text, 640);
        state.SetPreviewWidth(WorkbenchPageKeys.Static, 700);
        UiStateService.Save(state, StateFile);

        var reloaded = UiStateService.Load(StateFile);
        Assert.Equal(480, reloaded.GetPreviewWidth(WorkbenchPageKeys.Assets));
        Assert.Equal(640, reloaded.GetPreviewWidth(WorkbenchPageKeys.Text));
        Assert.Equal(700, reloaded.GetPreviewWidth(WorkbenchPageKeys.Static));
        // 旧字段必须跟 assets 项同值（旧版本编辑器读同一份配置也不丢宽度）。
        Assert.Equal(480, reloaded.AssetsPreviewColumnWidth);
    }

    [Fact]
    public void Unrecorded_page_width_falls_back_to_default()
    {
        var state = new UiStateService();
        Assert.False(state.TryGetPreviewWidth(WorkbenchPageKeys.Bank, out _));
        Assert.Equal(UiStateService.DefaultPreviewColumnWidth, state.GetPreviewWidth(WorkbenchPageKeys.Bank));
        // 未知 key / 空 key 也不抛，返回默认值。
        Assert.Equal(UiStateService.DefaultPreviewColumnWidth, state.GetPreviewWidth("未知页"));
        Assert.Equal(UiStateService.DefaultPreviewColumnWidth, state.GetPreviewWidth(string.Empty));
    }

    [Fact]
    public void Per_page_widths_are_clamped_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile,
            "{\"workbenchPreviewWidths\":{\"assets\":10,\"text\":99999,\"static\":500}}");
        var state = UiStateService.Load(StateFile);
        Assert.Equal(260, state.GetPreviewWidth(WorkbenchPageKeys.Assets));
        Assert.Equal(2000, state.GetPreviewWidth(WorkbenchPageKeys.Text));
        Assert.Equal(500, state.GetPreviewWidth(WorkbenchPageKeys.Static));
        // assets 项被钳制后旧字段同步为钳制值。
        Assert.Equal(260, state.AssetsPreviewColumnWidth);
    }

    [Fact]
    public void Set_preview_width_clamps_nan_infinity_and_out_of_range()
    {
        var state = new UiStateService();
        state.SetPreviewWidth("text", double.NaN);
        Assert.Equal(UiStateService.DefaultPreviewColumnWidth, state.GetPreviewWidth("text"));
        state.SetPreviewWidth("text", double.PositiveInfinity);
        Assert.Equal(UiStateService.DefaultPreviewColumnWidth, state.GetPreviewWidth("text"));
        state.SetPreviewWidth("text", 1);
        Assert.Equal(UiStateService.MinPreviewColumnWidth, state.GetPreviewWidth("text"));
        state.SetPreviewWidth("text", 99999);
        Assert.Equal(UiStateService.MaxPreviewColumnWidth, state.GetPreviewWidth("text"));
        // 空 key 直接忽略（不产生垃圾条目）：字典里只剩显式写入的 text 一项。
        state.SetPreviewWidth("   ", 500);
        Assert.Single(state.WorkbenchPreviewWidths);
        Assert.True(state.WorkbenchPreviewWidths.ContainsKey("text"));
    }

    [Fact]
    public void Page_keys_are_disjoint()
    {
        Assert.Equal(WorkbenchPageKeys.All.Length, WorkbenchPageKeys.All.Distinct().Count());
        Assert.Contains(WorkbenchPageKeys.Assets, WorkbenchPageKeys.All);
        Assert.Contains(WorkbenchPageKeys.Bank, WorkbenchPageKeys.All);
        Assert.Contains(WorkbenchPageKeys.Text, WorkbenchPageKeys.All);
        Assert.Contains(WorkbenchPageKeys.Static, WorkbenchPageKeys.All);
    }
}
