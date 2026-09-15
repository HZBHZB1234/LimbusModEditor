# 资源库与高开销流程重构（2026-09-14）

本次实际修改资源索引的数据布局、启动扫描/回灌的内存使用、catalog 定位算法、
Carra2 导出对象读取和 Carra 差分查找。游戏文件始终只读，输出和基准库放在独立目录。

## 数据库格式与运行行为

资源库 `unity-cache-index.db` 升级为 `PRAGMA user_version=2`：

| 表/索引 | 数据与用途 |
|---|---|
| `bundles` | 整数 `id`；完整磁盘路径、大小、时间、外层/内层键、静态标记各存一次 |
| `strings` | 容器名与基线文字去重，按整数引用；读回同一字符串实例 |
| `assets` | `(bundle_id,bundle_index)` 聚簇主键，`WITHOUT ROWID`；对象标识、类型、大小与可选游戏路径 |
| `ix_assets_named` | 只含有游戏路径的行，覆盖关联分析所需三列 |

旧库不迁移内容，版本不符时在事务中清空并重建表结构，随后回收旧库空页。
缺失业务表也使全部 bundle 新鲜度失效。首次升级需要完整扫描游戏源。
这只影响可重建的资源缓存；项目编辑内容和加载器产物格式保持原有语义。

读回使用一个 SQLite 快照和顺序游标，每次仅构造一个 bundle 的索引行；指定 bundle
时按整数主键读取对应范围。扫描不再同时持有整库中间行字典；冷解析最多并行四个
bundle，只构造索引行，合并时才构造资产，目录字符串在 bundle 内共享。
写入继续采用单事务、预编译语句、增量更新和级联淘汰，异常或取消会回滚整个事务。

冷扫描直接消费后端 descriptor，去掉 `UnityAssetService.ScanBundle` 产生的百万级临时
`AssetRecord`。基线校验借用本次已经解包的流，不再为 CRC 重复打开和解包同一 bundle；
使用 Microsoft `System.IO.Hashing` 8.0.0 的流式 IEEE CRC32，计算前后恢复流位置，
不会释放借用流。解析失败仍不触发 catalog 惰性加载。

catalog 原来对每个内容哈希重新扫描整个二进制文件，现在对全部目标 Hash128
建立集合后一次线性扫描；名称匹配位置也直接复用。保留首次命中、名称排序、
双布局自校准、外层键兜底和未知数据处理规则。

Carra2 导出按源 bundle 分组，组内只解包一次并缓存 SerializedFile，组结束立即释放。
只读会话不跨写入、不并发共享。逐对象状态与类型表索引保持原有口径；进度统计由反复
计数列表改为递增计数，日志按 bundle 输出。Carra 差分直接比较字节，并通过字典查找
修改后的对象，去掉重复 SHA256 与逐变更线性查找。

## 真实数据结果

本机 .NET 8 / Windows、Debug 构建。使用同一份真实资源索引副本：
**1,275,623 个对象、51,376 条有名称资源**；真实 catalog 共 1,461 条记录。
时间是墙钟时间，分配是 `GC.GetTotalAllocatedBytes` 的累计值，**不是峰值内存**。
没有清空操作系统文件缓存，以下是开发机测量，不是冷磁盘或多轮统计结果。

| 测量项 | 重构前 | 重构后（最终隔离运行） |
|---|---:|---:|
| 资源数据库大小，含业务索引，WAL checkpoint 后 | 488.43 MiB | 53.01 MiB（减少 89.1%） |
| 顺序读回全部索引行 | 11.79 s | 4.63 s |
| 顺序读回累计分配 | 660.29 MiB | 125.51 MiB（减少 81.0%） |
| 读取关联分析所需资源 | 1.104 s | 0.128 s |
| 全部记录写入新库（含源读取） | 34.19 s | 19.11 s |
| 写入累计分配（含源读取） | 1,956.89 MiB | 1,510.10 MiB |
| catalog 加载、解析与结果序列化 | 18.04 s | 1.14 s |

初次新实现测得全量读取 3.56 s、写入 15.32 s、catalog 0.44 s；上表采用后续
仅运行基准类的结果，避免挑选最低耗时。运行期间的机器负载会影响数值。

真实 bundle 中 64 个对象，通过独立 AssemblyLoadContext 加载**重构前发布程序集**，
与新共享会话进行同进程对照（旧程序集是 Release，新实现测试为 Debug）：

| 测量项 | 旧发布程序集 | 新共享会话 |
|---|---:|---:|
| 读取与校验耗时 | 232.6 ms | 85.8 ms |
| 累计分配 | 3.78 MiB | 2.94 MiB |

旧实现的 AssetsManager 已有内部缓存，因此旧 API 调用 64 次不等于解包 64 次。
早期独立会话对照结果不作为旧版性能。新会话的意义是显式复用 SerializedFile、减少
逐对象调用开销，并在切换 bundle 时释放上一组，而不是让所有 bundle 驻留到导出结束。
新会话加载计数为 1。它在旧版读取后运行，文件缓存已热；本项衡量**导出的对象读回阶段**，
不能据此宣称整个导出加速同样倍数。完整导出仍有纹理处理、重打包、引用验证和 XZ 压缩。
数据库缩小减少了扫描字节量，会话减少了解包次数；没有用系统跟踪工具统计实际磁盘 I/O 次数。

## 正确性证据

最终全量测试扫描 **1,473 个条目 / 16.05 GB 原始 bundle 文件**，解析 1,471 个，
得到 1,275,623 条资源。冷扫描 184.43 s，完整引用资产回灌 11.61 s，索引命中的
Unity 热扫描 0.846 s。两个不能解析的条目仍报告原有错误（`capacity` 与扩展类型树 `tthm`），
不写成成功缓存。与本轮 CRC/共用解包改造前的冷建库相比，全部 bundle 和资产行的摘要一致，
包括 baseline 和静态标记；该中间版本冷扫描为 474.46 s，不能把它当作项目原版的严格冷启动基准。

上述完整测试进程还执行了全库 JSON 序列化摘要验证，其最终进程峰值工作集记录为
10,489 MiB；之前的全量测试为 2,830 MiB，两次测量不能证明全流程峰值内存下降。
本次明确验证的内存收益是索引读回累计分配下降，以及不再同时保留整库中间行。
完整流程峰值与资产常驻集合仍需单独做阶段性内存分析，不能用累计分配替代峰值。

- 测试夹具把旧库副本逐 bundle 写入新库，并对全部元数据和 **127.6 万行**逐行比较，完全一致。
  旧库读取器只在测试里使用，生产代码没有迁移兼容路径。
- 重构前后完整 catalog 结果 JSON 的 SHA256 相同：
  `5D94B1D0717A1E4FCD41D0A84C2C019ACC891E214D97629963F863FE126D9DA0`。
- 64 个真实对象的原始字节与类型表索引逐项一致，共享会话加载计数为 1。
- 数据库回归覆盖空 bundle、中文、空值、64 位 ID/大小、共享字符串、大小写路径、
  失败回滚、取消回滚、并发快照、旧库重建和缺表失效。
- 真实“一键扫描 → 实体化 → PNG 替换 → Carra2 导出 → 重新导入验证”已通过。

最终 solution 构建零警告、零错误；普通回归 **746 项全通过（Domain 672 + Format 74）**。
Release / win-x64 发布已成功，产物位于 `artifacts/performance/publish-win-x64/`，
包含新增的 `System.IO.Hashing.dll`；原发布目录与其缓存未覆盖。
额外显式启用了全量扫描与完整启动门控测试，不把默认门控提前返回计作真实数据验证。

真实完整启动使用隔离的应用目录、现有项目及五库副本：回灌 1,275,228 条引用资源，
项目合计 1,275,623 条。该次回灌 **38.0 s**，后续六步扫描 **3.8 s**；音频 1,531 bank、
静态 1,392 表、文本 2,048 JSON、关联 1,313 对象 / 62,970 链接全部通过，零失败/跳过。
这说明回灌仍然是完整启动的大头；不能把单独 Unity 热扫描的 0.846 s 当作整个应用启动时间。
原始报告为 `artifacts/performance/startup.trx`。

## 复现

基准入口：`tests/LimbusModEditor.Domain.Tests/DatabasePerformanceTests.cs`。
先构建项目，再在 PowerShell 中设置：

```powershell
$env:LME_PERF_INDEX = '绝对路径\资源索引副本.db'
$env:LME_PERF_OUTPUT = '绝对路径\新的空输出目录'
dotnet test tests/LimbusModEditor.Domain.Tests --no-restore --filter FullyQualifiedName~DatabasePerformanceTests --logger 'console;verbosity=detailed'
```

输入为旧库时额外设置 `LME_PERF_LEGACY=1`；测试会在输出目录生成 v2 夹具并逐行验证，
转换不计入性能测量，原输入以只读连接打开。输出目录必须每次使用新目录。
已是 v2 的输入不要设置该变量。默认源位置来自本机用户目录与 Steam 游戏目录，
可用 `LME_UNITY_CACHE_DIR` / `LME_GAME_DIR` 覆盖。

全量冷扫描、索引回灌和热扫描另用 `LME_PERF_FULL_SCAN=1`，并筛选
`FullyQualifiedName~Real_cold_scan_rehydrate_and_hot_scan`，输出独立 `cold-index.db` 与
`full-scan.json`。不会清理或覆盖游戏资源。

设置 `LME_PERF_COMPARE_INDEX` 为前一次 v2 冷建库可核对全库事实。导出读取基准可设置
`LME_PERF_BASELINE_UNITY` 为重构前 `LimbusModEditor.Formats.Unity.dll` 的绝对路径，
测试会加载该程序集直接对照；未指定时只是当前单对象 API 与共享会话的对照。

本次本地证据位于 `artifacts/performance/`：`before/metrics.json`、`final/metrics.json`、
`export-original/export-read.json`、前后 catalog JSON 和测试结果；该目录由 git 忽略。

## 全量常驻改为查询化的可行性实测（2026-09-14）

上节遗留的「UI 仍把全部引用资产物化为 `AssetRecord`」这一项，做了一轮**只读**实测，
只量代价、没有改 `src/`。测量对象是同一份真实索引库副本（`assets` 1,275,623 行 /
`bundles` 1,471 / `strings` 1,462 / 52.8 MiB），全部改动都做在 `%TEMP%` 的副本上。

### 前提：缓存引用资产的字段本来就在库里

`UnityCacheScanService.BuildRecord` 只是把索引列**拼装**成 `AssetRecord`：
`LogicalPath` = `outer_key/inner_key/container/pathId.typeId`，`Account`/`Bundle`/`SourcePath`/
`ContainerPath`/`UnityPathId`/`UnityTypeId`/`Size` 全是列，`Metadata` 里的 `bundleIndex`、
`cacheOuter`、`cacheInner`、`reference`、`catalogBaseline`、`containerEntry`、`staticBundle`
也全部来自列。**因此查询化不需要新增任何对象数据**，只需要派生列与索引。

`LogicalPath` 实测在全库唯一（1,275,623 行 = 1,275,623 个 DISTINCT），可以当稳定身份键
与分页全序的尾键；它与 `EditOperation.targetPath` 同形。

### 排序键不能编码下推 SQL —— 这是本轮最重要的结论

`AssetDisplay.ComparePaths/CompareNames` 是自定义自然序（数字段按数值比较），
SQLite 只有 BINARY/NOCASE。把自然序编码成「数字段长度前缀 + 字符」的键后，
BINARY 序看起来等价，但**在真实数据上失效**：

| 方案 | 随机 20 万对符号不一致 | 磁盘增量 | 页定位（第 5,000 页 / 末页） |
|---|---:|---:|---:|
| 编码排序键 + 覆盖索引 | **87,821 对 / 43.9%** | +494.4 MiB | 497 / 699 ms |
| 编码排序键（非覆盖索引） | 同上 | +787.3 MiB | 9,859 / 12,186 ms |
| **物化稠密名次（rank）** | **0（按构造）** | **+140.6 MiB** | **0.3 / 0.3 ms** |

原因是行分布：**96% 的行显示名形如 `未命名资源/未命名 #<pathId>`**（`container_entry`
非空只有 51,376 行），而 `pathId` 可以为负、也可以是 19 位长整数。参考比较器在
「数字段 vs `-`」处按**原始码点**比较（`-` = 0x2D < `0` = 0x30），任何「给数字段加前缀」
的编码都会把数字排在前面 → 顺序反转。同类字符还有 `空格 . , +` 等所有码点小于
`0x30` 的字符。抽样还给出 `"a0b"` 与 `"ab"` 在参考比较器下**相等**（零数字段被
`TrimStart('0')` 归零），编码方案无法表达。

⇒ 安全做法是**把顺序本身物化**：用现有 C# 比较器全量排序，写回一个稠密整数名次列
`r`（0…N-1），查询期 `ORDER BY r` 走整数索引。零语义偏差、索引键 8 字节、
**不需要注册自定义 collation**（那会让建索引付出 O(N log N) 次 interop 回调）。

### 分页：不需要 OFFSET，也不需要 keyset

名次是稠密的，所以第 k 页就是一次区间定位：

```sql
SELECT ... WHERE r >= k*200 AND r < k*200 + 200 ORDER BY r
```

| 页 | 名次区间 | 同一索引上的 OFFSET |
|---|---:|---:|
| 第 1 页 | 0.2 ms | 0.7 ms |
| 第 1,250 页 | 0.2 ms | 26.6 ms |
| 第 5,000 页 | 0.3 ms | 171.4 ms |
| 末页 | 0.3 ms | — |
| keyset 续页 | 0.7 ms | — |

**页定位耗时与页深无关**，因此「第 N / M 页 + 跳页」的 UI 形态是安全的，
不需要退回「只能上一页/下一页」。分段扫描的代价才是关键：`OFFSET` 在**非覆盖**索引上
是 O(offset) 次回表随机读（9,859 ms @1,000,000），在**覆盖**索引上是纯索引游走（497 ms）。

### 代价与边界

- **回填成本**：`ALTER TABLE ADD COLUMN` 是纯增量（实测 0.00 s，**不 drop、不重扫**）；
  回填两列 1,275,623 行 26.5 s（48,080 行/秒）。**绝不能改 `SchemaVersion`** —— 那会
  `DROP TABLE` 并迫使全量重扫。
- **排序成本**：Python 复刻全量排序 271.5 s，是 C# 的上界；C# 同算法预计 2–6 s，
  且发生在扫描期（本就在内存里排一次），不在查询路径上。
- **子串搜索是唯一比内存方案慢的地方**：`LIKE '%x%'` 全表扫描 220–1,250 ms，
  且**提高 `cache_size` 几乎无效**（8 MB → 256 MB 只从 1,249 ms 变到 1,209 ms，
  开销在逐行比较而非缺页）。若要更快只能上 FTS5 trigram（会增加大量索引体积，本轮未测）。
  可接受的替代是防抖 + 后台查询 + 明确的忙碌反馈。
- **不能下推到 SQL 的部分**：① `HasUsableReplacement` 是 `File.Exists`（文件系统事实，
  数量受限于替换编辑数）；② 任何内容解码（预览、哈希、Spine、静态表）本来就要按需读盘；
  ③ `EditState`（含 `Conflict`/`Invalid`）是项目态而非目录态。
- **覆盖规则**：实体化后的资产在索引库里**仍有行**，查询必须让项目态优先 ——
  这正好等于 `RehydrateFromIndexAsync` 现有的 `byPath.TryAdd`（既有记录优先）语义，
  落到 SQL 是「按 `LogicalPath` 左连接项目态临时表」。实测真实项目态很小
  （`MyMod.lmeproj`：`assets[]` 395 条全部 materialized / `edits[]` 1 条），
  每次查询重建这张临时表也不值一提。
- **铁律影响（必须显式承认）**：索引库从「只影响速度」变成「列表的必需投影」。
  删除它仍只是重扫、不影响正确性，但 UI 必须给出明确的「索引缺失 → 需重新扫描」状态，
  不能再让它静默退化成空列表。

### 复现

`logs/probe_sql_only.py`（字段完备性 + 唯一性 + 首个编码方案）、
`logs/probe_sql_only2.py`（精简编码方案 + cache_size 灵敏度 + 保序性反例）、
`logs/probe_sql_only3.py`（名次方案 + 页定位）。三个脚本都只读打开原库，
只在 `%TEMP%\lme-sqlprobe*` 的副本上做结构改动，输出在同名 `.out.txt`。

## 派生层定案：名次表 + FTS5 trigram（S2 实测，2026-09-14）

上一节的结论是「物化稠密名次 `r`，查询期 `ORDER BY r`」。S2 把它落成了**纯增量扩容**：
只 `CREATE ... IF NOT EXISTS`，**不动 `assets` 表、不动 `SchemaVersion`** —— 老用户的缓存库
不需要重扫那 1,471 个 bundle。上节写的「若要更快只能上 FTS5 trigram（本轮未测）」到这里补测并定案。

### 最终结构

```sql
CREATE TABLE catalog_rank (r INTEGER PRIMARY KEY, bundle_id, bundle_index) WITHOUT ROWID;
CREATE VIRTUAL TABLE asset_fts USING fts5(dp, lp, content='', detail=none, columnsize=0, tokenize='trigram');
CREATE TABLE derived_state (k TEXT PRIMARY KEY, v TEXT NOT NULL);
-- revision='d2' + rows + src_prefix + src_suffix
```

- **名次不写成 `assets.r` 列**：`UPDATE assets SET r=?` 实测 43–50 µs/行 → 55–65 s（127 万行）；
  而 `catalog_rank` 用多行 `VALUES` 批量插入 4.7–9.5 µs/行 → **5.9 s**。顺带的好处是 `assets`
  的 schema 完全不动，迁移只剩 `CREATE`，零 DROP 风险。
- **FTS 的 `rowid` 直接用名次**，所以检索命中的 rowid 就是名次，排序/分页零转换。
  代价是名次属于**整表全序**，任何一行增删都会挪动它后面的名次 ⇒ `catalog_rank` 与 `asset_fts`
  **必须在同一个事务里一起重建**，否则「搜到的行」会指向「另一条资源」。
- **FTS 索引 `dp` + `lp` 两列**（`d2` 起）。口径来源是旧 `AssetSearchService.MatchesText` 的匹配面
  `DisplayPath ∪ LogicalPath ∪ SourcePath`：只索引 `dp` 会让「按 LogicalPath 搜」**从能搜到变成搜不到**
  —— 这是行为回归，不是优化。代价也实测了（`logs/probe_fts_dp_lp.py`，真实规模副本）：

  | FTS 形态 | 净增 | 灌入 |
  |---|---|---|
  | `fts5(dp)` | +49.7 MiB | 10.7 s |
  | `fts5(dp, lp)` | **+180.8 MiB** | **45.0 s** |
  | `lp` 的边际 | **+131.1 MiB** | **+34.2 s** |

  `lp` 形如 `<32位16进制>/<32位16进制>/CAB-<32位16进制>/<pathId>.<typeId>`，除编号外全是机器哈希
  —— 没人会手打，但它与 `src` 的可变部分同源，所以不索引它就等于丢掉两个字段的搜索能力。
  （早先测的 `dp+lp` @ `detail=full` 是 +855.1 MiB / 161.4 s；`detail=none` 的 `lp` 边际只有它的 1/6.5。）
- **`src`（SourcePath）不单列索引，但等价性没丢**：实测 `data_path` 全部 1,471 条同形
  `<缓存根>\<outer>\<inner>\__data`，**可变内容只有 outer/inner —— 而 `lp` 已经含这两段**。
  剩下只有「每一行都相同」的两段常量：公共前缀（`C:\…\ProjectMoon_LimbusCompany\`）与
  公共后缀（`\__data`）。于是规则是：needle 含于公共前后缀 ⇒ 命中**全部行**（旧实现在这种情况下
  同样没有任何过滤作用）；含反斜杠（跨 outer/inner 或跨进 `\__data`）⇒ 退化为全表扫描；其余情形
  `FTS(dp∪lp)` 已是 `src` 匹配面的超集。两个前后缀在建派生层时算好、存进 `derived_state`。
- `content=''`（contentless）**采用**，但有三条必须照着写的约束（都是实测踩出来的）：
  ① **清空只能用专用指令**：`DELETE FROM asset_fts`（带不带 `WHERE` 都一样）直接报
  `asset_fts: table does not support scanning`；正确写法是
  `INSERT INTO asset_fts(asset_fts) VALUES('delete-all')`（可重复调用，之后重插与检索都正常，
  `integrity-check` 通过 —— `logs/probe_fts_clear.py`）。
  ② **不能从索引里把文本读回来**（同样是 `table does not support scanning`）。这一条**我们不需要**：
  复核是拿命中的 `rowid`（= 名次）回到 `catalog_rank`/`assets`/`bundles`/`strings` 的列上**重新算**
  三种文本（`ReadReverifyKeysByRank`），从不读 FTS 表的原文 —— 这也是 contentless 能用的前提。
  ③ **不能 `rebuild`**（contentless 没有可重建的源）。
  省下来的体积很实在：contentless `dp` 净 **+42.3 MiB / 灌 15.6 s**，vs 存储原文的 `detail=none,columnsize=0`
  **+113.8 MiB / 灌 21.8 s**。代价是「搜到的不等于索引里的原文」这件事完全由复核兜住。

### 检索：候选是超集，必须复核

`detail=none` 不存位置 ⇒ **不支持裸短语查询**（直接报 `phrase queries are not supported (detail!=full)`）。
唯一可用写法是把查询串切成 3 字符窗口、**每窗加双引号**、再用 `AND` 连起来：

```text
needle = anim/icon2  →  "ani" AND "nim" AND "im/" AND "m/i" AND "/ic" AND "ico" AND "con" AND "on2"
```

这样得到**只是超集**：含全部词项 ≠ 含连续子串（例：`assets/icon/on2/x.png` 同时含
`ico`/`con`/`on2`，却没有 `icon2`）。所以必须把候选行读回来算文本、用 `OrdinalIgnoreCase`
判子串做**精确复核**（`ReadReverifyKeysByRank` + `MatchesReverifyKeys`）。

复核的口径 = 旧 `AssetSearchService.MatchesText` 的匹配面，**三个字段**：
`dp ∪ lp ∪ src`。拼接一律走 `AssetDisplay.CacheRowDisplayPath` / `CacheRowLogicalPath` ——
与列表显示、派生层回填、兜底全表扫描**是同一份实现**，所以「搜到的」严格等于「看到的」，
且不会出现「索引按一种拼法命中、复核按另一种拼法否掉」的假阴性。

- 实测 trigram **跨空白建词项**（`"本 #"` 是合法词项），逐窗口加引号是必须的：
  裸写的多词查询会被当成短语，而 `detail=none` 直接报错。
- 查询串 **短于 3 字符**时索引里取不到任何词项 ⇒ 退化为流式全表扫描（实测 1.5–3 s，不物化记录）。
  **含反斜杠**的查询串同理：它可能只落在 `src` 的分隔符上（跨 outer/inner 或跨进 `\__data`），
  而 `src` 不在索引里 —— 也走同一条兜底扫描。两条路径共用同一个复核函数，所以结果一致。
- 复核用的 `Contains(..., OrdinalIgnoreCase)` 是**字面量**匹配，没有 `LIKE` 的 `_`/`%` 通配语义。
  实测拿 `LIKE` 当判据会**多算**：`'%cg_40%'` 命中 35 条，而真正的子串命中是 33 条
  （SQLite 的 `_` 是单字符通配符）⇒ 复核口径必须用 `Contains`，不能用 `LIKE`。
- 实测「FTS 候选 → 复核后」：`Personality` 1,638 候选（8.6 ms）→ 1,638 命中（20.1 ms）；
  `cg_40` 33 → 33。

### 实测（真实规模副本）

| 项 | 值 |
|---|---|
| 索引库体积 | 46.5 → **138.7 MiB**（`d1`，`assets` 55.22 / `catalog_rank` 19.19 / `asset_fts_data` 39.79）<br>`d2` 的 FTS 再 +131.1 MiB ⇒ **≈ 270 MiB** |
| 单页读取 | **1.35 / 1.58 / 1.68 ms**（首页 / 中段 / 末页 —— 与页深无关） |
| 名次重建 | **5.9 s**（DELETE + 重插） |
| 检索索引重建 | **27.8 s**（`d1`）→ **≈ 62 s**（`d2`，按 `lp` 边际 +34.2 s 估） |
| 每次缓存变化合计 | **≈ 34 s**（`d1`）/ **≈ 68 s**（`d2`），且只在缓存真的变过之后发生一次 |
| 排序打平 | 显示路径并列的行数（靠 `LogicalPath` 才分先后） |

> `d2` 的两项是 `logs/probe_fts_dp_lp.py` 在同一份真实副本上用**生产不用的**
> `journal_mode=OFF / synchronous=OFF` 量的，所以是**下界**：生产走 WAL + `synchronous=NORMAL`，
> 真实墙钟要更高。相对代价（`lp` 让检索引擎的灌入时间翻 4 倍以上、体积翻 2.6 倍）是准的。
> 程序日志里已有 `派生层重建完成：… 用时 X 秒` 一行，以它为准。


### 维护策略

- `PersistAll` 只**失效**：在同一事务里 `DELETE FROM derived_state`（回滚也保持一致）。
- `EnsureDerived` **按需重建**：`revision` 或 `rows` 不符才动手，否则零成本返回 ——
  所以每次扫描后都能放心调一次。
- **查询路径另有廉价自愈闸门**：查询前只读一行状态，不对就补建。没有它会出现**静默错答**
  ——「索引写完就被杀掉」时 `ReadPage` 返回空页、`SearchRanks` 返回零命中，用户看到的是
  「资源没了」而不是「还在建」。这条闸门也让「索引库是列表的必需投影」这条铁律有兜底：
  删库仍然只影响速度。

### 复现

`logs/probe_final_shape.py`（定案尺寸与耗时）、`logs/probe_fts_dp_lp.py`（`lp` 的边际体积/时间，
同一副本两变体 + `page_count/freelist_count` 口径）、`logs/probe_rank_table.py`（名次表 vs 逐条 UPDATE）、
`logs/probe_fts_maint.py`（contentless 的删除/重建限制）、`logs/probe_fts_clear.py`（`delete-all` 写法）、
`logs/probe_shapes.py`（`lp`/`dp` 实形）、
`logs/probe_sql_only7.py`（用 `page_count/freelist_count` 量真实体积）、
`logs/probe_trigram_space.py`（跨空白词项）、`logs/probe_trigram_sem.py`（`LIKE` 判据不可靠）、
`logs/probe_bulk.py`（多行 VALUES 对 FTS5 虚表同样可用）。

## S3a：查询门面 `AssetCatalog`（2026-09-15）

派生层（名次 + 检索）本身不会让界面变快 —— 得有人去用它。`AssetCatalog`
（`Application/Catalog/AssetCatalog.cs`，**无 WPF 依赖**）就是那个门面：
`Count` / `Page` / `Locate` / `IndexIn` / `Resolve`。本步**纯新增**，调用点尚未迁移，
所以先保证「与旧 `AssetSearchService.Search` 逐行等价」，再在 S3b/S3c 换列表与集合。

### 一条页面的产生过程

1. **下推候选**：`UnityCacheSqliteIndexStore.ReadCandidateRanks(UnityCacheIndexFilter)`。
   能在 SQL 里判的条件全给 SQLite —— 类型、`path_id`、`type_id`、大小区间、容器子串、
   有没有容器条目，以及**文本**（复用派生层的 `asset_fts`，写成
   `k.r IN (SELECT rowid FROM asset_fts WHERE asset_fts MATCH $q)`，不需要把名次列表塞进 IN）。
2. **复核判据**：逐候选构造记录，走**唯一那份**判据 `AssetSearchService.Matches`。
   库里没有的事实只有三类 —— 编辑状态、替换文件是否存在、静态判定 —— 它们只在这里生效。
3. **只留名次**：命中集里**不保留 `AssetRecord`**，只留名次（+ 非按名称排序时的排序键）。
   留记录就等于把「1,028 字节/条的全量常驻」原样搬回来。
4. **物化一页**：只有最终那一页的 `take` 条才真正构造成 `AssetRecord`。

### 三个「唯一出处」（这是本门面存在的全部意义）

| 口径 | 唯一出处 | 说明 |
| --- | --- | --- |
| 判据 | `AssetSearchService.Matches` | 分页列表、旧全量搜索、导出/构建必须同结论 |
| 排序 | `AssetSearchService.BuildSortKey` / `CompareSortKeys` | `SortByKeys` 也改成走它，不再各写一份 `OrderBy` 链 |
| 重建记录 | `UnityCacheScanService.BuildReferenceRecord` | 与扫描写入项目时**同一个函数**（`BuildRecord`） |

### 「按名称」不需要排序

`catalog_rank.r` 本身就是目录全序（`ComparePaths` 再 `LogicalPath` 兜底），而 `LogicalPath`
全库唯一 ⇒ 这是**严格全序、没有并列**。所以按名称这条路径直接按名次升序取候选，
既不排序、也不算排序键（省掉每条一次的显示路径分配）。另外四种排序仍要排，
但只对**下推后的候选集**排（默认视图 5.1 万行），不是全表。

### 两个必须记住的定位口径

- `Locate(logicalPath)` 给的是**全局名次**。只有「无任何筛选」的视图里它才等于页下标 ——
  默认视图隐藏静态数据表（`ContainerOnlyFilter` 为勾选 + `ShowStaticTables=false`），
  两个数就不是一个。
- 要「翻到某一页并选中」用 `IndexIn(query, logicalPath)`，它给的是**命中集内的下标**。

### 正确性证据

`tests/LimbusModEditor.Domain.Tests/AssetCatalogTests.cs`：数据集 4 bundle × 6 行，维度刻意铺满
（两种静态判据各占一个 bundle、一行落在 static-data 路径前缀、类型覆盖 class-id 映射 /
ref-type 伪 id / 映射不到保留类型树结论、**不同 bundle 显示路径故意重名**以压住打平兜底），
然后 34 种筛选 × 5 种排序 = 170 条查询，逐条断言

- `Page(query, 0, ∞)` 与 `AssetSearchService.Search(project, query)` **逐行逐字段**相等
  （含 `AssetId`）；以及
- 页大小取 1 / 5 / 7 / 23 / 24 / 25 时分页拼接等于整份结果。

另有两组：项目态参与（编辑状态、`replacementPath`、已实体化摘 `reference`）与逐行等价同样成立；
以及「项目态来源为空时除 `AssetId` 外逐字段与回灌记录一致」——用来锁住
`BuildReferenceRecord` ≡ `RebuildRecords`。

### 下推的类型判据为什么是完整的（不是近似）

一行索引的目录类型是 `Map(type_id, 索引里的 type)`。它等于目标类型当且仅当二者之一成立：
① `Map(type_id)` 就是目标 ⇒ `type_id ∈ ClassIdsOf(目标)`；② `Map(type_id) == Unknown`
且索引里的 `type` 就是目标。所以 SQL 的 `assets.type = $t OR assets.type_id IN (…)`
是**完整覆盖**。`UnityClassId.ClassIdsOf` 的反向表由正向表生成，并由测试双向对账
（正→反、反→正）。少数类型（`Json`、`ScriptableObject`）没有对应 class id，反向表为空是
**正确**的 —— SQL 退化成 `assets.type = $t`，而那一列正是它们唯一的出处。

## S3a 修正：让「下推」真的下推（2026-09-15）

S3a 只证明了**等价性**，没在真实规模上量过。用 `logs/run_catalog_probe.py` 在真实库副本
（1,275,623 行）上一量，默认视图**每页 5.67 s** —— 比要替换掉的旧路径还慢。
那是「拿内存换时间」的坏交易，所以先把钱花在哪拆开：

| 环节（默认视图：4.86 万候选 → 1 页 200 条） | 修前 | 修后 |
|---|---|---|
| ① `ReadCandidateRanks`（候选集 SQL） | **~4.8 s** | **0.76 s** |
| ② `StreamByRanks` 读回 51,376 行 | 0.835 s | 0.59 s |
| ③ 每行造 `AssetRecord`（`BuildReferenceRecord`） | +0.46 s | +0.45 s |
| ④ 判据 `AssetSearchService.Matches` | 0.108 s | 0.067 s |
| ⑤ `BuildSortKey`（仅非「按名称」排序） | 0.050 s | 0.056 s |
| **⑦ `Page(默认视图, 0, 200)` 整条链路** | **5,671 ms** | **1,374 ms** |

分配量也说明了同一件事：修前每次取页分配 **152 MiB**（= 5 万条记录的 2.9 KiB/条），
而这 5 万条里有 48,575 条只是用来「筛掉 2,801 条静态数据」的。

### 根因一：候选集 SQL 每次全扫 127 万行（4.8 s → 0.17 s）

`EXPLAIN QUERY PLAN` 给出的计划是：

```
SCAN k                                   -- 全扫 catalog_rank
SEARCH a USING PRIMARY KEY (…)           -- 逐行回表 assets
SEARCH b USING INTEGER PRIMARY KEY (…)   -- 逐行回表 bundles（条件里根本没用它）
SEARCH s USING INTEGER PRIMARY KEY (…)   -- 逐行回表 strings
```

127 万行 × 3 次 B 树查找 = 4.8 s，**筛选条件一点没起作用**。两个原因：

1. **`ORDER BY k.r` 把 planner 钉死在「扫名次表主键」上**。排序需求一写，全扫就成了它
   眼里最省的计划。去掉之后同一个条件立刻变成两侧 COVERING INDEX。
2. **`catalog_rank` 上没有 `(bundle_id, bundle_index)` 的索引**，所以「assets 驱动 →
   反查明次」这条路根本不可用 —— 光在 `assets` 上建部分索引 `ix_assets_named` 是不够的，
   反查那一步无索引可用，planner 只能放弃。

修法：`CREATE INDEX IF NOT EXISTS ix_catalog_rank_bundle ON catalog_rank(bundle_id, bundle_index)`
（+16.8 MiB），并且候选集 SQL **不再写 `ORDER BY`**（名次是 int，几万条在内存里排只要几毫秒），
JOIN 也按需最小化 —— 无条件时连 `assets` 都不碰（`SELECT r FROM catalog_rank` 出 127 万行 39 ms，
而原来的写法 41.8 s），`bundles` 从前 join 进来却从未在条件里用过。

| 写法 | 计划 | 耗时 |
|---|---|---|
| 现状 | `SCAN k` | **3,866 ms** |
| 加反向索引，但仍 `ORDER BY k.r` | `SCAN k`（planner 不为所动） | 3,448 ms |
| 加反向索引 + 去掉 `ORDER BY` | a、k 双向 COVERING INDEX | **168 ms** |
| 同上 + `INDEXED BY` 提示 | 同上 + 临时 B 树 | 157 ms |
| 把筛选列搬进 `catalog_rank` 宽表 | 窄表全扫 | 249 ms（+22 MiB，需整表重建） |

最后一行是**没有采用**的方案：它更快不了，却要多维护一份冗余事实、每次改列都得重建 127 万行。

### 根因二：`Locate` / `IndexIn` 对负 `path_id` 静默失效

`TryParseLogicalPath` 用 `NumberStyles.None` 解析尾段 `<pathId>.<typeId>`。`path_id` 是
Unity 的对象 id（带符号 64 位哈希），**真实缓存里有大量负值**，而 `NumberStyles.None`
拒绝前导负号 ⇒ `FindRank` 对它们永远返回 -1。

症状极隐蔽：列表里明明看得到那条资源，「定位到它 / 精确跳转」无声无息地不生效，日志里
只有一句「不在本代际的结果中」。**是探针抓到的** —— 对页码 0 的第一条资源调 `Locate`
直接拿到 `null`，而它的 `path_id` 正是 `-4060527305021521791`。

修法：`NumberStyles.AllowLeadingSign`（仍然拒绝空白 / 千位分隔 / 十六进制 —— 那些真的不像
索引路径）。测试里把负值直接写进 `AssetCatalogTests` 的模板，让 170 条等价性查询与定位
测试一起压住它。

### 升级路径

反查索引由建表脚本 `CREATE INDEX IF NOT EXISTS` 保证，**不抬高 `DerivedSchemaRevision`**：
老库打开时补一个索引即可，实测 `derived_state` 仍是 `d2` ⇒ 既不重建派生层、更不重扫 1471 个
bundle。派生层重建时则先 `DROP INDEX` 再整批建（逐行维护 127 万行的索引远比整批建贵，
整批建实测 1.1 s）。

### 剩下的开销（下一步）

修完仍要 **1.37 s/页**，瓶颈已经换到「读回 5 万行 + 为此造 5 万条记录」上。
两条都不便宜且都必要——因为判据 `Matches` 现在只接受 `AssetRecord`：

- ② 读 51,376 行 ≈ 0.6 s（3 个 JOIN + 4 个字符串列的材料化，约 12 µs/行）；
- ③ 造 51,376 条记录 ≈ 0.45 s（2.9 KiB/条，其中 `Metadata` 字典与逐行的
  `Path.GetDirectoryName` 是大头）。

方向是**把判据下沉到行级**：候选只走一个轻投影（判据真正需要的列），命中才造记录，
且只为最终那一页造。按 ④ 的单价（0.067 s / 5 万条）估，这条路把 1.37 s 压到 **≈ 0.3 s**。
在此之前，页间复用同一查询的命中集（只留名次）就能让**翻页**本身变成 O(一页)。

### 复现

`logs/run_catalog_probe.py`（真实库副本上一次跑完全部场景，先把库暂存到临时目录，
`LME_STAGE=1` 强制重新暂存）、`logs/diag_catalog_sql.py`、`logs/diag_catalog_sql2.py`
（`EXPLAIN QUERY PLAN` + 各写法的耗时对比 + 反查索引/宽表的体积代价）。

## 仍存在的开销

UI 仍会把全部引用资产物化为 `AssetRecord`，没有改成数据库分页查询；百万级资产的常驻
集合、路径和元数据仍占较多内存。**迁移可行的形态与实测代价见上一节**：派生列 + 稠密名次，
磁盘约 +217 MiB（`d2`，含 `lp` 检索）+ 16.8 MiB（反查索引）换掉约 1,250.9 MiB 常驻堆。
**S3a 已把查询门面做出来、证明与旧搜索逐行等价，并修掉了「下推没真下推」与「负 path_id
定位失效」两个只在真实规模/真实数据上才暴露的问题**（取页 5,671 → 1,374 ms），
剩下的工作是让列表/树改走它（S3b）、再把 `ModProject.Assets` 收窄成「项目态记录」
并迁移其余用法（S3c），以及把判据下沉到行级（见上一节末）。
其他四个工作台/派生缓存没有改表布局。
XZ 压缩与 Unity 重打包仍由原库处理；本次不改变加载器格式或扩展未知 Unity 格式的写回支持。
