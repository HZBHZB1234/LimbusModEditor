namespace LimbusModEditor.App;

/// <summary>中文文本服务（UI 文案统一出处）。</summary>
public static class TextService
{
    /// <summary>WebView2 运行时缺失时的中文引导。</summary>
    public static string GetRuntimeMissingMessage() =>
        "未检测到 WebView2 运行时。\n\n" +
        "请安装 Evergreen WebView2 Runtime 后重试：\n" +
        "https://developer.microsoft.com/en-us/microsoft-edge/webview2/\n\n" +
        "或者使用 Fixed Version 分发模式（随包携带运行时，无需额外安装）。\n\n" +
        "离线机器请使用 Fixed Version 分发。";

    /// <summary>前端产物缺失时的中文占位提示。</summary>
    public static string GetFrontendMissingMessage() =>
        "前端产物不存在。\n\n" +
        "请先构建前端工程：\n" +
        "  npm --prefix src/LimbusModEditor.Web run build\n\n" +
        "构建产物将输出到 src/LimbusModEditor.Web/dist/，\n" +
        "发布时复制到 artifacts/publish-win-x64/wwwroot/。";
}
