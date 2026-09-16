using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using LimbusModEditor.Application.Ipc;
using NLog;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.App;

/// <summary>Win32 IFileDialog 真文件夹选择器（无新依赖，FOS_PICKFOLDERS）。</summary>
internal static class NativeFolderPicker
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid IID_IFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cTypes, [In] IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        uint GetFileTypeIndex();
        void Hook(IntPtr ptr);
        void GetHookResult(out int phresult);
        void SetOptions(int fos);
        void GetOptions(out int fos);
        void SetDefaultFolder(IntPtr psi);
        void SetFolder(IntPtr psi);
        void GetFolder(out IntPtr ppsi);
        void GetCurrentSelection(out IntPtr ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IntPtr ppsi);
        void AddPlace(IntPtr psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
    }

    private const int FOS_PICKFOLDERS = 0x20;
    private const int FOS_FORCEFILESYSTEM = 0x40;
    private const int S_OK = 0;

    /// <summary>显示真文件夹选择器。返回选中路径，取消返回 null。</summary>
    public static string? PickFolder(string? title, string? initialDirectory, IntPtr ownerHandle)
    {
        try
        {
            var dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog) ?? throw new InvalidOperationException("无法创建 FileOpenDialog");
            var dialog = (IFileDialog)Activator.CreateInstance(dialogType)!;
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);

            if (!string.IsNullOrWhiteSpace(title))
                dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                // 创建 shell item 从路径
                var hr = SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, out IntPtr psi);
                if (hr >= 0 && psi != IntPtr.Zero)
                {
                    dialog.SetFolder(psi);
                    Marshal.Release(psi);
                }
            }

            var result = dialog.Show(ownerHandle);
            if (result != S_OK)
                return null; // 用户取消

            dialog.GetResult(out IntPtr psiResult);
            if (psiResult == IntPtr.Zero)
                return null;

            try
            {
                return GetPathFromShellItem(psiResult);
            }
            finally
            {
                Marshal.Release(psiResult);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "NativeFolderPicker: Win32 IFileDialog 失败，回退 OpenFileDialog 惯例");
            return FallbackPickFolder(title, initialDirectory, ownerHandle);
        }
    }

    private static string? FallbackPickFolder(string? title, string? initialDirectory, IntPtr ownerHandle)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title ?? "选择文件夹",
            ValidateNames = false,
            CheckFileExists = false,
            FileName = "选择此文件夹",
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var wpfOwner = HwndSourceHelper.FromHwnd(ownerHandle)?.RootVisual as Window;
        var result = wpfOwner is not null ? dialog.ShowDialog(wpfOwner) : dialog.ShowDialog();
        return result == true ? Path.GetDirectoryName(dialog.FileName) : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, out IntPtr ppsi);

    private static string GetPathFromShellItem(IntPtr psi)
    {
        var hr = SHGetNameFromPath(psi, out string? path);
        return path ?? string.Empty;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetNameFromPath(IntPtr psi, [MarshalAs(UnmanagedType.LPWStr)] out string? pszPath);
}

internal static class HwndSourceHelper
{
    public static System.Windows.Interop.HwndSource? FromHwnd(IntPtr hwnd) =>
        System.Windows.Interop.HwndSource.FromHwnd(hwnd);
}

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

        var ownerHandle = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
        var path = NativeFolderPicker.PickFolder(req.Title, req.StartPath, ownerHandle);

        return IpcResponse.Success(request.Id, new DialogResponse(path is not null, path, null));
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
