using System.Text.RegularExpressions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 防复发契约测试：<b>前端实际调用的 IPC 方法名</b> × <b>后端真实的方法派发表</b>。
///
/// <para>为什么用「扫源码」而不是「发请求」：派发未知方法只会得到「未知方法：x」，
/// 但要探出来就得真跑一遍处理器——<c>wiki.generate</c> 会真的重建维基库、
/// <c>dialog.openFile</c> 会弹系统对话框、<c>process.start</c> 会起进程。
/// 所以这里核对的是<b>派发表本身</b>（Application 网关 + App 宿主原生桥），
/// 谁新增了前端调用而后端没接，测试立刻红。</para>
///
/// <para>扫描口径：前端取 <c>ipc.request('x.y', …)</c> 的字面量；后端取
/// <c>"x.y" =&gt;</c> 派发标签与 <c>Method == "x.y"</c> 比较（宿主侧进程启动那几个
/// 是后一种写法）。</para>
/// </summary>
public sealed class IpcMethodContractTests
{
    /// <summary>已登记但确实还没实现的方法（每个都要写清原因，允许出现在缺口里）。</summary>
    private static readonly string[] RegisteredUnimplemented = [];

    [Fact]
    public void Every_ipc_method_the_web_ui_calls_is_dispatched_by_the_backend()
    {
        var root = RepoRoot();
        var web = Path.Combine(root, "src", "LimbusModEditor.Web", "src");
        var sources = new[]
        {
            Path.Combine(root, "src", "LimbusModEditor.Application", "Ipc", "IpcGateway.cs"),
            Path.Combine(root, "src", "LimbusModEditor.App", "NativeBridgeService.cs"),
            Path.Combine(root, "src", "LimbusModEditor.App", "WebView2MainWindow.xaml.cs"),
        };

        var called = FrontendMethods(web);
        // 扫不到源码目录比扫到空更糟（会被当成「没有缺口」），所以先钉住规模
        Assert.True(called.Count >= 20, $"只扫到 {called.Count} 个前端方法，检查仓库路径：{web}");

        var backend = string.Concat(sources.Select(File.ReadAllText));
        var gaps = called.Where(method => !IsDispatched(backend, method)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(RegisteredUnimplemented.OrderBy(x => x, StringComparer.Ordinal), gaps);
    }

    private static List<string> FrontendMethods(string webDirectory)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(webDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".vue", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file),
                         @"ipc\.request(\s*<[^>]*>)?\(\s*['""]([A-Za-z]+\.[A-Za-z]+)['""]"))
            {
                methods.Add(match.Groups[2].Value);
            }
        }
        return methods.ToList();
    }

    private static bool IsDispatched(string backendSource, string method)
    {
        var escaped = Regex.Escape(method);
        return Regex.IsMatch(backendSource, $@"""{escaped}""\s*=>")
               || Regex.IsMatch(backendSource, $@"==\s*""{escaped}""");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusModEditor.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根（LimbusModEditor.slnx）。");
    }
}
