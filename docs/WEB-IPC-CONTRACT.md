# Web 宿主 IPC 契约（WEB-IPC-CONTRACT）

- 状态：**冻结**（2026-09-15，W0 冻结波次；与 `docs/ARCH-WEBVIEW2-VUE.md` 同步生效）
- 适用范围：WebView2 宿主（WPF 薄壳）↔ Vue3/TS 前端（`src/LimbusModEditor.Web/`）之间的**全部**消息交换。
- **归属铁律**：本契约的消息帧与 DTO **定义在 `src/LimbusModEditor.Application/Ipc/`（net8.0，无 WPF 依赖）**，可被 `Domain.Tests` 直接单测——铁律 §3-10（需测逻辑下沉 Application）。宿主传输层（`CoreWebView2.PostWebMessageAsJson` / `WebMessageReceived`）唯一的例外，在 App 宿主项目内。
- 复用既有纯数据形状（不新造）：`AssetCatalogPage(Items,TotalCount,Offset,Take)`、`AssetSearchQuery`、`RelationDeepLink` 的 `'\0'` 分段载荷、`IReferenceRevealable.Reveal` 的布尔语义、`AssetRecord`/`AssetDisplay` 的既有字典约定（大小写不敏感 `Metadata`）。
- 契约版本：`v1`。演进规则：**只加不删**（新增 method/字段可选）；破坏性变更升版本号并双版本并存一个波次。与 `RelationIndexSource.FormatVersion`（派生库口径）无关，互不触发。

---

## 1. 消息帧与三个方向

所有消息都是 JSON，外层信封：

```json
/* 请求（前端 → 宿主） */
{"id":"req-0001","kind":"request","method":"catalog.query","payload":{...}}

/* 响应（宿主 → 前端， id 与请求对应） */
{"id":"req-0001","kind":"response","ok":true,"payload":{...}}
{"id":"req-0002","kind":"response","ok":false,"error":{"code":"cancelled","message":"用户取消"}}

/* 事件（宿主 → 前端推送，无 id，可多次） */
{"kind":"event","method":"progress","payload":{...}}
```

| 方向 | 说明 |
| --- | --- |
| **请求** request | 前端发起，必须带全局唯一 `id`（前端生成，建议 `req-<序号>`）；宿主必须回同 `id` 的 response（成功或失败）。 |
| **响应** response | 宿主回执：`ok=true` 带 `payload`；`ok=false` 带 `error{code,message}`（message 一律中文，铁律 §3-2）。 |
| **事件** event | 宿主主动推送：进度、状态变更、对话框异步回调、toast。前端不回复事件。 |

传输纪律（不变量）：**禁止 base64 二进制过 IPC**（§5）；单条消息体建议 ≤ 64KB（JSON 序列化后），大一律走二进制通道或文件。

> **W0 spike 说明**（2026-09-16，`docs/SPIKE-WEBVIEW2.md`）：spike 期宿主用简化帧
> `{method, payload}`（页面→宿主）/ `{type, result}`（宿主→页面）做端到端验证，
> 是本报封的**子集**（无 `id`/`kind` 字段）。**W1 正式接入统一到本报封**（请求带全局唯一 `id`、
> 响应回同 `id`、事件带 `method`），前端按本报封实现、宿主 W1 起只发本报封；spike 简化帧不进入 W1 代码。

## 2. 请求/响应清单（按域）

> W1 起按竖切片落地；标 ★ 为 W1 竖切片（资源台）必须，其余 W2 补全。

### 2.1 资源目录（统一分页）

```
catalog.query ★    payload: { query: AssetSearchQuery, offset: int, take: int }
                    response payload: { items: AssetRecord[]（页内条目）, totalCount: int, offset: int, take: int }
                    形状即 AssetCatalogPage(Items,TotalCount,Offset,Take)
catalog.count       payload: { query: AssetSearchQuery }   → { totalCount }
catalog.locate      payload: { assetId }                  → { offset }（定位某资产在第几页，配合前端跳页）
```

### 2.2 资产预览/读取

```
asset.preview ★     payload: { assetId } → { kind: AssetPreviewKind, rows: PropertyRow[], binaryUrl: string|null }
                    binaryUrl 指向二进制通道（§5），前端直接 <img>/<audio>/spine 加载；无则 null
asset.readText      payload: { assetId, charLimit? } → { text }（超长由宿主截断闸门，铁律不变量 §6-21/§6-26）
```

### 2.3 资产编辑（编辑集仍在宿主，前端只传操作）

```
asset.edit.replacePayload  ★   payload: { assetId, replacementPath } → { ok }
asset.edit.fieldEdit       payload: { assetId, edits: UnityFieldEditSet } → { ok }（写回前校验在宿主）
asset.edit.spriteMetadata   payload: { assetId, rect, pivot, border, ppu } → { ok }
asset.edit.batchReplace   payload: { sourceDirectory, namePattern?, onlyUnreplaced? = true }
                          → { replaced, skipped, items: [{ assetId, logicalPath, replacementPath }], warnings, info }
                          按文件名从目录批量登记替换（源目录只读，文件复制进项目 edits/assets）
asset.edit.applyFieldEdits / asset.edit.undo …（与现有 AssetEditService/UnityFieldEditService/SpriteMetadataEditService 一一对映）
asset.import                payload: { packagePath, format? } → { imported: int, errors: string[] }
```

### 2.4 Spine（W2 按移除/保留清单落地）

```
spine.locate     payload: { assetId } → { folder: string, files: { skeleton: binaryUrl, atlas: binaryUrl, pages: binaryUrl[] }, pages: string[] }
                  「定位并流式喂原始字节」：前端拿 URL 自行用 spine-ts 加载渲染；缺件给中文原因（绝不白图）
spine.export     payload: { assetIds: string[], targetDirectory } → { written: string[], skipped: string[] }
                  目标清单由前端决定（哪些资产生成条目），写盘由 C# 执行（SpineExportService）
```

### 2.5 关联与精确跳转

```
relation.describe   payload: { assetId } → { subjects: RelationSubject[] }（资源→对象，反向索引）
relation.links      payload: { subjectId } → { links: RelationLink[] }（对象→资源）
relation.reveal     payload: { payload: string, fallbackKeyword: string } → { located: bool }
                    payload 即 RelationDeepLink 的 '\0' 分段载荷，**原样透传不解析**；
                    located=true = 宿主已定位并选中滚动到该行；false = 前端退化为关键词过滤（IReferenceRevealable 布尔语义，失败不抛）
```

### 2.6 项目 / 导出 / 调试

```
project.open / project.save          → { ok }
export.plan                          → { slots: PlannedSlot[], skippedReasons: string[] }
export.run ★                         payload: { targetDirectory }（进度走 §4 事件；取消走 §3）
debug.apply                           payload: { target = game directory }（备份+还原清单在宿主，铁律 §3-3）
```

### 2.7 配置 / UI 状态

```
config.read / config.write           payload: { key } / { key, value }（shared-config.json / ui-state.json 的 Web 对应物）
uiState.read / uiState.write         payload: { pageKey, columnWidth? }（列宽钳制 260–2000 语义不变）
```

## 3. 错误与取消语义

### 3.1 错误

- 所有失败一律 `ok:false + error{code,message}`；message 中文；**fail fast，不猜测、不静默降级**（铁律 §3-2/§3-15）。
- 错误码（闭集合，新增只能追加）：

| code | 含义 |
| --- | --- |
| `cancelled` | 用户取消（导出/调试/长查询） |
| `not-found` | 资源/项目/对象不存在 |
| `invalid-query` | 查询参数非法（含 `AssetSearchQuery` 校验失败） |
| `io-error` | 读写失败（含原子写回退） |
| `unsupported` | 无真实样本的格式兼容宣称一律走此码并附中文原因 |
| `internal` | 宿主内部错误（附日志位置） |

- 未知负载/压缩/字段 → `internal` + 中文原因 + 日志（不静默「成功」）。

### 3.2 取消

- 前端发 `{kind:"request", method:"cancel", payload:{operationId}}`；
- 宿主在**协作式检查点**（导出槽位/逐对象逐资源边界、查询页边界）响应：进行中的不可中断部分跑完后回 `cancelled` 响应；
- 取消后宿主必须清理临时中间产物（不留半成品，对应 `AtomicOutput` 语义）；
- 与既有不变量 §6-17/§6-18 一致：关窗/取消按钮等效。

## 4. 进度与取消事件

```json
{"kind":"event","method":"progress","payload":{"operationId":"export-000","phase":"carra2","current":37,"total":100,"message":"正在重打包…"}}
```

- `progress`：首条与末条必达；中间事件节流（≥200ms 间隔或 1% 进度变化才发）——对应 C# 侧 `ThrottledProgress`，避免淹掉 WebView2 消息队列；
- `event: state.scanDone` / `state.projectChanged` / `state.indexInvalid`：宿主索引/项目变更通知，前端据此失效缓存；
- `event: toast`：`{level, message}` 宿主级提示（中文）；
- 导出/调试共用同一 `operationId` 生命周期（创建→progress×N→完成/取消/错误）。

## 5. 二进制通道（禁止 base64）

- 宿主启动时注册两个本地虚拟主机映射（`SetVirtualHostNameToFolderMapping`）：

| 主机名 | 映射目录 | 用途 |
| --- | --- | --- |
| `https://lme.app/` | `<程序目录>/wwwroot` | 前端静态资源（`dist/` 发布落点） |
| `https://lme.data/` | 只读数据根 | 缓存 bundle 实体化副本、项目 `sources/`、替换文件、Spine 三套装原始字节 |

- 图片/Spine 纹理/骨架 `.json`/atlas `.txt`/PNG 字节**一律**通过 `https://lme.data/...` URL 直接加载（`<img>`/spine-ts loader/fetch）；**禁止 base64 过 IPC**（大纹理下立即成为新瓶颈）。
- 数据根只读：`lme.data` 不映射任何写路径；写操作（替换/编辑/导出）全部走 §2.3/§2.4/§2.6 请求，由宿主在项目目录内落盘（铁律 §3-3 口径不变）。
- 前端启动导航：`https://lme.app/index.html`。

## 6. 原生对话框与剪贴板 / Process.Start 回调通道

**铁律：文件/文件夹选择一律用宿主 Win32 对话框**（保留系统外观、真实路径、拖拽语义）；**禁止** Web `<input type=file>` 顶替。

```json
/* 对话框（同步请求-响应，宿主模态执行） */
request  dialog.openFile    { title, filters?, startPath? }          → { ok, path }          // 取消 → { ok:false }
request  dialog.saveFile    { title, filters?, startPath?, defaultName? } → { ok, path }
request  dialog.folderPick  { title, startPath? }                     → { ok, path }
request  dialog.messageBox  { text, title, buttons, icon? }           → { button }

/* 剪贴板（纯文本/路径交换经宿主，避免 WebView2 剪贴板语义差异） */
request  clipboard.readText   → { text }
request  clipboard.writeText  { text }

/* Process.Start（白名单：游戏启动器等既有 5 处） */
request  process.start   { appId?: "game"|..., args? }                → { ok, exitCode, stderrTail? }
```

- 对话框结果异步时以 `event: dialog.callback` 推送（与 `dialog.messageBox` 请求-响应二选一，按调用点语义定，W0 spike 定案）；
- `process.start` 仅限白名单 appId，宿主侧游戏目录只读（唯一例外仍为调试应用，§2.6 `debug.apply`）。

> **实现注记（W1 定案，契约语义不变）**：`dialog.folderPick` 的**接口语义**（标题/起始路径/返回路径/取消）
> 已冻结；**实现路径** W1 评估后定案——当前 spike 用 `OpenFileDialog`「选择此文件夹」惯例
> （与既有 `ExportMod_Click` 一致），可选更优体验：`Ookii.Dialogs.Wpf` 的 `VistaFolderBrowserDialog`
> 或 Win32 `IFileDialog`（`FOS_PICKFOLDERS`）。选定后宿主侧实现替换，契约零改动。

## 7. 事件/请求版本与兼容

- 契约版本 `v1` 起算；宿主在首个事件里带 `contractVersion` 与程序版本（`session.hello` 事件，前端据此判定兼容性）；
- 前端不认识的 method 必须忽略并记日志（不崩溃）；宿主不认识的请求回 `internal` + 中文「未知方法」；
- 分页形状 `AssetCatalogPage` 一旦冻结不再变更字段（只追加可选字段）；`Metadata` 字典保持大小写不敏感（铁律不变量 §6-10）。

## 8. 不变量速查（实现与测试的验收对照）

| # | 不变量 |
| --- | --- |
| 1 | 所有列表一律「查询 → 一页」（`catalog.query`/`asset.preview` 等）；**禁止取全量再前端筛**（`AssetCatalogPage` 是唯一分页形状） |
| 2 | 二进制一律走 `lme.app`/`lme.data` 本地虚拟主机；**禁止 base64 过 IPC** |
| 3 | 对话框/剪贴板/`Process.Start` 走请求回调，宿主 Win32 执行；Web 文件输入不顶替 |
| 4 | 错误一律 `ok:false + error{code,message}`（中文）；取消走 `cancel` 请求 + 协作式检查点 |
| 5 | 进度事件节流（首/末条必达，中间 ≥200ms） |
| 6 | `relation.reveal` 的 `'\0'` 分段载荷原样透传；响应 `located:false` 时前端退化为关键词过滤（与 `IReferenceRevealable` 一致） |
| 7 | 契约 DTO 在 `Application/Ipc`（无 WPF）；宿主传输是唯一 WPF 依赖点 |
| 8 | 铁律 §3-10 不变：契约与宿主网关逻辑全部可被 `Domain.Tests` 覆盖（W1 起补测） |
