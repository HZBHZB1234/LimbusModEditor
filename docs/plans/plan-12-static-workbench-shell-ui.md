# Plan 12 — 静态数据工作台重做（资源工作台同款页面 + 表缓存）

> 覆盖需求：「static mod 可以使用资源工作台类似的页面。」
> 依赖 plan-09（公共骨架、`JsonTreeEditor`、表缓存底座）。可与其他计划并行。

## 1. 现状与目标

现状（`StaticWorkbenchPage.cs`）：纯 C# 建 UI；浏览列只有一个扁平表列表，**无树**；
搜索对 1392 张表**逐张 UTF-8 解码后 `Contains`**（GB 级文本，触发即卡）；每次进页面
重新 `Locate` + `ReadTextAssetEntries`（重新解析 bundle）；编辑器是展平键值表 + 原始 JSON 框。

目标：**与资源工作台同款浏览列 + 静态表编辑器**：

```
[浏览列] [搜索行][🗂 树 | ☰ 列表][筛选：全部/已修改][排序：按 dataClass/名称/大小]
         🗂 树（默认）：dataClass（如 personality）→ 表（m_Name 末段），与资源页「按容器目录」语义对齐
         ☰ 列表：扁平表（数据类 / 表 / 大小 / 状态，已修改金色标记）
[splitter]
[编辑列] 表路径 + 定位信息（catalog 来源 / 外层键 / 缓存 __data）
         属性（只读：container、pathId、大小）
         Tab1 键值树（共享 JsonTreeEditor）| Tab2 原始 JSON | Tab3 与官方版本差异（RFC6902 摘要）
         按钮：应用修改 / 保存原始 JSON / 还原此表 / 导出 .staticmod
```

## 2. 表缓存（`cache/static-tables.db`）

```sql
index_meta(source_key PRIMARY KEY, signature)   -- source_key = 外层键 + 内层键（catalog 动态解析结果）
tables(container_entry PRIMARY KEY, name, data_class, serialized_file, path_id,
       size_bytes, is_utf8, cached_ticks)       -- 索引与搜索用的元数据
documents(container_entry PRIMARY KEY, text, size_bytes)  -- 按需缓存的 JSON 文本（原版快照）
```

- 失效规则：`source_key`（外层+内层内容哈希）变 → 整库重建（**热修换 hash 天然作废，绝不缓存 hash 常量**，
  对齐 plan-08 §5 铁律 2）。文件级校验：内容哈希未变即视为同一份 bundle。
- `documents` 只在「首次打开某表」时写入（1392 张表全量文本可能 GB 级，**不全量预缓存**；
  提供设置项/页面入口「清空静态表缓存」）。
- **搜索改为 SQL 命中**：`tables` 上按 name/data_class 即时查；
  表内键/值全文搜索走 `documents`（已缓存的那部分）+ 实时解码兜底并**后台+防抖**，
  绝不再同步逐张解码 1392 张表。
- 编辑集仍只在内存（`_modified`/`_vanilla`），缓存只存 vanilla；导出 `.staticmod` 路径一字不改。

## 3. 实施步骤

1. `StaticTableIndexStore`（`Application/StaticMods/`）：建表/签名判定/persist/查询 + 单测
   （合成 fixture：外层键变化 → 重建；同 bundle 重连 → 命中）。
2. `StaticWorkbenchPage` 改 `.xaml(.cs)`：接入 shell；树/列表切换；筛选与排序；
   搜索走缓存（后台防抖）；属性面板（复用既有 `container`/`pathId` 元数据）。
3. 编辑器换成共享 `JsonTreeEditor`（树/原文双 Tab）+ diff Tab
   （`TextDiffService` 生成 RFC6902，展示 op 数与摘要；导出仍是
   `StaticModService.CreateJsonPatchPackage`，manifest 带 `container`）。
4. 与资源页的联动复核：资源页「显示静态数据表」开关默认关的行为不变
   （`AssetSearchQuery.ShowStaticTables`），静态表在资源页仍默认不出现。
5. USAGE.md「静态数据工作台」节重写（树形浏览、搜索、缓存说明与清理入口、导出与加载器开关）。

## 4. 验收标准（审查门）

- [ ] 真实环境冒烟：catalog 定位成功（外层键 + 缓存 `__data` 命中）、枚举 1392 张表；
      🗂 树按 dataClass 分组正确（`SplitTableIdentity` 口径）。
- [ ] 搜索：表名即时命中；表内键/值搜索不冻结 UI（后台 + 防抖），结果可跳转。
- [ ] 修改一张表 → diff Tab 显示 RFC6902 摘要 → 导出 `.staticmod` → `StaticModService.Read`
      回读一致、manifest 含 `container`（与 plan-08 验收同口径复跑）。
- [ ] 缓存门：二次进页面不再重新 `ReadTextAssetEntries`；缓存库删除/写坏后自动重建不崩；
      模拟外层键变化（伪造 signature）→ 整库重建，旧数据不残留。
- [ ] 资源工作台默认视图仍不出现静态数据表（开关行为未回归）。
- [ ] 编辑器全程不写 catalog / 缓存 / 游戏目录；USAGE 更新；build + test 全绿。

## 5. 风险与边界

- `documents` 缓存体积：只缓存「打开过的表」，页面提供清理；不预缓存全量。
- 静态表 JSON 很大（MB 级）：树惰性展开 + 异步加载 + 代际守卫（沿用资源页 `_loadGeneration` 模式）。
- `full` 整文件替换通道仍不在页面暴露（`StaticModService` 能力保留）。
