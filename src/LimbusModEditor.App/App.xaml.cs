using System.Configuration;
using System.Data;
using System.Windows;
using LimbusModEditor.App.Logging;
using NLog;
using WpfApplication = System.Windows.Application;

namespace LimbusModEditor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private static Logger? _logger;

    /// <summary>宿主日志器（属性而非静态字段：静态字段会在类型初始化时碰 LogManager，
    /// 从而在 <see cref="LogHost.Initialize"/> 之前触发 NLog 自动加载配置）。</summary>
    private static Logger Log => _logger ??= LogManager.GetLogger("LimbusModEditor.App");

    public App()
    {
        // 日志系统在最早时机装配（早于 MainWindow 构造，也早于任何磁盘动作）：
        // NLog 文件日志 + 全局异常钩子 + UI 心跳。
        LogHost.Initialize(this);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Info("App.OnStartup（参数 {0} 个）", e.Args.Length);
        if (!OperatingSystem.IsWindows())
        {
            Log.Fatal("当前平台不是 Windows，拒绝启动。");
            MessageBox.Show("Limbus Mod Editor 仅支持 Windows。", "平台不受支持", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
        Log.Info("App.OnStartup 完成（即将创建主窗口）");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App.OnExit（退出码 {0}）", e.ApplicationExitCode);
        LogHost.Shutdown();
        base.OnExit(e);
    }
}
