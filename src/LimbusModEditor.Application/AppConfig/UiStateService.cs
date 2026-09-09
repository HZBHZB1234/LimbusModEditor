using System.Text.Json;

namespace LimbusModEditor.Application.AppConfig;

/// <summary>
/// UI 状态持久化（plan-04）：保存布局占比等界面偏好到
/// <c>&lt;程序目录&gt;/config/ui-state.json</c>。与 shared-config 同目录同风格
/// （原子写 + 损坏回退默认）；UI 状态不属于项目数据，绝不写进 .lmeproj。
/// 放在 Application 层以便被现有测试工程直接覆盖（App 项目没有测试工程）。
/// </summary>
public sealed class UiStateService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>资源工作台预览列宽（px）。默认 360，受 MinWidth 260 约束。</summary>
    public double AssetsPreviewColumnWidth { get; set; } = 360;

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

    /// <summary>把读到的状态钳制回合法区间（JSON 被手改或旧版本遗留时防崩）。</summary>
    private static UiStateService Sanitize(UiStateService? state)
    {
        state ??= new UiStateService();
        if (double.IsNaN(state.AssetsPreviewColumnWidth) || double.IsInfinity(state.AssetsPreviewColumnWidth))
            state.AssetsPreviewColumnWidth = 360;
        state.AssetsPreviewColumnWidth = Math.Clamp(state.AssetsPreviewColumnWidth, 260, 2000);
        return state;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
