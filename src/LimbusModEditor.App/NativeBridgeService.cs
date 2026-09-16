using System.IO;
using System.Text.Json;
using System.Windows;
using LimbusModEditor.Application.Ipc;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.App;

/// <summary>
/// 原生桥服务：处理对话框/剪贴板/Process.Start 回调（WEB-IPC-CONTRACT §6）。
/// 宿主侧执行（WPF 依赖），页面侧不依赖 &lt;input type=file&gt;。
/// </summary>
public static class NativeBridgeService
{
    /// <summary>处理对话框请求，返回响应 JSON。</summary>
    public static IpcResponse HandleDialog(IpcRequest request, Window? owner)
    {
        try
        {
            return request.Method switch
            {
                "dialog.openFile" => HandleOpenFile(request, owner),
                "dialog.saveFile" => HandleSaveFile(request, owner),
                "dialog.folderPick" => HandleFolderPick(request, owner),
                "dialog.messageBox" => HandleMessageBox(request, owner),
                _ => IpcResponse.Failure(request.Id, IpcErrorCode.Internal, $"未知对话框方法：{request.Method}")
            };
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.Internal, $"对话框错误：{ex.Message}");
        }
    }

    /// <summary>处理剪贴板请求。</summary>
    public static IpcResponse HandleClipboard(IpcRequest request)
    {
        try
        {
            return request.Method switch
            {
                "clipboard.readText" => HandleClipboardRead(request),
                "clipboard.writeText" => HandleClipboardWrite(request),
                _ => IpcResponse.Failure(request.Id, IpcErrorCode.Internal, $"未知剪贴板方法：{request.Method}")
            };
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.Internal, $"剪贴板错误：{ex.Message}");
        }
    }

    /// <summary>处理 Process.Start 请求（白名单）。</summary>
    public static IpcResponse HandleProcessStart(IpcRequest request)
    {
        try
        {
            var req = DeserializeReq<ProcessStartRequest>(request);
            return req?.AppId switch
            {
                "explorer" => HandleOpenExplorer(req?.Args),
                "game" => IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, "游戏启动器暂未实现（W2）"),
                _ => IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, $"未知进程 appId：{req?.AppId}")
            };
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.Internal, $"进程启动错误：{ex.Message}");
        }
    }

    private static T? DeserializeReq<T>(IpcRequest request) where T : class
    {
        return request.Payload.Deserialize<T>(Options);
    }

    // ── 对话框实现 ──

    private static IpcResponse HandleOpenFile(IpcRequest request, Window? owner)
    {
        var req = request.Payload.Deserialize<DialogOpenFileRequest>(Options)
            ?? throw new ArgumentException("dialog.openFile 载荷格式错误。");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = req.Title ?? "打开文件",
            Filter = req.Filters ?? "所有文件 (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(req.StartPath) && Directory.Exists(req.StartPath))
            dialog.InitialDirectory = req.StartPath;

        bool? result = owner is not null
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        return IpcResponse.Success(request.Id, new DialogResponse(
            result == true, result == true ? dialog.FileName : null, null));
    }

    private static IpcResponse HandleSaveFile(IpcRequest request, Window? owner)
    {
        var req = request.Payload.Deserialize<DialogSaveFileRequest>(Options)
            ?? throw new ArgumentException("dialog.saveFile 载荷格式错误。");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = req.Title ?? "保存文件",
            Filter = req.Filters ?? "所有文件 (*.*)|*.*",
            FileName = req.DefaultName,
        };
        if (!string.IsNullOrWhiteSpace(req.StartPath) && Directory.Exists(req.StartPath))
            dialog.InitialDirectory = req.StartPath;

        bool? result = owner is not null
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        return IpcResponse.Success(request.Id, new DialogResponse(
            result == true, result == true ? dialog.FileName : null, null));
    }

    private static IpcResponse HandleFolderPick(IpcRequest request, Window? owner)
    {
        var req = request.Payload.Deserialize<DialogFolderPickRequest>(Options)
            ?? throw new ArgumentException("dialog.folderPick 载荷格式错误。");
        // W1 定案：使用 OpenFileDialog「选择此文件夹」惯例（与既有 ExportMod_Click 一致）
        // 可选更优体验：Ookii.Dialogs.Wpf VistaFolderBrowserDialog 或 Win32 IFileDialog(FOS_PICKFOLDERS)
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = req.Title ?? "选择文件夹",
            ValidateNames = false,
            CheckFileExists = false,
            FileName = "选择此文件夹",
        };
        if (!string.IsNullOrWhiteSpace(req.StartPath) && Directory.Exists(req.StartPath))
            dialog.InitialDirectory = req.StartPath;

        bool? result = owner is not null
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        var path = result == true ? Path.GetDirectoryName(dialog.FileName) : null;
        return IpcResponse.Success(request.Id, new DialogResponse(
            result == true, path, null));
    }

    private static IpcResponse HandleMessageBox(IpcRequest request, Window? owner)
    {
        var req = request.Payload.Deserialize<DialogMessageBoxRequest>(Options)
            ?? throw new ArgumentException("dialog.messageBox 载荷格式错误。");
        var result = owner is not null
            ? MessageBox.Show(owner, req.Text, req.Title, ParseButtons(req.Buttons), ParseIcon(req.Icon))
            : MessageBox.Show(req.Text, req.Title, ParseButtons(req.Buttons), ParseIcon(req.Icon));

        return IpcResponse.Success(request.Id, new DialogResponse(true, null, result.ToString()));
    }

    // ── 剪贴板实现 ──

    private static IpcResponse HandleClipboardRead(IpcRequest request)
    {
        string text = "";
        if (System.Windows.Clipboard.ContainsText())
            text = System.Windows.Clipboard.GetText();
        return IpcResponse.Success(request.Id, new ClipboardReadResponse(text));
    }

    private static IpcResponse HandleClipboardWrite(IpcRequest request)
    {
        var req = request.Payload.Deserialize<ClipboardWriteRequest>(Options)
            ?? throw new ArgumentException("clipboard.writeText 载荷格式错误。");
        System.Windows.Clipboard.SetText(req.Text ?? "");
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    // ── Process.Start 实现 ──

    private static IpcResponse HandleOpenExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return IpcResponse.Failure("process", IpcErrorCode.NotFound, $"目录不存在：{path}");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
        return IpcResponse.Success("process", new ProcessStartResponse(true, null, $"已打开 {path}"));
    }

    // ── 辅助 ──

    private static MessageBoxButton ParseButtons(string? buttons) => buttons?.ToLower() switch
    {
        "okcancel" => MessageBoxButton.OKCancel,
        "yesno" => MessageBoxButton.YesNo,
        "yesnocancel" => MessageBoxButton.YesNoCancel,
        _ => MessageBoxButton.OK,
    };

    private static MessageBoxImage ParseIcon(string? icon) => icon?.ToLower() switch
    {
        "warning" => MessageBoxImage.Warning,
        "error" => MessageBoxImage.Error,
        "information" => MessageBoxImage.Information,
        "question" => MessageBoxImage.Question,
        _ => MessageBoxImage.None,
    };
}
