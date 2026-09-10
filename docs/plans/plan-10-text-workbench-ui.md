# Plan 10 — 文本工作台重做（树形浏览 + 共享编辑器 + 表缓存）

> 覆盖需求：「文本编辑器页面不美观，没有树形查看。」
> 依赖 plan-09（公共骨架 `WorkbenchShell`、`JsonTreeEditor`、表缓存底座）。可与其他计划并行。

## 1. 现状与目标

现状（`TextWorkbenchPage.cs`）：纯 C# 建 UI；无树；「搜索结果」列表挤在浏览列中间；
键值表是**全局展平**（`Flatten` 遇 5000 行直接 `return` → 大文件后半截根本编辑不到）；
`_jsonBox` 只有 160px；每次进页面 `EnumerateFiles` 全目录重读。

目标页面（三列，与资源工作台同骨架）：

```
[浏览列]  [搜索行][文件/树切换][筛选：全部/已修改/键数]
          🗂 树（默认）：lang 根 → config.json / 活动语言目录 → 子目录（StoryData 等）→ 文件 → JSON 键层级
                        （点两级直达某个键，叶子双击 = 定位到编辑器该行）
          ☰ 列表：扁平文件表（文件/键数/大小/状态）
[splitter]
[编辑列]  顶部：文件路径 + 已修改徽标 + 「还原此文件」
          Tab1 键值树 | Tab2 原始 JSON（共享 JsonTreeEditor）
          按钮：应用修改 / 删除此键 / 保存原始 JSON / 导出 lang 补丁 / 直接应用到 lang 目录（警告）
          底部：编辑集状态（N 个文件已改）+ 差异摘要
```

## 2. 表缓存（`cache/text-index.db`）

- `index_meta(source_key PRIMARY KEY, signature)`：`source_key` = lang 根（规范化路径）；
  签名变（换游戏目录）→ 整库重建。
- `files(rel_path PRIMARY KEY, size, mtime_ticks, key_count, is_utf8)`：
  文件级索引，**代替进页面全目录重读**；签名 = `(size, mtime_ticks)`，变则重解析该文件。
- `hits(rel_path, kind, key_path, snip, seq)`：文件名/键/值命中，
  `kind ∈ {File, Key, Value}`；搜索改为 SQL 查询（`snip LIKE`），
  **不再对每个文件现解码**（现状 `Search` 逐文件读盘 + JSON 解析）。
- 不存键值行：树按需**只读被选中的那一个文件**（与资源页预览同策略），
  内存展平改为惰性展开，杜绝 5000 行截断。

## 3. 数据流

1. `LangTextWorkbenchService.EnumerateFiles` 增加「签名命中则直接读库」的旁路
   （保持既有签名/返回值不变，缓存对它透明；`LangTextWorkbenchServiceTests` 全绿是硬门）。
2. `Search` 增加库查询路径；库缺失/被删时自动回退到现有实现（缓存只加速，不改变语义）。
3. 编辑集、导出补丁（`ExportPatch`）、`ApplyToDirectory` **完全不动**——
   缓存只存 vanilla 事实，编辑态仍在 `LangTextWorkbenchService` 内存里。

## 4. 实施步骤

1. `TextIndexStore`（`Application/Texts/`）：建表/签名判定/重建/persist/查询 + 单测。
2. `TextWorkbenchPage` 改 `TextWorkbenchPage.xaml(.cs)`：换成 shell 骨架；
   浏览列加树/列表切换 + 独立「搜索结果」视图（命中态才显示，不再占常驻高度）；
   编辑列接 `JsonTreeEditor`（树 + 原文 Tab），按钮语义与现状一一对应。
3. 树枚举：目录层级（`StoryData/` 等子目录 → 文件 → JSON 键）走 `LangTextTreeBuilder`（纯函数，可单测）；
   文件名与键路径双路径搜索。
4. 大文件（`BattleSpeechBubbleDlg.json` 359KB、`Skills.json` 292KB）异步加载 + 惰性展开，UI 不冻结。
5. USAGE.md「文本工作台」节重写（树形浏览、搜索、编辑集、导出、直接应用警告）。

## 5. 验收标准（审查门）

- [ ] 真实 lang 目录冒烟：活动语言 `LLC_zh-CN`；树含全部子目录（StoryData 920 文件）与根级文件；
      点两级可直接定位 `AbDlg_Faust.json` 里的键。
- [ ] 修改某值 → 导出补丁 → `TextDiffService.Apply` 回放可还原（与现状同口径）。
- [ ] 冷建索引 vs 二次进页面：二次进入**不再全目录重读**（日志/计时证明，目标 <1s）。
- [ ] 搜索：文件名/键/值三类命中都可跳转到文件并定位键；1392+ 文件搜索不冻结 UI。
- [ ] 大文件打开与滚动流畅；键值树不再有 5000 行截断（超过 5000 键的文件可完整查看）。
- [ ] 默认不写游戏 lang 目录；「直接应用」保留警告与 `.bak` 行为说明。
- [ ] build + test 全绿；USAGE 更新。

## 6. 风险与边界

- 只处理 `config.json` 指向的活动语言目录 + 根级 `config.json`（其他翻译组目录不索引）。
- 编码统一 UTF-8；非 UTF-8 文件标记 `is_utf8=0` 并在 UI 明示，不猜编码。
- 树节点上限保护：单文件键数极大时按层惰性展开（不做全量物化）。
