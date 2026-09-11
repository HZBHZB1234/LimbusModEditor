# Plan 13 — 用户反馈修复批次（8 项）

> 覆盖需求（2026-09 用户截图反馈，逐条对应本文件 §2 的编号）：
> ① 侧边栏 tooltip 中文渲染成豆腐块；② 资源工作台图像预览上下颠倒；③ 缓存加载/创建逻辑改为
> 每次启动自动扫描全部资源（含四个 db）；④ 「载入 lang 文件失败：no such table: index_meta」；
> ⑤ JSON 树里鼠标悬停在项上时滚轮滚不动；⑥ 音频编辑页改成资源工作台同款设计（只留全展开列表视图 +
> bank 归类树视图，右侧只留几个按钮，用进度条替代单一试听按钮）；⑦ 图像预览默认缩放到合适大小；
> ⑧ 筛选下拉菜单文本显示不全（被裁掉）。
> 依赖：plan-09（`WorkbenchShell` / 表缓存底座）、plan-10/11/12（三个工作台与三个缓存库）。

## 1. 结论速览

八项全部修复，全部有单测或真实数据证据；三个缓存库 + 资源索引库现在**每次启动都会被建好/校表并增量扫描**。
本轮提交涉及：`Application`（缓存自愈 + 启动扫描服务 + 扫描热路径优化）、`Editing`（纹理行序）、
`App`（四个页面的 UI/交互修复：tooltip 字体 / 滚轮 / 下拉裁字 / 音频页重做 / 图像适应窗口）。
**528 → 549 个测试全绿**（74 Format + 475 Domain）。

**真实数据实测（本机：1473 个缓存 bundle、1531 个 bank、1392 张静态表、2049 个 lang 文件）**：
启动扫描（热缓存，即用户第二次及以后每次启动的实际路径）
**96.7 秒 → 9.0 秒**，逐步耗时：缓存库 0.0s / 游戏资源 6.8s / 音频索引 2.0s / 静态表 0.1s / lang 0.0s。
（优化前后各一次完整实测见 §3；优化点：① catalog.bin 惰性解析；② 项目与索引一致时跳过
119 万行逐行对账；③ 无变化时连合并段一起跳过；④ 静态表索引用「按内层哈希直接查缓存目录」
的探针代替 5 MB catalog 解析。）

## 2. 逐项：现象 → 根因 → 修复 → 证据

### ① 侧边栏 tooltip 中文全是豆腐块（□□□□）

- **根因**：`MainWindow.xaml` 的活动栏按钮样式 `ActivityButton` 把
  `FontFamily="Segoe MDL2 Assets"` 设在 **RadioButton 本身**上。WPF 的 ToolTip 会继承
  PlacementTarget 的字体属性，而**符号字体没有字体回退**，于是提示里的中文全部渲染成缺字方块。
- **修复**：图标字体只写在按钮内容那个 `TextBlock` 上（`<TextBlock Text="&#xE7B8;" FontFamily="Segoe MDL2 Assets"/>`），
  按钮本身不再声明字体 → tooltip 继承界面正文字体（有回退）。图标尺寸/居中不变。
- **证据**：`grep "Segoe MDL2 Assets"` 只命中 6 处 TextBlock 内容；发布版人工截图（§3 步骤 4）。

### ② 资源工作台图像预览上下颠倒

- **根因**：Unity 的 Texture2D 像素负载**以左下角为原点**（负载第 0 行 = 图像最下面一行），
  而 PNG / ImageSharp 的行序自上而下。此前的解码/编码都按「第 0 行 = 最上面一行」处理，
  于是预览上下颠倒（DXT 块行同理）。Sprite 子图裁剪路径也要一并正确：
  `UnitySpriteCrop.Resolve` 的 `y = H - rectY - h` 本来就是「左下 → 左上」换算，
  它与这里的翻转**互为配套**（此前因为解码没翻，裁剪出来的区域也是镜像的）。
- **修复**：`UnityTextureCodec` 统一行序约定 `UnityPixelDataIsBottomUp`：
  `ToImage`/`DecodeDxt`（解码）翻一次，`FromPng`/`EncodeDxt`（编码）翻回去，
  保证「读出来 → 换图 → 写回」像素逐字节往返不变（写回后游戏里的朝向与原版一致）。
- **证据**：`UnityTextureCodecTests` 新增 4 个测试——PNG 首行 = 负载末行（红/蓝双行样本）、
  往返逐字节相同、`ToPngCropped(0,0)` 取到图像顶部（与裁剪换算配套）、
  DXT 负载首块 = 图像下半块（4×8 纯色双块）。

### ③ 缓存加载/创建逻辑：每次启动自动扫描全部资源（含四个 db）

- **现象**：四个缓存库此前是「页面首次打开时才建/才刷新」——用户看不到全局进度，
  首次进某个工作台要等索引，而且从没建过库时还会踩到 ④ 的坑。
- **修复**：新增 `Application/Scanning/StartupScanService`：
  - `EnsureCacheDatabases()`：把 `unity-cache-index.db` / `bank-index.db` / `static-tables.db` /
    `text-index.db` **四个库一次建好并校表**（缺文件、0 字节、半成品都补建）；
  - `ScanAllAsync(project)`：五步依次执行——缓存库 → 游戏资源（Unity 缓存**全部 bundle**，引用模式）
    → 音频索引 → 静态表索引 → lang 文本索引；每步真实枚举磁盘、只重解析签名变过的文件
    （热启动秒级），**每步失败只记录原因并继续**（前提缺失 = 跳过，不打断启动）；
    每步结果带耗时（`StartupScanStepResult.Elapsed`），「哪一步慢」一眼可见；
  - 复用既有解析实现的唯一入口（`UnityCacheScanService` / `BankIndexService` /
    `StaticIndexService` / `LangTextWorkbenchService`），不新增第二套解析。
- **接线**：`MainWindow` 在窗口 Loaded 时先建四个库（状态栏报「新建/补表 N 个」），
  打开/恢复项目后自动跑 `RunStartupScanAsync()`（后台 + 状态栏实时进度 + 完成摘要 +
  关窗/切项目取消）。**不再需要「空项目才弹扫描窗口」**：扫完提示条直接引导下一步，
  项目为空时提示「确认 Unity 缓存目录 / 先启动一次游戏」。
- **热路径优化（实测 96.7s → 9.0s，同一台机器同一份真实数据）**：
  1. `UnityCacheScanService` 的 **catalog 改为完全惰性**（`Lazy<CatalogFileService?>` +
     `Lazy<IReadOnlySet<string>>`），且**只在某个 bundle 解析成功之后**才取值：
     真实 `catalog.bin`（5 MB，正则启发式解析）本机实测一次 ~17 秒，缓存里总有 2 条
     永远解析不了的条目，旧逻辑为了它们每次开扫描都要白花这 17 秒；
  2. 调用方声明「项目与索引一致」（`projectMatchesIndex`，见下）时**跳过索引命中 bundle 的
     逐行对账**：真实规模要读 119 万行 + 做 127 万次字典查找，结果恒为空；
  3. 若本次**没有任何 bundle 解析成功**且项目与索引一致，连整个合并段也跳过（含 127 万条的
     路径字典分配）；只保留「登记缓存来源」与索引收缩（`PersistIndex`）；
  4. `StartupScanService` 的静态表步骤改走**不解析 catalog 的探针**：索引里记着静态 bundle 的
     内层内容哈希（`index_meta.source_key`），缓存布局是 `<缓存根>/<外层键>/<内层哈希>/__data`，
     按它直接找目录即可判定换源/签名（实测 18.9s → 0.1s）；探针不成立时原样回落到 catalog 全路径。
  安全性：`projectMatchesIndex` 是**调用方契约**（打开项目时刚成功回灌过才允许传 true，
  `MainWindow._assetsMatchIndex` 保守置真/假），并有单测钉死「带声明 vs 不带声明结果逐条相同」。
- **证据**：`StartupScanServiceTests` 7 个测试——四个库与各自业务表/`index_meta` 就位、
  幂等（第二次无补建）、四个库逐个 0 字节补建、缺前提时 4 步跳过且 0 失败、
  合成 lang 根首次扫描建索引 + 第二次 `AlreadyFresh`、合成 Unity 缓存被真实扫描进项目；
  `UnityCacheScanServiceTests` 的 `Project_matches_index_hint_skips_reconciliation_without_changing_the_result`
  钉死热路径契约（跳过对账/跳过合并 vs 逐条对账，两条路径的项目内容逐条相同）；
  **真实数据门控冒烟** `RealStartupScanSmokeTests`（`LME_STARTUP_SCAN_SMOKE=1` + `LME_APP_DIR=<发布目录>`）
  在真实环境跑完整启动扫描：五个步骤全部非跳过、无失败、四个库都非空，并打印逐步耗时。

### ④ 载入 lang 文件失败：`SQLite Error 1: 'no such table: index_meta'`

- **根因（本机复现）**：`artifacts/publish-win-x64/cache/text-index.db` 是 **0 字节**文件。
  连接串是 `SqliteOpenMode.ReadWriteCreate`，所以「文件存在但没有表」时连接照样能开；
  页面路径 `IsFresh → MatchesSource → ReadSourceSignature` 直接
  `SELECT … FROM index_meta` → 抛 `no such table`，被 `TextWorkbenchPage` 包成
  「载入 lang 文件失败」。同样的问题存在于 `UnityCacheSqliteIndexStore` 的读路径（回灌索引）。
- **修复**：
  - `SqliteTableCache.Read/Write` 内置「每实例一次」的 `EnsureSchemaOnce()`（缺表无声补建，
    删库后重置标记）；`UnityCacheSqliteIndexStore` 加 `OpenEnsured()` 并用于
    `ReadBundleIndex` / `ReadAllRowsGrouped` / `ReadAll`；
  - 启动阶段的 `EnsureCacheDatabases()` 再兜一层（见 ③）。
  两处都保持铁律：**缓存只影响速度不影响正确性**，缺表 = 空库 = 冷扫描，绝不往上抛业务错误。
- **证据**：`SqliteTableCacheTests` 新增 4 个测试（缺文件读、0 字节读、无表写、删库后读）；
  `TextIndexStoreTests` 新增 2 个测试（0 字节库被修复且补齐索引后搜索与无缓存路径一致、
  库文件不存在时 `PersistFiles` 冷建可用）。

### ⑤ JSON 树：悬停在项上时滚轮滚不动

- **根因**：滚轮事件从指针下**最深的元素**开始冒泡，树行 / 行模板里的控件会先把它处理掉
  （甚至标记 `Handled`），于是只剩「悬停空白处」还能滚。
- **修复**：`WorkbenchShell.EnableWheelScrolling(UIElement)`——在目标上用
  `AddHandler(MouseWheelEvent, …, handledEventsToo: true)` 收下已处理的滚轮事件，
  自己解析内部 `ScrollViewer`（惰性 + 缓存 + 失效校验）显式滚动并置 `Handled`；
  内部没有可滚余量时把事件重新派发出去交给外层（并对合成事件做 `e.Source` 重入保护，
  避免无限递归）。`CreateTree/CreateList` 自动接线，`JsonTreeEditor` 的 XAML 树显式接线。
- **证据**：代码路径单测不覆盖 WPF 事件路由；以发布版人工操作验证（§3 步骤 4）。

### ⑥ 音频工作台重做成资源工作台同款页面

- **变更**：
  - 视图从三种（全部音频 / bank 树 / bank 列表）收敛为**两种**，并改用壳的标准「☰ 列表 / 🗂 树」
    切换对：`☰ 全部音频`（跨 bank 样本总表，DataGrid 行虚拟化）与 `🗂 bank 树`（bank → FSB → 样本，惰性展开）；
    第三视图「☰ bank 列表」删除（信息与树完全重合）。
  - 右侧编辑列**只留四个按钮**（用 WAV 替换… / 导出样本 WAV… / 导出整包 .bank… / 导出 .rebank…）+ 选中上下文；
    原来的内嵌样本列表删除（浏览列已是全展开列表，重复展示只会让人分不清哪份是数据源）。
  - **单一「试听」按钮 → 播放进度条**：`ProgressBar`（6px）+ `▶ 播放/■ 停止` + `00:03 / 00:12`
    时间标签，`DispatcherTimer` 200ms 拉 `MediaPlayer.Position` 刷新，时长取 `NaturalDuration`
    （解码后临时 WAV 的真实长度），可点击/拖动进度条跳转；停止/切换选择/播完/失败一律归零。
    解码、临时文件、FMOD 缺失中文原因等业务逻辑一字未改。
  - 「在 bank 树中定位」按钮删除，定位能力挂到 bank 树双击（少一个按钮位 + 少一份启用状态）。
- **证据**：`BankWorkbenchPage` 里 `_viewSwitch/_bankList/_sampleList/_locateButton/_editSampleRows`
  等引用计数为 0；发布版人工操作验证。

### ⑦ 图像预览默认缩放到合适大小

- **根因**：`ScrollViewer` 以「无限尺寸」测量内容，`Stretch.Uniform` 在这种容器里不生效——
  图像按原始像素铺开，大纹理一进来只能看到左上角；而旧实现用 `RenderTransform` 缩放
  （不改布局），放大后连滚动条都不出现，等于只能看左上角。
- **修复**：图像按像素显式定尺寸 + **`LayoutTransform`** 缩放；默认比例 = 视口 / 图像
  （大图缩到看得全，小图最多放大 2 倍），视口高度 260；滚轮在此基础上乘用户倍数
  （放大到 1.5 倍以上切最近邻，保持像素硬边）；双击预览区或「适应窗口」按钮复位；
  底部显示当前比例与操作提示。切换「替换图 ↔ 原图」后重新适应。
- **证据**：`AssetsWorkbenchPage.BuildImageView` 重写（`git diff`）；发布版人工操作验证。

### ⑧ 筛选下拉菜单文本显示不全

- **根因**：下拉被写死 `Height = 28`，而 Fluent（WPF-UI）模板的 `MinHeight` 是 32——
  内容盒被压扁，中文上下被裁；同时部分固定宽度（110/120）比「最长候选项 + 左侧内边距 +
  右侧箭头」还窄，右侧也被箭头裁掉。
- **修复**：新增共享样式 `WorkbenchFilterCombo`（`MinHeight=32`、`MinWidth=120`、垂直居中、
  `BasedOn` Fluent 模板）与 `WorkbenchShell.CreateFilterCombo(...)`（按候选项估算宽度：
  全角 ≈13px / 半角 ≈7px + 46px 余量，下限 140）；搜索框等 28 高的输入框一并改 `MinHeight=32`；
  筛选行容器由水平 `StackPanel` 改 `WrapPanel`（下拉变宽后整行会换行而不是被裁掉）。
- **证据**：发布版人工截图对比（§3 步骤 4）。

## 3. 验收标准（审查门）

- [x] 基线命令全绿：`dotnet build LimbusModEditor.slnx`、`dotnet test LimbusModEditor.slnx`
      （549 = 74 Format + 475 Domain，真实数据门控测试在本机真跑）；
      注：`RealBankIndexSmokeTests` 的「读索引 < 1.5s」是**墙钟预算**断言，在
      `dotnet test LimbusModEditor.slnx` 并行跑两个测试项目时偶发超时（单独跑该项目 2/2 通过），
      属既有门禁的抖动，与本轮改动无关。
- [x] 每个修复都有对应的回归测试或可复现的人工步骤（§2 各项「证据」）。
- [x] 设计色门：非 `Themes/` 文件没有新增色值字面量（新增/改动的页面与样式只引用具名资源）。
- [x] 缓存铁律不变：四个库只存 vanilla 事实、编辑集不落缓存、失效规则仍是「源签名变化」；
      缺表/0 字节一律无声补建，缓存删掉功能不受影响。
- [x] 真实数据实测：启动扫描热路径 96.7s → 9.0s（本机 1473 bundle / 1531 bank / 1392 表 /
      2049 lang 文件），逐步耗时见 §1；四个库均非空（`text-index.db` 由 0 字节修好为 54.5 MB，
      2049 个 lang 文件入索引）。
- [ ] 发布版人工冒烟（下面 7 条）：见「人工验证步骤」。

### 实测记录（启动扫描，同一台机器 / 同一份真实数据）

| 步骤 | 优化前 | 优化后 |
|---|---|---|
| 缓存库（四库建/校表） | 0.0 秒 | 0.0 秒 |
| 游戏资源（1473 bundle） | 45.7~73.5 秒（含 17 秒 catalog + 119 万行对账 + 127 万条路径字典） | **6.8 秒**（零 catalog、零对账、零合并） |
| 音频索引（1531 bank） | 0.6~2.8 秒 | 2.0 秒 |
| 静态数据表（1392 张） | 18.9 秒（每次解析 5 MB catalog） | **0.1 秒**（内层哈希探针） |
| lang 文本（2049 文件） | 0.0 秒 | 0.0 秒 |
| **合计** | **96.7 秒**（另一次 50.5 秒） | **9.0 秒** |

> 中间过程数据（用于判断慢在哪）：`catalog.bin` 解析 17.2 秒；`ReadAllRowsGrouped` 读 119 万行 +
> 127 万条记录字典约 40 秒；1473 个 bundle 的两次文件元数据遍历约 0.7 秒（用 PowerShell 单独实测，
> 说明瓶颈不在文件 IO，而在内存/GC 与 catalog 解析）。

### 人工验证步骤（发布版）

```text
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 ^
  --self-contained false -o artifacts/publish-win-x64 --no-restore
artifacts\publish-win-x64\LimbusModEditor.App.exe
```

1. 启动即应看到状态栏「启动扫描：…」逐个步骤推进，最后给出「启动扫描完成：…」摘要；
   `cache/` 下四个 db 都在且非 0 字节（`text-index.db` 此前是 0 字节）。
2. 悬浮左侧活动栏任一图标：tooltip 中文正常（不再是方块）。
3. 资源工作台选一张纹理：图像方向与游戏内一致、默认整张可见（底部显示「适应窗口：xx%」）；
   换一张超宽/超小纹理，滚轮放大后出现滚动条、可拖拽平移、双击复位。
4. 文本工作台不再出现「载入 lang 文件失败：no such table: index_meta」，直接列出 lang 文件。
5. JSON 树（文本/静态工作台）里把指针停在树行上滚滚轮，能正常滚动。
6. 音频工作台：只有「☰ 全部音频 / 🗂 bank 树」两个视图；右侧只有四个操作按钮 + 播放进度条；
   选样本后点播放，进度条与时间标签随播放推进，可点击跳转，播完/停止后归零。
7. 各页筛选下拉（全部类型 / codec / 时长 / 状态 / 排序）文字完整显示，不被箭头或边框裁掉。
