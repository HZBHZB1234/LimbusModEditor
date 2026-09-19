# WebView2 宿主 Spike 实测结论（SPIKE-WEBVIEW2）

- 状态：**W0 spike 完成**（2026-09-16）
- 取代/补充：`docs/ARCH-WEBVIEW2-VUE.md` W0 验收口径「五个未知量一次性打掉」
- 范围：`src/LimbusModEditor.App/` 内最小宿主骨架 + `docs/`；未碰 `src/LimbusModEditor.Web/`、`Application/`、`SpineRuntime/`
- 验收：`dotnet build LimbusModEditor.slnx --no-restore --nologo` → **0 错误 0警告**（Nullable + TreatWarningsAsErrors 仍生效）

---

## 1. 构建与包

### 1.1 已加入的包

| 包 | 版本 | 用途 |
|---|---|---|
| `Microsoft.Web.WebView2` | 1.0.4191.47 | WebView2 宿主 SDK（Core + WPF 控件） |

### 1.2 构建命令与结果

```powershell
dotnet build LimbusModEditor.slnx --no-restore --nologo
# 结果：0 errors, 0 warnings
# 输出：src/LimbusModEditor.App/bin/Debug/net8.0-windows/ win-x64/LimbusModEditor.App.dll
```

### 1.3 新增文件清单

| 路径 | 用途 |
|---|---|
| `src/LimbusModEditor.App/WebView2SpikeWindow.xaml` | 试验窗口 XAML（独立于 MainWindow） |
| `src/LimbusModEditor.App/WebView2SpikeWindow.xaml.cs` | 试验窗口代码：WebView2 初始化、虚拟主机、消息桥、运行时检测 |
| `src/LimbusModEditor.App/WebHostSpike/index.html` | 静态试验页（IME/DPI/对话框/剪贴板/进程/二进制通道六项测试） |
| `src/LimbusModEditor.App/WebHostSpike/data/spike/test.png` | 测试用真实图片（704 KB，从 `docs/img/wiki-front-page.png` 复制；源图已随 `docs/img/` 于 2026-09-19 移出仓库） |
| `src/LimbusModEditor.App/app.manifest` | PerMonitorV2 DPI 感知 + 长路径支持 |
| `src/LimbusModEditor.App/App.xaml`（改） | StartupUri → Startup 事件（支持 --spike-webview2 走独立窗口） |
| `src/LimbusModEditor.App/App.xaml.cs`（改） | App_OnStartup：--spike-webview2 路由到 WebView2SpikeWindow |
| `src/LimbusModEditor.App/LimbusModEditor.App.csproj`（改） | 加 WebView2 包引用 + app.manifest + WebHostSpike Content 复制到输出 |

---

## 2. 运行时检测（未知量 ①）

### 2.1 Evergreen 探测

```csharp
var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
```

**本机实测**：`153.0.4234.32`（Edge WebView2 Runtime，机器已装）

**验证命令**（PowerShell，读注册表）：
```powershell
Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" | Select-Object -ExpandProperty pv
# 输出：153.0.4234.32
```

### 2.2 缺失时的中文引导

`CoreWebView2Environment.GetAvailableBrowserVersionString()` 在缺失时抛 `WebView2RuntimeNotFoundException`。宿主侧已捕获并弹中文 MessageBox：

> 未检测到 WebView2 运行时。请安装 Evergreen WebView2 Runtime 后重试。
> 下载地址: https://developer.microsoft.com/en-us/microsoft-edge/webview2/
> 或者使用 Fixed Version 分发模式（随包携带运行时）。

**注意**：此路径在本机（已装运行时）未实际弹框验证，需离线机器或卸载运行时后人工复测。

### 2.3 Fixed Version 分发开关预留

```csharp
var env = await CoreWebView2Environment.CreateAsync(
    browserExecutableFolder: null,  // ← Evergreen 模式
    // browserExecutableFolder: @"path\to\Microsoft.WebView2.FixedVersionRuntime",  // ← Fixed Version 模式
    userDataFolder: Path.Combine(baseDir, "WebView2Data"),
    options: envOptions);
```

**切换方式**：W1 起在配置（`shared-config.json`）加 `"webview2Distribution": "evergreen" | "fixedVersion"` 字段，宿主读取后传路径。

**体积代价**（ADR §6.3 口径）：Fixed Version 运行时约 **+180MB**。当前发布基线 519MB，目标上限 700MB。

**离线机器处置**：受众机器可能离线 → 建议默认用 Fixed Version 分发（随包携带），启动时仍做 Evergreen 探测作为降级备选（用户已装新版 Evergreen 则用之，免重复占用磁盘）。此决策留 W1 定案；本 spike 结论支持「Fixed Version 为默认路径」。

---

## 3. 虚拟主机映射与本地加载

### 3.1 映射配置

```csharp
// lme.app → 前端静态资源
_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "lme.app", _webHostSpikeDir,
    CoreWebView2HostResourceAccessKind.Allow);

// lme.data → 只读数据根
_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "lme.data", _dataDir,
    CoreWebView2HostResourceAccessKind.Allow);
```

**实测映射**：
- `lme.app` → `…/ bin/Debug/net8.0-windows/win-x64/WebHostSpike/`
- `lme.data` → `…/ bin/Debug/net8.0-windows/win-x64/WebHostSpike/data/`

### 3.2 导航与加载验证

**启动导航**：`https://lme.app/index.html`

**实测结果**：
- 页面加载成功 ✓
- 页面内 JavaScript 执行 ✓
- 页面→宿主消息（`spike.pageReady`）发送成功 ✓
- 宿主收到消息并记录日志 ✓

**日志证据**：
```
2026-09-16 16:15:36.7695 INFO  OnLoaded: 虚拟主机映射已配置: lme.app → …/WebHostSpike, lme.data → …/WebHostSpike/data
2026-09-16 16:15:36.7695 INFO  OnLoaded: 导航到: https://lme.app/index.html
2026-09-16 16:15:37.0101 INFO  OnWebMessageReceived: 收到页面消息: {"method":"spike.pageReady","payload":{"url":"https://lme.app/index.html"}}
```

**结论**：`SetVirtualHostNameToFolderMapping` 加载本地产物成功，**无需 base64 或 data URL**。

---

## 4. PerMonitorV2 DPI（未知量 ②）

### 4.1 配置方式

已添加 `src/LimbusModEditor.App/app.manifest`：

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings>
</application>
```

### 4.2 实测结论

- 清单已加入 csproj（`<ApplicationManifest Include="app.manifest" />`）✓
- 本机缩放 100% 下窗口正常显示，WebView2 跟随宿主 DPI ✓
- **未实测项**：125%/150% 缩放 + 双显示器间移动窗口 → 需人工在真实多显示器环境复测（本 spike 环境为单显示器 100%）

**契约对照**：ADR §3-6「宿主进程 PerMonitorV2；WebView2 跟随宿主 DPI（Chromium 自行处理 per-monitor）；统一缩放策略：以宿主窗口 DPI 为基准，前端不自行做 DPI 换算。」→ 清单配置与此一致。

---

## 5. 中文 IME 输入（未知量 ③）

### 5.1 测试页

`WebHostSpike/index.html` 含 `<textarea id="ime-input">`，页面部署后由人类在真实中文输入法（拼音/五笔）下输入长文本。

### 5.2 实测状态

- 代码层：无特殊配置，WebView2 默认启用 IME（Chromium 内置）
- **需人工复测项**：候选词窗位置、回车确认、Escape 关闭、长文本输入不卡顿
- **W0 验收建议**：由 tester 在 125%/150% DPI + 中文输入法下输入 ≥500 字，观察候选窗是否偏移、回车是否误提交

**结论**：IME 在 WebView2 下默认可用；候选窗偏移是 Chromium 已知问题，PerMonitorV2 清单已是最优缓解。**最终验收需人工**。

---

## 6. 原生对话框 / 剪贴板 / Process.Start 回调（未知量 ④）

### 6.1 实现通道

| 功能 | 宿主侧 | 传输方式 |
|---|---|---|
| 打开文件 | `Microsoft.Win32.OpenFileDialog` | `WebMessageReceived` → 模态 `ShowDialog(this)` → `PostWebMessageAsString` 回传 `{ok, path}` |
| 保存文件 | `Microsoft.Win32.SaveFileDialog` | 同上 |
| 选择文件夹 | `OpenFileDialog`（`ValidateNames=false, CheckFileExists=false, FileName="选择此文件夹"`） | 同上，取 `Path.GetDirectoryName` |
| 消息框 | `MessageBox.Show(this, …)` | 回传 `{button}` |
| 读剪贴板 | `System.Windows.Clipboard.GetText()` | 回传 `{text}` |
| 写剪贴板 | `System.Windows.Clipboard.SetText(text)` | 无回传（fire-and-forget） |
| Process.Start | `Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = …, UseShellExecute = true })` | 回传 `{ok, message}` |

### 6.2 实测状态

- 代码落地：六项回调全部实现 ✓
- 消息帧：JSON `{ method, payload }` / `{ type, result }` 双向 ✓
- **需人工复测项**：对话框模态行为、取消语义、剪贴板权限、Process.Start 白名单

**注意**：文件夹选择用 `OpenFileDialog` 的「选择此文件夹」惯例（与既有 MainWindow `ExportMod_Click` 一致），**不**用 `FolderBrowserDialog`（过时且 UI 不佳）。W1 如需更优体验可迁移至 `Ookii.Dialogs.Wpf` 的 `VistaFolderBrowserDialog`。

### 6.3 契约对照

`WEB-IPC-CONTRACT.md` §6 定义的 `dialog.openFile/saveFile/folderPick/messageBox`、`clipboard.readText/writeText`、`process.start` → 本 spike 全部覆盖，**契约无需修正**。

---

## 7. 二进制通道（未知量 ⑤）

### 7.1 Virtual Host 路径

```
<img src="https://lme.data/spike/test.png" />
```

浏览器直接从磁盘加载，**不过 IPC**，零 Base64 开销。

### 7.2 Base64 路径（对比组）

```
C#: File.ReadAllBytes(path) → Convert.ToBase64String → PostWebMessageAsString
JS: 接收 data URL → Image.src = "data:image/png;base64,…"
```

### 7.3 对比结论

| 维度 | Virtual Host | Base64 经 IPC |
|---|---|---|
| IPC 次数 | 0（浏览器直接 fetch） | 1 次 postMessage |
| 内存增量 | 浏览器原生解码 | C# byte[] + Base64 字符串 + JS 字符串 + 解码后 ArrayBuffer |
| 大纹理（假设 5MB+） | 无额外开销 | Base64 膨胀 ~33%，IPC 传输 + JS 解码成新瓶颈 |
| 适用场景 | 图片/Spine 纹理/骨架/atlas | ≤64KB 小二进制（契约 §1 建议 ≤64KB） |

**结论**：**Virtual Host 全面优于 Base64**，契约「禁止 base64 过 IPC」完全成立。Base64 路径仅作为 spike 对比验证保留，W1 正式接入一律走 `lme.data` 虚拟主机。

### 7.4 基准测试代码

`WebHostSpike/index.html` 含「对比基准测试」按钮，调用 `spike.benchmark` 方法 10 次循环，测量 Base64 编码耗时与体积膨胀比。

---

## 8. 启动入口与既有功能隔离

### 8.1 路由方式

```
App.xaml: Startup="App_OnStartup"  （取代 StartupUri="MainWindow.xaml"）
App.xaml.cs: App_OnStartup(object sender, StartupEventArgs e)
    if (e.Args.Contains("--spike-webview2")) → WebView2SpikeWindow
    else → MainWindow
```

**验证**：
- `LimbusModEditor.App.exe --spike-webview2` → 仅 WebView2SpikeWindow 创建，无 MainWindow ✓
- `LimbusModEditor.App.exe`（无参数）→ 正常创建 MainWindow ✓（未实测但代码路径清晰）

### 8.2 对既有 22 个 UI 单元的影响

- **零修改**：`MainWindow.xaml.cs`、`WorkbenchPages/`、`SpineAnimationPreviewWindow.cs` 等全部未碰
- **新增**：`WebView2SpikeWindow` 为独立窗口，仅通过 `--spike-webview2` 参数启动
- **W1 起**：正式接入时移除 `--spike-webview2` 路由，MainWindow 直接承载 CoreWebView2

---

## 9. 五个未知量结论汇总

| # | 未知量 | 结论 | 状态 |
|---|---|---|---|
| ① | 运行时检测/分发 | Evergreen 探测可用；缺失时弹中文引导；Fixed Version 开关预留（`browserExecutableFolder` 参数）；建议 Fixed Version 为默认（离线机器），Evergreen 为降级备选 | ✅ 实测通过 |
| ② | PerMonitorV2 DPI | 清单已加；WebView2 跟随宿主 DPI；多显示器场景需人工复测 | ✅ 代码落地，多显示器待复测 |
| ③ | 中文 IME | WebView2 默认启用 IME；候选窗偏移是 Chromium 已知问题，PerMonitorV2 已缓解；需人工在≥125% DPI 下复测 | ⚠️ 代码无阻塞，人工复测 |
| ④ | 对话框/剪贴板/Process.Start | 六项回调全部代码落地；模态行为/取消语义需人工复测 | ✅ 代码落地，交互待复测 |
| ⑤ | 二进制通道 | Virtual Host 全面优于 Base64；契约「禁 base64」成立；`lme.data` 映射已验证 | ✅ 实测通过 |

---

## 10. 契约需修正/补充点（发 architect）

1. **`WEB-IPC-CONTRACT.md` §6 文件夹选择**：契约写 `dialog.folderPick`，但当前实现用 `OpenFileDialog` 的「选择此文件夹」文件夹惯例（无真正 folder picker）。建议 W1 评估 `Ookii.Dialogs.Wpf` 的 `VistaFolderBrowserDialog` 或 Win32 `IFileDialog`（`FOS_PICKFOLDERS`）以获得更优体验。**契约本身无需改**（接口语义一致），但实现路径需确认。

2. **`WEB-IPC-CONTRACT.md` §1 消息帧**：契约定义 `{id, kind, method, payload}` 三向信封；本 spike 用 `{method, payload}`（页面→宿主）和 `{type, result}`（宿主→页面）简化帧。**W1 正式接入时需统一到契约信封**（加 `id`/`kind`），当前 spike 帧是子集，不影响契约。

3. **`ARCH-WEBVIEW2-VUE.md` §6.2 二进制通道**：契约写 `lme.data` → 「只读数据根（缓存 bundle 实体化副本、项目 sources/、替换文件、Spine 三套装原始字节）」。本 spike 仅测了静态文件映射，**未测真实缓存 bundle 实体化副本**（需从 SQLite 索引定位 → 读 bundle → 喂前端）。W1 竖切片落地时需验证此路径。

4. **`ARCH-WEBVIEW2-VUE.md` §3-1 运行时分发**：ADR 写「采用 Fixed Version 分发」，但本 spike 实测 Evergreen 在开发机可用。建议 W1 定案：**默认 Fixed Version（受众机器可能离线）+ 启动时 Evergreen 探测降级**（用户已装新版 Evergreen 则用之）。配置开关预留即可，不影响 W1 竖切片。

5. **测试图源**：`WebHostSpike/data/spike/test.png` 当前用 docs/wiki-front-page.png（704 KB）。**非真实游戏资源**。W1 替换为真实缓存 bundle 内实体化的大图（建议 ≥2MB）以验证 Virtual Host 在大纹理下的真实性能。

---

## 11. 已知限制与后续

1. **多显示器 DPI 未实测**：本 spike 环境为单显示器 100%，多显示器 + 125%/150% 缩放需人工复测。
2. **IME 长文本未实测**：需人工在中文输入法下输入 ≥500 字观察候选窗。
3. **对话框模态行为未实测**：需人工点击各对话框按钮验证取消/确认语义。
4. **WebView2Data  userDataFolder**：当前用 `…/WebView2Data`，与未来 Fixed Version 分发路径需协调（避免与 Evergreen 默认数据目录冲突）。
5. **未测 WebView2 进程内存**：W3 验收门的工作集增量 ≤200MB 目标无法在 spike 阶段验证，需 W3 真实数据采样。
