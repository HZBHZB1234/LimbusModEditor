# Plan 09 — 工作台公共骨架 + 表缓存底座（串行前置）

> 覆盖需求：「资源工作台交互页面设计非常好（树形 + 预览），其他三种模组的编辑器有很大问题……
> static mod 可以使用资源工作台类似的页面。事实上，我们也可以使用表来缓存这些数据。」
> 本计划是 plan-10/11/12 的**唯一共同前置**：先把「页面骨架」和「表缓存」两层做出来，
> 三个页面改造只消费它，不各自重复实现。

## 1. 问题诊断（2026-09 读码结论，作为事实依据）

| 页面 | 实现形态 | 具体缺陷 |
|---|---|---|
| 资源工作台 | `WorkbenchPages/AssetsWorkbenchPage.xaml`(+.xaml.cs 68KB) | 标杆：树/列表双视图、搜索防抖、splitter + `UiStateService` 持久化、预览按 Kind 切换、SQLite 扫描索引 |
| 文本工作台 | `TextWorkbenchPage.cs` 512 行**纯 C# 建 UI** | 无树；中间夹 150px「搜索结果」列表把布局切碎；键值表是全局展平（`Flatten` 5000 行硬上限截断）无层级；`_jsonBox` 160px 挤在底部；无缓存，每次进页面全目录重读 |
| 音频工作台 | `BankWorkbenchPage.cs` 541 行纯 C# 建 UI | 只有「逐 bank → 该 bank 样本」，**没有跨 bank 的全局音频视图**；无树；无缓存（每次 `ScanDirectory` 全量重扫）；两张 ListView 上下堆叠 |
| 静态工作台 | `StaticWorkbenchPage.cs` 520 行纯 C# 建 UI | 无树（只有扁平表列表）；搜索对 1392 张表**逐张 UTF-8 解码 + Contains**（GB 级文本，UI 会卡）；每次进页面重新解析 bundle 枚举 |

共同根因两条：
1. **UI 骨架各自重造**：三个页面用 C# 手搓 `#171D24/#2A3540/#7F93A4` 那套设计语言，
   与资源页 XAML 的 `Theme.xaml` 资源重复实现且走样（无树、无筛选行、无分隔层次）。
2. **没有数据层**：每次进页面重新解析源文件，所以既慢又不敢提供「全量视图」。

## 2. 交付物 A：公共 UI 骨架（`App/WorkbenchPages/WorkbenchShell.*`）

新增 `WorkbenchShell`（`UserControl` + XAML），把资源页的设计令牌与骨架抽成可复用件：

- **三列骨架**：`[浏览列 * MinWidth 280][6px GridSplitter][编辑/预览列 默认值 MinWidth 260]`，
  GridSplitter 样式与资源页一致（1px 竖线、hover 高亮 `#4C8DDA`、双击复位、拖动结束持久化）。
- **设计令牌**：颜色/字号/ListView 与 TreeView 的主题化（`#171D24` 面板、`#11151A` 列表底、
  `#2A3540` 边框、`#7F93A4` 次要文字、`#FFD37A` 已修改行）集中在 `Themes/Theme.xaml`，
  **不得**再在页面 C# 里出现硬编码颜色（验收 grep 门）。
- **列表↔树切换对**：`MakeViewToggle` 助手（沿用资源页 `ViewToggle` 样式语义；
  与资源页同样**默认树视图**）。
- **搜索行 / 筛选行 / 状态条 / 空态提示**统一构件。
- **共享 `JsonTreeEditor` 控件**（plan-10/12 共用，杜绝第二份实现）：
  JSON 树（对象/数组/叶子，惰性展开，大表不冻结）+ 值编辑（**按原值类型写回**：
  字符串/整数/浮点/布尔/null）+ 删除键（确认框）+ 原始 JSON 双 Tab（树视图 / 原文视图）。
  从 `TextWorkbenchPage.Flatten/SetNodeValue` 与 `StaticWorkbenchPage` 的同名逻辑提取，
  行为不变（含 5000 行上限→改为惰性展开，不再截断）。

页面接线：`WorkbenchShell` 通过 `IWorkbenchHost` 取值（不改接口），
`UiStateService` 扩展为**每页列宽**（`PreviewColumnWidths: Dictionary<string,double>`，
key = 页面 key；旧的 `AssetsPreviewColumnWidth` 读入时映射到 `assets`，保持向后兼容）。

**资源页本次只做两件事**：把硬编码颜色/骨架换成 shell 公共件（外观必须逐像素不变），
把列宽读写切到新字段。其余行为不动（避免与 plan-10/12 的静态改造撞车）。

## 3. 交付物 B：表缓存底座（`Application/Caching/`）

复用既有依赖与惯例：`Microsoft.Data.Sqlite 8.0.11`、`cache/*.db`、
WAL + `synchronous=NORMAL` + 单事务批量写（照抄 `UnityCacheSqliteIndexStore:115-214` 的策略）。

- `SqliteTableCache`：连接串构造、`EnsureSchema` 幂等建表、`EnsureColumn` 轻量迁移、
  单事务 `Persist`、**损坏即删库重建**（索引只影响速度不影响正确性）。
- `CacheSignature`：`(length, mtimeUtcTicks)` 组合签名 + `==` 判定；
  明确写清「mtime 变即重新解析」这条**唯一失效规则**（不缓存任何游戏版本常量）。
- 数据库文件（程序目录，与 `unity-cache-index.db` 同处）：

| 库 | 表 | 键 | 内容 |
|---|---|---|---|
| `cache/bank-index.db` | `index_meta` / `banks` / `samples` | 见 plan-11 | bank 头信息 + 逐样本行（「所有音频视图」的数据源） |
| `cache/static-tables.db` | `index_meta` / `tables` / `documents` | 见 plan-12 | 表元数据 + 按需 JSON 文本缓存 |
| `cache/text-index.db` | `index_meta` / `files` / `hits` | 见 plan-10 | 文件键数/大小 + 文件名/键/值命中 |

`index_meta(source_key PRIMARY KEY, signature)` 统一记录「本表是按哪个源目录建的」，
源目录换（共享配置改游戏目录）即整体失效重建。

**编辑集绝不落这份缓存**：文本/静态的编辑态仍走各自服务的内存编辑集 + 导出门控，
避免「缓存里是原版还是改动版」的歧义；缓存只存**原版（vanilla）事实**。

## 4. 事实来源与边界

- 缓存位置沿用既有惯例：程序目录 `cache/`（与 `unity-cache-index.db` 同处）。
- 单位换算统一：字节 `HumanSize` 既有转换器；时长 = `SampleCount / SampleRate`（采样率 0 时显示 `—`，不猜）。
- 一切写入只落 `cache/`；**绝不写游戏目录 / 缓存 / catalog**。
- 本计划不改变任何导出产物格式（`.bank` / `.rebank` / `lang.json` / `.staticmod` 一字不动）。

## 5. 实施步骤

1. `Themes/Theme.xaml` 增补共享令牌 + `WorkbenchPages/WorkbenchShell.xaml(.cs)`（骨架、splitter、toggle、状态条）。
2. `WorkbenchPages/JsonTreeEditor.xaml(.cs)`：从两个既有页面提取树/值编辑/原文 Tab。
3. `UiStateService` 扩每页列宽（迁移兼容 + `Sanitize` 钳制）+ `UiStateServiceTests` 补断言。
4. `Application/Caching/SqliteTableCache.cs` + `CacheSignature.cs` + 单测
   （建表幂等、签名失效、损坏库删重建、事务批量写往返）。
5. 资源页换用 shell 公共件 + 列宽迁移（**外观不变**）。
6. 基线三命令 + 手工冒烟：四个页面全部可打开、splitter 可拖、双击复位、列宽重启后保留。

## 6. 验收标准（审查门）

- [ ] build + test 全绿（基线 334 不得下降；新增缓存/UI 状态测试）。
- [ ] 资源页改造前后**外观一致**（截图对照：颜色值、列宽默认 360/最小 260、树默认展开行为）。
- [ ] `grep` 确认三个改造页与资源页的 XAML/C# 里不再出现硬编码 `#171D24` 等设计色（集中在 Theme.xaml）。
- [ ] `cache/` 新增库文件可被删除后自动重建；库文件被写坏（截断）后页面给出中文提示并重建，不崩。
- [ ] 四页 splitter 宽度各自持久化到 `config/ui-state.json`，重启保留；双击复位默认。
- [ ] 编辑器全程不写游戏目录（代码审查 + grep）。

## 7. 风险与边界

- 资源页是当前唯一「好用」的页面：改造限定为**结构迁移 + 颜色令牌替换**，
  禁止顺手调整交互（行为回归风险太高）。
- `JsonTreeEditor` 从两处提取时**行为逐条对齐**（类型写回、删除确认、非法 JSON 拒绝），
  提取后两个旧页面在 plan-10/12 才切换，本计划不切换 → 可随时回滚。
- 缓存库不加索引复杂度：四个库都很小（万级行），够用即可，不做查询优化。
