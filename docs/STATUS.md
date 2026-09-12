# 项目现状与待办（STATUS）

> 接手入口文档：先读本文，再读 `docs/CODE-STRUCTURE.md`（结构与不变量）与
> `docs/PROJECT-INDEX.md`（逐文件功能索引），最后按需查 `docs/USAGE.md`（用户手册）、
> `docs/REALDATA-VERIFY.md`（真实数据验证证据）、`docs/REVIEW.md`（自审与风险）、
> `docs/ARCHIVE-DESIGN-NOTES.md`（历史设计决策，仅保留仍然有约束力的部分）。
>
> 历史执行计划已删除：`docs/plans/`（17 份逐轮任务书）与 `docs/NEXT-STEPS.md`（交接计划），
> 见 §7；`docs/ROADMAP.md` 保留为实现历史长文（其中「下一步」一律以本文为准）。

---

## 1. 一句话现状

Windows-only 的 C#/.NET 8 + WPF 模组创作工作台，已能跑通
**真实游戏数据读取（1459 个缓存 bundle / 1531 个 bank / 真实 lang 目录 / catalog 基线）
→ 四个工作台编辑（资源 / 音频 / 文本 / 静态）
→ 一键导出加载器可消费的包（carra / bank / rebank / staticmod / lang bus·patch·pathset）
→ 调试铺盘（逐文件备份 + 关闭逐字节还原）**
的完整闭环；写回链路已在真实纹理上端到端验证（`docs/REALDATA-VERIFY.md`）。

---

## 2. 验证基线（每次改动前后都要跑）

```text
dotnet build LimbusModEditor.slnx --no-restore --nologo
dotnet test  LimbusModEditor.slnx --no-build --nologo
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
```

**2026-09-12 实测：611 个测试全绿（74 Format + 537 Domain），本机真实数据门控测试全部真跑。**

测试基线的历史轨迹（参考）：168 → 205 → 218 → 223 → 228 → 231 → 256 → 274 → 334 → 476 → 528 → 549 → 562 → 573 → 610 → **611**。

注意（见 `docs/PROJECT-INDEX.md` §11）：

- 真实数据门控失败是**静默 `return`**，报告里的「通过」可能等于「没测」；
  需要显式设置 `LME_BENCH` / `LME_FULL_SCAN_SMOKE` / `LME_STARTUP_SCAN_SMOKE` / `LME_REALDATA_INSTALL` 才能跑重活。
- 少数「本机必须有真实样本」的硬断言在无样本机器上会**失败**（`StaticModServiceTests` 两条、`RealBankTests` 三条）——刻意如此。
- `RealBankIndexSmokeTests` 的墙钟预算断言（读索引 < 1.5s）在两工程并行时偶发超时，单独跑通过，属既有 flake。

---

## 3. 项目铁律（不可违反）

1. **只扩展 AssetsTools.NET 适配层**（`Formats.Unity/AssetsToolsBackend.cs`），不手写完整 Unity Bundle/SerializedFile 解析器。
2. **不猜测**：未知负载 / 未知压缩 / 未知字段一律 fail fast 并给中文错误；没有真实样本验证不宣称格式兼容。
3. **不写用户游戏数据**：游戏目录、Unity 缓存、catalog 一律只读；唯一例外是用户显式触发的调试应用
   （必须有备份 + 还原清单 + 冲突不覆盖 + 游戏运行中拒绝执行）。
4. **FMOD / FSBank DLL 随包分发**（`third_party/fmod/` 用户合法放置，git 忽略；发布脚本复制进输出 `fmod/`）。
   运行库只绑定**公开 C ABI**，不伪造、不逆向、不分发专有二进制源码。
5. **真实加载器 = LCTA**（`E:\desktop\work\LCTA-Limbus-company-transfer-auto\launcher`，GPL-3.0，基于 LimbusModLoader v1.8）：
   只可读作**事实来源**（字段布局 / 路径 / 常量可引用），**不得整段复制代码**。
6. **仓库文本文件只用 read/edit/write 工具修改**（历史上用 pwsh 重写文件发生过两次中文乱码事故）；
   pwsh 只用于 build / test / git / publish。
7. **UI 文案用中文**；提交信息用中文；每个里程碑独立提交；提交前 build + test 必须全绿。
8. **格式边界**：Unity 走 AssetsTools.NET 适配层、图像走 ImageSharp、XZ 走 Joveler（解码用 SharpCompress）、
   FMOD 只绑公开 C ABI。
9. **设计色只允许出现在** `App/Themes/Theme.xaml` 与 `App/Themes/WorkbenchStyles.xaml`；
   `WorkbenchPages/` 下硬编码色值必须为 0（既有验收门）。
10. **新增逻辑要可测**：`App` 项目没有测试工程，值得测的逻辑必须下沉到 `Application`。
11. **文档索引与代码同步**：新增/删除源文件、改变职责边界或口径时，同步更新
    `docs/PROJECT-INDEX.md`（逐文件索引）与 `docs/CODE-STRUCTURE.md`（不变量表/流程）；
    行数等派生数据也用 read/edit/write 工具改，不要用脚本批量重写文档（见 §6.3 事故记录）。

---

## 4. 真实环境事实（本机已实测，直接引用）

| 项 | 值 |
|---|---|
| 游戏目录 | `C:\Program Files (x86)\Steam\steamapps\common\Limbus Company` |
| Unity 缓存根 | `%LocalAppData%Low\Unity\ProjectMoon_LimbusCompany`（1459 个 `<outer>/<inner>/__data`） |
| Bank 目录 | `<游戏>/LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop`（1531 个 .bank） |
| lang 目录 | `<游戏>/LimbusCompany_Data/lang`；**活动语言目录由 `lang/config.json` 的 `lang` 字段决定**（本机为汉化组目录 `LLc-CN-LCTA`，代码一律跟随、不得写死） |
| 静态 bundle | `static_s1_0_assets_all_<32hex>.bundle`，经 catalog 动态解析（外层键 `64bd0105…` 每次解析，不硬编码） |
| catalog | `<游戏>/LimbusCompany_Data/StreamingAssets/aa/catalog.bin`（本机无 `catalog_S1.bin`）；记录区 CRC/size 字段随格式版本整体平移（+0x44/+0x48 ↔ +0x3C/+0x40，解析器双布局自校准） |
| 模组目录 | `%APPDATA%\LimbusCompanyMods`（真实加载器约定；`_disable` 后缀禁用） |
| 游戏 Unity 版本 | 6000.3.12f1；SerializedFile 版本串被抹为 `0.0.0`；类型树全部内嵌 |
| 事件 bank | `<id>.bank`：SNDH 合法 size 0（无 FSB），尾部 STDT/STBL/HASH/DEL 零块 |
| 音频 bank | `<id>.assets.bank`：SNDH 表 (offset,size) 直指 FSB5（实测 codec 16 = Vorbis） |
| Carra2 键 | `<缓存外层键(32hex)>/<bundleHash>/<pathId>.<类型表索引>`，逐条目 XZ，含 `carra.json` 标记 |
| 纹理格式 | 实测 RGBA32(4) / DXT5(12) / DXT1(10) / RGB24(3) / R8(29)；存在 `m_StreamData`（`.resS` 容器内流），内联数组为空 |

**实测性能**（真实规模）：

| 路径 | 数值 |
|---|---|
| 项目保存 / 加载 | 12.4s / 1595MB → **0.4s / 1KB**；加载 64.9s（阻塞）→ **0.04s** + 后台 14s 回灌 |
| 热扫描（索引全命中） | 102.7s → **45.8s**；冷扫描 ≈295s（bundle 解析 + catalog CRC 为主，一次性） |
| 全量搜索过滤+排序 | 5.4s → 4.8s（后台线程） |
| 树根层构建 / 全展开 | 0.5s / 7.2s → 0.47s / 6.3s |
| `text-index.db` | 冷建 9.6s → 二次进页面 608ms；搜索 105ms（vs 逐文件现读 1962ms） |
| `bank-index.db` | 1531 文件 / 52826 样本，冷建 19.3s → 热读 917ms（0 解析） |
| `static-tables.db` | 1392 张表 / 43.6MB 正文，冷建 6.5s → 热读 11ms（枚举 bundle 需 4.3s） |

---

## 5. 待办任务（按价值排序，含验收标准）

### T-A（等待用户）游戏内确认写回模组
- 唯一未闭环动作：用户按 `docs/REALDATA-VERIFY.md` §5 启动游戏，确认
  `Fx_T_Shape_LineFlash_01` 特效线条闪光变为纯白高亮，然后按步骤卸载（重命名加 `_disable` 或删除）。
- **无需 agent 动手**；若用户反馈「游戏内看不到变化」，排查顺序：
  ① 加载器是否真的加载了模组（LCTA 日志/`__original` 备份是否存在）；
  ② Carra2 键是否与缓存对齐（外层键随游戏更新会变）；
  ③ 是否被 `_disable` 或被其他模组覆盖。

### T-B（候选）静态模组 `fullFiles` 支持
- 现状：`.staticmod` 只支持 `opType=jsonpatch`，遇到 `fullFiles` 条目**明确报错**（编辑器导出也只走 jsonpatch）。
- 验收：能读取并应用 `fullFiles` 条目（整文件替换），带真实样本验证；无样本时保持明确报错。

### T-C（候选）catalog 依赖图与诊断合并报告
- 现状：`CatalogFileService` 的解析器已就绪，`catalogBaseline` 已写入资源元数据；
  但「缓存对齐 + vanilla 基线 + 依赖关系」没有合并视图。
- 验收：一个诊断视图/报告列出「缓存未对齐的 bundle」「相对 vanilla 被修改的 bundle」「静态 bundle 定位结果」，
  全中文说明，且不新增写盘面。

### T-D（候选）旧导出通道的取舍
- 现状：`ModExportService`（含 `ExportAllAsync`）、`ExportProgressWindow`、`ExportReportWindow`
  已不是界面入口，但仍是 CLI 与 5 个测试文件的依赖（约 17 处引用）。
- 决策点：要么正式退役（改 CLI + 迁移测试），要么在文档里明确「保留为 CLI/兼容通道」。**不要无计划地删**。

### T-E（候选）纹理格式扩展（需真实样本，保持不猜测）
- 现状：支持 RGB24 / RGBA32 / BGRA32 / ARGB32 / RGB565 / BGR24 / Alpha8 / R8 / R16 / RG16 / RGBA4444 / ARGB4444 / DXT1 / DXT5。
- 扩展 BC7 / ASTC / ETC 的前提：先拿到真实样本并用 AssetsTools 侧确认负载布局；**没有样本就不做**。

### T-F（候选）SpriteAtlas 网格重写
- 现状：读取可、写回阻止（保持）。需要真实 SpriteAtlas 样本 + 图集网格布局事实才能动。

### T-G（候选）工程化
- CI（Windows）+ 静态分析 + 覆盖率；格式 handler 的 fuzz/损坏输入测试（路径穿越、整数溢出、无限分配）；
  发布 zip + 校验和 + 第三方许可证清单；逐步 MVVM 化（不牺牲可运行性）。

### T-H（候选）性能收尾
- 40 万级资产下的列表虚拟化/分页；`UnityTextureCodec` 的逐像素路径（大纹理）块拷贝化；
  `ImagePreviewService.HasAlpha` 的全图扫描采样化。

---

## 6. 已知边界与已知问题（不要顺手改）

### 6.1 有意保留的边界

- **不写回**：SpriteAtlas 网格、Mesh/AnimationClip 可写字段、FEV 事件/总线索引——无真实样本支撑，保持「读取可、写回阻止」。
- **不解码**：加密 bank、未知 FSB 变体——明确报错。
- **mipmap**：只支持布局计算与切片，`ToImage`/`FromPng` 只处理 level 0。
- **静态模组**：只支持 `jsonpatch`（见 T-B）。
- **v2 文本美化规则集**：**不做**。该引擎只能表达字符串替换/包裹类动作（`replace/wrap/gradient/skill_color`），
  无法无损承载「精确逐字段赋值」，强行生成会得到「加载器不报错但改动丢失」的规则集。
  升级路径见 `docs/ARCHIVE-DESIGN-NOTES.md` §5。

### 6.2 已知问题

| 问题 | 影响 | 现状 |
|---|---|---|
| `RealBankIndexSmokeTests` 墙钟断言偶发超时 | 并行跑测试时偶发红 | 既有 flake，单独跑 2/2 通过；未放宽预算 |
| `Infrastructure/FileSystem/SafePathService.cs` 未被调用 | 越界校验逻辑在多处内联重复 | 见 `docs/PROJECT-INDEX.md` §4；统一化属候选重构 |
| `artifacts/**` 下有历史探针残留（`_probe.cs`） | 不参与构建 | 与源码无关，可忽略 |
| `tests/LimbusModEditor.Domain.Tests/UnitTest1.cs` 是空测试 | 无覆盖价值 | 保留（删除无收益，见 `docs/PROJECT-INDEX.md` §11.6） |
| 游戏更新后缓存外层键变化 | Carra2 导出会与缓存不对齐 | 由导出诊断暴露（**不自动修复**） |
| 旧导出通道与新导出并列 | 概念重叠 | 见 T-D 决策点 |

### 6.3 工具纪律事故记录（写文档必看）

- **2026-09-12**：编写本文档体系时，用 pwsh（`Get-Content` + `WriteAllLines`）批量改 `docs/PROJECT-INDEX.md`
  的行数，PowerShell 5.1 默认按 ANSI 读取 UTF-8，结果把整份中文文档写成乱码（已用 write 工具重建）。
  **教训**：仓库文本文件只通过 read/edit/write 工具修改（本项目铁律 §3-6），
  pwsh/脚本只用于 build / test / git / publish 与**只读**查询；派生数据（如行数表）也要用工具逐条改。

---

## 7. 文档地图与历史归档

| 文档 | 内容 |
|---|---|
| `README.md` | 项目门面：三步工作流、格式边界、CLI、打包与验证 |
| `docs/STATUS.md`（本文） | 现状 / 基线 / 铁律 / 环境事实 / 待办 / 已知问题 |
| `docs/CODE-STRUCTURE.md` | 分层与依赖、目录地图、界面外壳、核心流程、跨文件不变量、落盘位置、决策表 |
| `docs/PROJECT-INDEX.md` | **逐文件功能索引** + 元数据键字典 + 症状→文件速查 |
| `docs/USAGE.md` | 用户手册（界面行为、操作步骤、故障排查、格式边界、验证入口） |
| `docs/REALDATA-VERIFY.md` | 写回链路真实数据验证报告（含严重缺陷的根因与修复） |
| `docs/REVIEW.md` | 自审报告：审查发现、设计变更风险、性能量化基线 |
| `docs/ARCHIVE-DESIGN-NOTES.md` | 历史计划中被删文件里**仍然有约束力**的设计决策（其余历史见 git log） |
| `docs/ROADMAP.md` | 实现历史与后续方向（长文；P0–P4 的原始表述） |

**已删除/归档的历史文件**（2026-09-12）：

- `docs/plans/`（`plan-01` ~ `plan-17` 逐轮任务书 + `plans/README.md` 计划索引 + `plans/REALDATA-VERIFY.md`
  计划期真实数据对照矩阵；根级的 `docs/REALDATA-VERIFY.md` 是另一份、**保留**）
  —— 17 个计划全部已实施并入库，其内容可由 git 历史（`git log --oneline` / `git show <commit>:docs/plans/<file>`）完整取回；
  仍具约束力的设计决策已提炼进 `docs/ARCHIVE-DESIGN-NOTES.md`，
  其中「计划索引里的共同执行规则 / 真实环境事实」已并入本文 §3 / §4。
- `docs/NEXT-STEPS.md` —— 交接计划文档，其中的待办已并入本文 §5，环境事实并入 §4，
  铁律并入 §3，历史叙述由 git 历史保留。

> 归档原则：**计划文档不常驻**（已完成即归档）；**约束、事实、索引常驻**（本文 + CODE-STRUCTURE + PROJECT-INDEX）。
