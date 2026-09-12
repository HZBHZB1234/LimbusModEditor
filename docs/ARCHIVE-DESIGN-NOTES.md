# 历史设计决策备忘（ARCHIVE-DESIGN-NOTES）

> 背景：原先散落在 `docs/plans/plan-*.md`（plan-01 ~ plan-17）里的**设计决策**有一部分
> 至今仍有约束力（改代码时踩到就会出事），而计划文档本身已完成使命。
> 2026-09-12 删除计划目录时，把这些**仍有约束力的结论**提炼到本文；其余历史
> （逐轮验收细节、截图反馈原文、实施步骤）可由 git 历史取回：
> `git log --oneline`、`git show <commit>:docs/plans/plan-XX-*.md`。
>
> 阅读建议：先读 `docs/STATUS.md`（现状与待办）与 `docs/CODE-STRUCTURE.md`（结构与不变量），
> 本文只在涉及「为什么是这个口径 / 为什么不做某件事」时查阅。

---

## 1. 真实加载器（LCTA）认什么产物

事实来源：`E:\desktop\work\LCTA-Limbus-company-transfer-auto\launcher`（GPL-3.0，基于 LimbusModLoader v1.8）。
**代码可读作事实，不得整段复制。**

| 种类 | 扩展名 | 载荷事实（加载器口径） |
|---|---|---|
| 完整音频包 | `.bank` | 文件名 = 游戏内 `FMODBuilds/Desktop` 下的目标 bank 文件名，整包替换 |
| 音频差分 | `.rebank` | zip：`rebank.json`（`format/name/version/author/description/base_bank/created/count/files[]`）+ `{fsb序号}/{wav文件名}.wav`；`base_bank` 可带可不带 `.bank` |
| 资源对象包 | `.carra` / `.carra2` | zip：条目键 `<缓存外层键>/<内层键>/<pathId>[.<类型表索引>]`，条目值为 **XZ 压缩的对象原始字节** |
| Lunartique | `.zip` | 包内同时有 `Uninstallation`/`Installation` 根目录，两侧同构 `<account>/<bundle>/<path_id>.<type_id>`，条目为 `__data`（bundle 字节） |
| 文本补丁 | `.json` | `{"patchs": {"<相对 lang 根路径>": [RFC6902 操作…]}}` |
| 文本美化规则集 | `.json` | `lcta-bus` / 调爪 / FL / LCJE / v2 多种形状（见 §4、§5） |
| 静态数据 | `.staticmod` | zip：`manifest.json`（`format: staticmod/v1`、`patches[]`（`dataClass/file/opType∈{jsonpatch,pathset}/source/container?`）、`fullFiles[]`）+ `patches/<dc>.json` + `full/<dc>/<file>.json` |

**发现机制**：加载器用 `rglob` 扫描模组根下**任意层**（`modcache.enabled_mod_files`），
`_disable` 段即禁用。因此本编辑器的「种类 → 格式」两层目录**不影响被发现**，
但**加载器不认识这套目录约定**——导出产物是「交付给用户/加载器使用」，
不是「加载器直接读的模组根」。这句话必须在导出报告里对用户讲清（当前由
`App/ModExportReportWindow.cs` + `ModPackExportResult` 承担）。

---

## 2. 导出目录布局为什么是「种类 → 格式」两层

```
<目标目录>/
  <项目名>_fmod/       bank/<目标bank文件名>.bank   rebank/<bank基名>.rebank
  <项目名>_data/       carra/<项目名>.carra          lunartique/<项目名>.zip
  <项目名>_text/       bus/<相对lang根路径>.json     patch/…   pathset/…
  <项目名>_static/     staticmod/<项目名>.staticmod
```

- 唯一事实来源是 `Domain/Formats/ExportLayout.cs`（纯数据、无 IO、可单测）；`All` 的顺序 = 报告与执行顺序。
- **空槽位不建目录、不产空包**（宁缺勿假）。
- 两条命名口径由 `ExportSlotDescriptor.NameBySource` 表达：
  按**来源名**（bank/rebank：每个被改的 bank 一份）或按**项目名**（carra/lunartique/staticmod 单文件归档）；
  三个语言槽位按「每条被改文本表一份」。
- `DebugOverwritePatch` 表达调试应用语义（导出成 `__data`/`.bank` 后备份覆盖）。
- 三个语言格式的 `files` 字段用**文件名**（加载器按语言包内相对路径匹配），
  但报告里同时打印**完整相对 lang 根路径**便于核对。跨语言目录（如 `LLc-CN-LCTA` 与 `LLC_zh-CN`）
  天然落在不同子路径，不会冲突。

---

## 3. `bus` / `pathset` 的「可表达性门」——整份不产出，而不是产出半份

写 `bus` 规则集前必须检查「这条 diff 能不能用 bus 的 `set` 语义无损表达」，
否则会产出**加载器不报错但改动丢失**的规则集（对制作者最坏的结果）。
判据（依据 LCTA `webutils/fancy/bus.py` 的 `_PathResolver` 与 `_apply_replacements`）：

| diff 里的操作 | bus 能否表达 | 处理 |
|---|---|---|
| `replace /a/b/c`（值合法） | ✅ | 写 rule |
| `add` 到原文档**已存在**的末段 | ✅ | 写 rule |
| `add` 到原文档**不存在**的位置（新键 / 数组新增元素） | ❌ 被静默跳过 | **整份不产出** |
| `remove` | ❌ 无删除动作 | **整份不产出** |
| 数组元素增删（`remove`+`add` 序列） | ❌ 数组长度变了 | **整份不产出** |
| 路径段不是 `A-Za-z_` 标识符（如含 `-`） | ❌ 加载器按 `.` 分段解析 | **整份不产出** |
| 数组下标路径（原文档已存在该下标） | ✅ `path="list[3].name"` | 写 rule |

**为什么是「整份」**：半份规则集会让加载器只改一部分，而用户以为全改了——比「没有这个格式」危险得多。
只要有一个操作表达不了，该格式整份跳过，并在诊断里逐条列出原因；
`patch`（RFC6902）表达得了全部操作，报告里指明「完整改动见 patch 格式」。
实现：`Application/Texts/LangExportFormatter.cs`。

---

## 4. 为什么不做 LCTA 的「v2 文本美化规则集」槽位（`_text/v2/`）

事实来源：`webutils/fancy/engine.py`（全读）。

- `compile_rulesets` 只接受两种形状：规则集对象（必须 `version == 2`）或裸规则对象；
  `modfancy` 还要求 `version == 2 and "name" in data and "rules" in data`。
- 一条规则的字段：`files`（**必需**，`fnmatch` 匹配文件名）、`scope`、`targets`（**必需**路径数组）、
  `where`（可选条件，`operator ∈ {equals,in,contains,regex}`）、`actions`（**必需**非空数组）。
- 动作**只有四种**：`replace`（`from`/`to` 都必须是字符串，mode `literal|regex`）、`wrap`、`gradient`、`skill_color`。
- 应用语义：**只对字符串值生效**（`if not isinstance(value, str): return value`）。
- 路径语法：`a.b[3][*]`，**只有 `*` 与数字下标，没有选择器 `[?field=value]`**（后者是 bus 通道独有）。

与「精确文本编辑」的四处不匹配：

1. **只能改字符串**：数值/布尔/枚举字段、增删键、数组元素类型变化一律被静默忽略 → 「导出成功但改动丢失」。
2. **没有 `set` 动作**：改字符串只能用 `replace{mode:literal}`，而它是 `str.replace` 的**全量替换**
   （同一字符串出现两次就一起被改）。
3. **`targets` 语义是「扫全部命中位置」**：表达的是「批量美化规则」，不是「第 3 条记录的第 2 个字段改成这个值」。
4. **无法表达删除键 / 删除数组元素 / 新增数组元素**。

结论：**不提供该槽位**（符合仓库「不猜测」铁律）。若以后要做，正确做法是
**先在 C# 侧实现 v2 引擎的等价解释器**（含 `where` 求值、`*` 展开、四种动作、`skill_color`），
再以「生成 → 解释器回放 → 与目标 JSON 逐字节比对」作为验收门。那是一个独立计划。

---

## 5. `.rebank` 条目名必须是真实样本名（曾经的严重缺陷）

- 加载器按 `(FSB 序号, 样本名)` 匹配条目；旧实现写 `{i}.fsb` → **一条都匹配不上**，
  而加载器还会因「替换数 0」报错回滚。
- 正确做法：用 `Formats.Bank/Fsb5Models.cs`（`Fsb5Parser`）核对「改后与原版样本集合同构」，
  并用 FMOD 逐样本解码验证；结构不符或缺 DLL 时**整份跳过并说明**。
- 实测样本：`1D306I.assets.rebank` 的条目是 `0/1D306I-04.wav`（真实样本名）。

---

## 6. `text-index.db` 条目口径去掉「语言目录那一层」

- 条目口径 = **相对活动语言目录**（如 `StoryData/S1.json`）；`index_meta.language_prefix`
  存绝对路径（原样大小写）供读取方拼回磁盘路径；口径版本升级时**整库重建**。
- 加载器口径 = **相对 lang 根**（如 `LLc-CN-LCTA/StoryData/S1.json`）。
  两套口径之间只剩 `LangTextWorkbenchService.ToPatchKey` **一处转换**。
- **风险点**：若有人「顺手统一口径」，会同时打红
  `LangEntryCaliberTests`、`TextIndexStoreTests`、`RealTextIndexSmokeTests`、`ModExportPlanTests`、`LangExportFormatterTests`。
  现有往返测试就是把这条钉死的。

---

## 7. 表缓存的五条共同契约（plan-09 ~ plan-12 定下，不可违反）

1. 位置固定 `<程序目录>/cache/`（与既有 `unity-cache-index.db` 同处），**绝不写游戏目录 / Unity 缓存 / catalog**。
2. `index_meta(source_key PRIMARY KEY, signature[, language_prefix])`：签名变 → **整库重建**。
3. **只存 vanilla 事实**；文本/静态编辑集（内存修改）绝不进缓存，避免「缓存里是原版还是改动版」的歧义。
4. 失效规则只有「源签名变化」一条：文件用 `(size, mtimeTicks)`，内容哈希类直接用哈希文本；
   **绝不缓存游戏版本常量**。
5. 不做查询优化（万级行，索引够用即可）；加列走 `SqliteTableCache.EnsureColumn`（幂等 `ALTER TABLE`），
   不要改已发布建表脚本来「骗过」旧库。

---

## 8. 调试应用的语义与护栏（plan-16 定下）

- 写盘面：bank 覆盖 + 缓存 `__data` 就地重写 + lang 目录补丁 + 静态模组 catalog 双写。
- 护栏：**逐文件 sha256 + 备份 + `steps.tsv` 清单 + 关闭时逆序逐字节还原 + 目标被外部改动则不覆盖（记冲突）
  + 中途失败先整体回滚 + 游戏运行中拒绝执行**。
- catalog 写入按 `crc@Hash128+0x44` / `size@Hash128+0x48` 显式读写，并用 size 合理性（0.1–50 MB）作布局判据；
  **布局不符即拒绝写**（宁可不应用也不写错位置）。本机实测 catalog 记录 `size` 与磁盘 `__data` 字节数完全一致。
- 静态模组当前**只支持 `opType=jsonpatch`**，遇到 `fullFiles` 明确报错（见 `docs/STATUS.md` 待办 T-B）。

---

## 9. 其它仍然有效的实现结论

- **FMOD 2.x 兼容**：游戏自带 `fmodstudio.dll`（2.2.26）的 `FMOD_System_Create` 需要 `headerversion`；
  单参数调用会返回 `FMOD_ERR_HEADER_MISMATCH(20)`。现按导出符号所属 DLL 的文件版本推导 `FMOD_VERSION`，
  失败再退回 1.x 单参数调用。
- **Sprite 裁剪口径**：用 `m_RD.textureRect`（缺失时回退 `m_Rect`）。
  真实样本实测 `m_Rect` 是 Sprite 逻辑尺寸（含透明留白），`textureRect` 才是纹理内像素区域。
- **纹理行序**：Unity 负载左下原点；解码翻正、写回翻回，DXT 块行序同理，
  `UnitySpriteCrop` 的坐标换算与其互为配套（见 `docs/CODE-STRUCTURE.md` §6）。
- **容器路径与容器条目是两个字段**：`AssetRecord.ContainerPath`（CAB/SerializedFile 名）
  被导出/构建链路消费；`Metadata["containerEntry"]`（Unity `m_Container` 的游戏内路径）被显示层消费。
  **曾因混用出错并回退，不可混用。**
- **项目瘦身**：纯引用资产（`Metadata["reference"] == "true"`）保存时跳过，打开时从索引后台回灌；
  阅读旧项目文件必须保持兼容（`ProjectFileSlimmingTests` 守住）。
- **`AssetType` / `AssetEditState` 被 UI 当筛选值下发**：只能追加枚举成员，不能重排（否则筛选语义整体偏移）。
- **旧导出通道有意保留**：`ModExportService`（含 `ExportAllAsync`）、`ExportProgressWindow`、
  `ExportReportWindow` 仍被 CLI 与 5 个测试文件使用，删除会连带约 17 处引用；
  退役或保留需按 `docs/STATUS.md` 待办 T-D 正式决策。
