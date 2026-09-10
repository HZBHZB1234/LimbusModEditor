# Plan 11 — 音频工作台重做（跨 bank 样本总表 + bank 树 + 表缓存）

> 覆盖需求：「bank 页面没有所有音频的视图。」
> 依赖 plan-09（公共骨架、表缓存底座）。可与其他计划并行。
> 用户已确认视图形态：**跨 bank 样本总表 + bank→FSB→样本 树，两者并存**。

## 1. 现状与目标

现状（`BankWorkbenchPage.cs`）：纯 C# 建 UI；进入页面即 `ScanDirectory` 全量重扫（每次）；
只有「选中单个 bank → 看该 bank 的样本表」，**没有任何跨 bank 的音频总览**，
想找某个音效必须先猜到它在哪个 bank 里；两张 ListView 上下堆叠，空间利用率低。

目标页面：

```
[浏览列] [搜索行（bank 名 / 样本名）][视图切换 + 筛选行]
         ☰ 全部音频（默认）：跨全部 bank 的样本总表（虚拟化）
             列：样本名 / bank / FSB / codec / 采样率 / 声道 / 时长 / 大小 / 状态
             筛选：类型（全部/音频/事件/加密/无法识别）、codec、时长区间、仅已修改
             排序：名称 / 时长 / 大小 / bank
         🗂 bank 树：bank（← FSB（← 样本，惰性展开，样本名来自缓存）
         ☰ bank 列表：现状的 bank 表（文件名 / 类型 / FSB 数 / 大小）
[splitter]
[编辑列] 选中上下文（bank 或样本）+ 样本表 + 试听 / 导出 WAV / 用 WAV 替换 /
         导出整包 .bank / 导出 .rebank + 状态与导出报告
         从「全部音频」选中样本 → 「在 bank 树中定位」（展开到该 bank/FSB 并高亮）
```

## 2. 表缓存（`cache/bank-index.db`）

```sql
index_meta(source_key PRIMARY KEY, signature)         -- source_key = bank 目录
banks(path PRIMARY KEY, name, size_bytes, mtime_ticks, kind, kind_note, fsb_count,
      note, scanned_ticks)                            -- kind_note = 无法识别时的中文原因
samples(bank_path, fsb_index, sample_index, name, codec_name, sample_rate, channels,
        sample_count, data_size, data_offset,
        PRIMARY KEY(bank_path, fsb_index, sample_index))
CREATE INDEX ix_samples_name ON samples(name);
```

- **失效规则唯一**：`banks` 的 `(size_bytes, mtime_ticks)` 与磁盘不符（或新增/删除文件）才重新探测该文件；
  bank 变了则删除其 `samples` 行并重解析该 bank（不做全库重建）。
- 类型判定复用 `BankDirectoryService`（前缀快路径 + 流式探测，语义一字不改），
  缓存只负责「存结果 + 判新鲜度」——**不新增第三处 bank 解析实现**。
- 样本表复用 `BankDirectoryService.ReadSampleTable`（`Fsb5Parser`）。
- 时长 = `sample_count / sample_rate`（采样率 0 → `—`，不猜）。
- 冷建索引：并行度受限（`MaxDegreeOfParallelism` ≤ 4，机械盘友好）+ 进度条（n/1531）；
  事件 bank（SNDH 空）无样本行；加密 bank 记录 kind 与 FSB 数，**不解码**。
- 单行解析失败（FSB 结构坏）记 `note`，该 bank 仍可用，不整库失败。

## 3. 实施步骤

1. `BankIndexStore`（`Application/Assets/`）：建表/签名判定/增量 persist/查询（总表 + 树聚合）+ 单测。
2. `BankIndexService`：编排「枚举目录 → 签名比对 → 仅重解析变化文件 → 单事务写库」，
   进度回调（UI 用），可取消（页面卸载时取消，不阻塞）。
3. `BankWorkbenchPage` 改 `.xaml(.cs)`：接入 shell 骨架；三视图 + 筛选/排序；
   `DataGrid`（`EnableRowVirtualization`）承载 7 万行级总表；
   选中样本 → 预览列操作按钮（试听/导出/替换/整包导出/rebank 导出语义**与现状一致**）。
4. 树：`bank → FSB → 样本` 三层，样本来自缓存（已建索引则零解析）；未建索引时惰性解析该 bank。
5. 「在 bank 树中定位」「在全部音频中查看该 bank 的样本」两个跳转。
6. USAGE.md「音频工作台」节重写（三视图、索引、试听/替换/导出、FMOD 依赖）。

## 4. 验收标准（审查门）

- [ ] 真实目录冒烟：1531 个 bank 全部判定类型（音频/事件/其他计数与截图一致）；
      样本总表行数 > 0，抽 3 个 bank 与 `RealBankTests` 口径一致（codec/采样率/样本数）。
- [ ] **性能门**：冷建索引有进度且可取消；二次进页面签名全命中、**不重新解析任何 bank 文件**，
      进入耗时 <1.5s（计时证明）。
- [ ] 总表 7 万行级滚动/筛选不卡（虚拟化生效）；样本名搜索可定位。
- [ ] 试听 1 个样本（本机有 FMOD DLL）；导出 WAV 可播放。
- [ ] 替换样本 → 整包导出 `.bank` → `BankParser` 可重新解析且差异仅在目标样本（既有验收口径复跑）。
- [ ] `.rebank` 导出仍符合 `bankmod.py` 语义（`base_bank` 纯文件名、wav 数 > 0）。
- [ ] 编辑器全程不写游戏目录；加密 bank 明确中文提示且不解码。
- [ ] build + test 全绿；USAGE 更新。

## 5. 风险与边界

- 索引库体积：7 万行 × 小字段，几 MB 级，可接受；损坏即删库重建。
- 冷建索引耗时取决于磁盘（1531 文件读头部 + 音频 bank 的 FSB 样本表）；
  必须**可取消 + 有进度**，且失败不破坏已有库。
- `ScanDirectory` 现签名/返回值保持不变（`BankDirectoryServiceTests` 全绿是硬门），
  缓存是它的外层编排，不是替换。
