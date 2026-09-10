using System.Text.Json;

namespace LimbusModEditor.Application.AppConfig;

/// <summary>工作台页面的稳定 key（plan-09）：列宽持久化与页面骨架共用同一套字符串。</summary>
public static class WorkbenchPageKeys
{
    /// <summary>资源工作台（AssetsWorkbenchPage）；旧配置文件的 AssetsPreviewColumnWidth 映射到它。</summary>
    public const string Assets = "assets";
    /// <summary>音频工作台。</summary>
    public const string Bank = "bank";
    /// <summary>文本工作台。</summary>
    public const string Text = "text";
    /// <summary>静态数据工作台。</summary>
    public const string Static = "static";

    /// <summary>四个工作台的 key（顺序与侧边栏一致）。</summary>
    public static readonly string[] All = [Assets, Bank, Text, Static];
}

/// <summary>
/// UI 状态持久化（plan-04；plan-09 扩展为「每页列宽」）：保存布局占比等界面偏好到
/// <c>&lt;程序目录&gt;/config/ui-state.json</c>。与 shared-config 同目录同风格
/// （原子写 + 损坏回退默认）；UI 状态不属于项目数据，绝不写进 .lmeproj。
/// 放在 Application 层以便被现有测试工程直接覆盖（App 项目没有测试工程）。
/// </summary>
public sealed class UiStateService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>预览/编辑列的默认宽度（px）。</summary>
    public const double DefaultPreviewColumnWidth = 360;
    /// <summary>预览/编辑列的最小宽度（px），与四个工作台 ColumnDefinition.MinWidth 一致。</summary>
    public const double MinPreviewColumnWidth = 260;
    /// <summary>预览/编辑列的最大宽度（px）：防止手改配置文件把浏览列挤没。</summary>
    public const double MaxPreviewColumnWidth = 2000;

    /// <summary>资源工作台预览列宽（px）。旧字段（plan-04）：仍然读写并始终与
    /// <see cref="WorkbenchPreviewWidths"/> 里 <c>assets</c> 那一项保持同值，
    /// 这样旧版本编辑器读同一份配置也不会丢宽度。</summary>
    public double AssetsPreviewColumnWidth { get; set; } = DefaultPreviewColumnWidth;

    /// <summary>各工作台的预览/编辑列宽（px），key = <see cref="WorkbenchPageKeys"/>。
    /// plan-09 引入：四个工作台各自记住自己的列宽，列宽变化互不影响。</summary>
    public Dictionary<string, double> WorkbenchPreviewWidths { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某页的预览列宽：缺省 <see cref="DefaultPreviewColumnWidth"/>，并钳制到
    /// [<see cref="MinPreviewColumnWidth"/>, <see cref="MaxPreviewColumnWidth"/>]。</summary>
    public double GetPreviewWidth(string pageKey)
        => TryGetPreviewWidth(pageKey, out var width) ? width : DefaultPreviewColumnWidth;

    /// <summary>某页是否有显式持久化的列宽（用于「未记录过 → 用默认值」判断）。</summary>
    public bool TryGetPreviewWidth(string pageKey, out double width)
    {
        width = DefaultPreviewColumnWidth;
        if (string.IsNullOrWhiteSpace(pageKey) || WorkbenchPreviewWidths is null) return false;
        if (!WorkbenchPreviewWidths.TryGetValue(pageKey.Trim(), out var stored)) return false;
        width = ClampPreviewWidth(stored);
        return true;
    }

    /// <summary>写某页的列宽（钳制后写入）；<c>assets</c> 同时同步旧字段。</summary>
    public void SetPreviewWidth(string pageKey, double width)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        WorkbenchPreviewWidths ??= new(StringComparer.OrdinalIgnoreCase);
        WorkbenchPreviewWidths[pageKey.Trim()] = ClampPreviewWidth(width);
        SyncLegacyAssetsWidth();
    }

    /// <summary>列宽钳制：NaN/Infinity 回默认值，其余夹到 [260, 2000]。</summary>
    public static double ClampPreviewWidth(double width)
        => double.IsNaN(width) || double.IsInfinity(width)
            ? DefaultPreviewColumnWidth
            : Math.Clamp(width, MinPreviewColumnWidth, MaxPreviewColumnWidth);

    /// <summary>加载 UI 状态；文件缺失/损坏时回退默认值（不阻塞启动）。</summary>
    public static UiStateService Load(string configFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        try
        {
            if (!File.Exists(configFile)) return new UiStateService();
            var state = JsonSerializer.Deserialize<UiStateService>(File.ReadAllText(configFile), Options);
            return Sanitize(state);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 损坏文件备份后回退默认，与 shared-config 同策略。
            try { if (File.Exists(configFile)) File.Move(configFile, configFile + ".bad", true); }
            catch (Exception) { /* best-effort */ }
            return new UiStateService();
        }
    }

    /// <summary>原子保存 UI 状态；写失败静默（UI 状态丢失可接受，不应打断用户）。</summary>
    public static void Save(UiStateService state, string configFile)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        try
        {
            var path = Path.GetFullPath(configFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 状态写不进去不影响功能；保留旧文件即可。
        }
    }

    /// <summary>把读到的状态规整为合法状态（JSON 被手改或旧版本遗留时防崩）：
    /// ① 字典比较器统一为忽略大小写（JsonSerializer 会替换集合实例，比较器会丢）；
    /// ② 旧字段 <see cref="AssetsPreviewColumnWidth"/> ←→ 字典 <c>assets</c> 双向兼容；
    /// ③ 所有列宽钳制到 [260, 2000]（NaN/Infinity 回默认）。</summary>
    private static UiStateService Sanitize(UiStateService? state)
    {
        state ??= new UiStateService();

        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state.WorkbenchPreviewWidths ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            widths[key.Trim()] = ClampPreviewWidth(value);
        }

        // 旧配置兼容：plan-04 的配置只有 assetsPreviewColumnWidth，字典里没有 assets。
        // 字典里已有 assets（新版本写的）时以字典为准，旧字段只是被同步成同一个值。
        if (!widths.TryGetValue(WorkbenchPageKeys.Assets, out var assetsWidth))
            assetsWidth = ClampPreviewWidth(state.AssetsPreviewColumnWidth);
        widths[WorkbenchPageKeys.Assets] = assetsWidth;

        state.WorkbenchPreviewWidths = widths;
        state.AssetsPreviewColumnWidth = assetsWidth;
        return state;
    }

    /// <summary>把 <c>assets</c> 的列宽同步到旧字段（保存时两个字段一致）。</summary>
    private void SyncLegacyAssetsWidth()
    {
        if (WorkbenchPreviewWidths is not null &&
            WorkbenchPreviewWidths.TryGetValue(WorkbenchPageKeys.Assets, out var assetsWidth))
            AssetsPreviewColumnWidth = assetsWidth;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
