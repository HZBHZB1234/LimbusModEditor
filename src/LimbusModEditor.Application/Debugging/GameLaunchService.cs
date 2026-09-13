using System.Diagnostics;
using System.ComponentModel;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Application.Debugging;

public sealed record GameLaunchResult(bool Started, string? ExecutablePath, string Message, Process? Process = null);

/// <summary>Windows-only launcher used by the debug workflow. It avoids shell
/// execution and only starts an executable found inside the configured game
/// directory.</summary>
public sealed class GameLaunchService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public GameLaunchResult TryLaunch(string gameDirectory, string? executablePath = null, string? arguments = null)
    {
        using var scope = Log.Scope("启动游戏进程");
        Log.Info("启动游戏请求：游戏目录 {0}，指定 exe {1}，参数 {2}",
            gameDirectory, executablePath ?? "-", arguments ?? "-");
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn("启动游戏被跳过：当前系统不是 Windows（{0}），本功能仅支持 Windows", Environment.OSVersion.Platform);
            return new(false, null, "游戏启动仅支持 Windows。");
        }
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            Log.Warn("启动游戏被跳过：游戏目录无效或不存在（{0}）", gameDirectory);
            return new(false, null, "游戏目录不存在。");
        }
        var path = string.IsNullOrWhiteSpace(executablePath)
            ? Directory.EnumerateFiles(gameDirectory, "LimbusCompany.exe", SearchOption.AllDirectories).FirstOrDefault()
            : Path.GetFullPath(executablePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warn("启动游戏回退失败：{0} 下（含子目录）没有找到 {1}，且调用方未指定有效 exe（{2}）",
                gameDirectory, "LimbusCompany.exe", executablePath ?? "-");
            return new(false, path, "未找到 LimbusCompany.exe，请确认游戏目录配置正确。");
        }
        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("启动游戏被拒绝：exe 位于配置的游戏目录之外（exe {0}，要求位于 {1} 内）", path, root);
            return new(false, path, "启动程序必须位于配置的游戏目录内。");
        }
        var workingDirectory = Path.GetDirectoryName(path)!;
        Log.Debug("启动游戏进程：exe {0}，参数 {1}，工作目录 {2}", path, arguments ?? string.Empty, workingDirectory);
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false
            });
            if (process is null)
            {
                Log.Warn("启动游戏失败：Process.Start 返回 null（exe {0}）", path);
                return new(false, path, "系统未能创建游戏进程。");
            }
            Log.Info("游戏进程已启动：pid {0}，exe {1}，工作目录 {2}", process.Id, path, workingDirectory);
            return new(true, path, "游戏已启动。", process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Error(ex, "启动游戏失败：exe {0}，参数 {1}，工作目录 {2}", path, arguments ?? string.Empty, workingDirectory);
            return new(false, path, $"启动游戏失败：{ex.Message}");
        }
    }
}
