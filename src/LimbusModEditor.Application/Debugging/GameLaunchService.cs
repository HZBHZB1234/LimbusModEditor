using System.Diagnostics;
using System.ComponentModel;

namespace LimbusModEditor.Application.Debugging;

public sealed record GameLaunchResult(bool Started, string? ExecutablePath, string Message, Process? Process = null);

/// <summary>Windows-only launcher used by the debug workflow. It avoids shell
/// execution and only starts an executable found inside the configured game
/// directory.</summary>
public sealed class GameLaunchService
{
    public GameLaunchResult TryLaunch(string gameDirectory, string? executablePath = null, string? arguments = null)
    {
        if (!OperatingSystem.IsWindows()) return new(false, null, "游戏启动仅支持 Windows。");
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return new(false, null, "游戏目录不存在。");
        var path = string.IsNullOrWhiteSpace(executablePath)
            ? Directory.EnumerateFiles(gameDirectory, "LimbusCompany.exe", SearchOption.AllDirectories).FirstOrDefault()
            : Path.GetFullPath(executablePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(false, path, "未找到 LimbusCompany.exe，请确认游戏目录配置正确。");
        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return new(false, path, "启动程序必须位于配置的游戏目录内。");
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false
            });
            return process is null
                ? new(false, path, "系统未能创建游戏进程。")
                : new(true, path, "游戏已启动。", process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new(false, path, $"启动游戏失败：{ex.Message}");
        }
    }
}
