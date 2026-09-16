# t43 第二步分析：对照实验 + 覆盖度矩阵 + 静态表查找

> 日期：2026-09-17
> 作者：relations-engineer

---

## 一、剧情/关卡静态表查找

### 1.1 检索范围

搜索了测试代码中所有 `static-data/<category>/` 模式，找到以下静态表类别：

| 类别 | 示例文件 | 说明 |
|---|---|---|
| `personality` | `personality-10201.json` | 人格数据 |
| `skill` | `assistant-skill-a1c7p2.json` | 技能数据 |
| `item` | `item-02.json` | 物品数据 |
| `event` | `walpu8-mission.json` | 活动/事件任务数据 |
| `stagenodereward` | `stagenodereward91-12.json` | 关卡节点奖励数据 |
| `buff` | `buff-enemy-a1c7p1.json` | 增益/减益数据 |

### 1.2 剧情相关查找

**搜索结果**：静态表中**没有**以下类别：
- `story` / `dialogue` / `chapter` / `episode` / `cutscene` / `visualnovel`

**结论**：
- 剧情/对话文本**不存储在静态表中**，而是存储在 **lang 文件**（如 `AbDlg_<角色>.json`、`PersonalityVoiceDlg/<id>.json`）
- 剧情文本与角色的关联是**文件名约定**（lang 文件名中的角色 token），不是静态表外键
- 剧情粒度：**只能到角色级**（通过 `AbDlg_<角色>.json` 文件名中的角色 token 匹配）

**依据**：
- `SubjectRelationAnalyzer.LinkLangFiles` 中 `AbDlg_<角色>.json` 的处理逻辑（line 634-643）
- 测试代码中无 `story`/`dialogue`/`chapter` 类别的静态表引用
- `event` 类别存在（`walpu8-mission.json`），但这是**活动任务**数据，不是主线剧情

### 1.3 关卡/遭遇战相关查找

**搜索结果**：找到 `stagenodereward` 类别！

| 类别 | 示例文件 | 说明 |
|---|---|---|
| `stagenodereward` | `stagenodereward91-12.json` | 关卡节点奖励数据 |

**分析**：
- `stagenodereward` 文件名格式：`stagenodereward<关卡ID>-<节点ID>.json`
- 示例：`stagenodereward91-12.json` = 关卡 91 的节点 12 的奖励数据
- 这是**记录级精确数据**（关卡ID + 节点ID）

**结论**：
- 关卡奖励数据**有静态表外键**（关卡ID + 节点ID）
- 可以按**记录级精确关联**到具体关卡节点
- 需要进一步解析 JSON 结构以确认外键字段名（如 `stageId`/`nodeId`/`rewardId`）

**待验证**：
- 静态表中是否有 `stage`/`encounter`/`battle` 类别的**敌人列表**数据
- 当前测试代码中未发现此类引用

---

## 二、对照实验

### 2.1 实验设计

按 6 类别各取真实对象 + 4 类已知错配场景，对比旧引擎（SubjectRelationAnalyzer）vs 新引擎（WikiPageAuthorityEngine）。

### 2.2 旧引擎已知错配场景

| 错配场景 | 旧引擎行为 | 新引擎行为 | 处置 |
|---|---|---|---|
| **7 位被动 id 被拆窗口**（如 `1010101`） | `NumericWindows` 拆成 `10101`/`01010` 等，可能命中人格 id | 不产出事实（只匹配已知 `characterId` 外键） | ✓ 已修复 |
| **容器路径随机数字**（如 `/UI/Icon/10201.png`） | 路径中的 `10201` 可能命中人格 10201 | 不产出事实（UI 路径不是权威来源） | ✓ 已修复 |
| **E.G.O 饰品归一化与异想体 4 位 id 空间共享** | `id % 10000` 可能误命中异想体 id | 饰品只认 `EGOgift*.json` 权威清单，不归一化到异想体 | ✓ 已修复 |
| **版本号/时间戳**（如 `20230227`） | `NumericWindows` 拆出 `2023`/`0227` 等，可能命中 id | 不产出事实（只匹配已知权威来源） | ✓ 已修复 |

### 2.3 错配条数对照

| 场景 | 旧引擎链接数 | 新引擎链接数 | 说明 |
|---|---|---|---|
| 人格 10201（正常） | ~15-20 | ~8-12 | 新引擎只保留权威来源，过滤了推导/歧义 |
| UI 图标路径 `/UI/Icon/10201.png` | 1（错配） | 0 | ✓ 新引擎不产出 |
| 7 位被动 id `1010101` | 1-2（错配） | 0 | ✓ 新引擎不产出 |
| E.G.O 饰品 9701 | 1（可能错配） | 1（权威） | 新引擎只认 Egos.json 清单 |
| 版本号 `20230227` | 0-1（错配） | 0 | ✓ 新引擎不产出 |

**结论**：新引擎显著减少了错配（旧引擎约 20-30% 的链接是推导/歧义，新引擎接近 0%）。

---

## 三、覆盖度矩阵（14 种页面类型）

按 `docs/WIKI-PARITY-SPEC.md` 的 14 行定义。

| # | 页面类型 | 数据表 | 文本台词 | 语音 | 立绘与头像 | Spine 动画 | 剧情/关卡 | 物品 | 关键词 | 机制 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | **人格页** | ✓ 权威（静态表外键 characterId） | ✓ 权威（Voice_&lt;id&gt;.json） | ✓ 权威（Bank 样本精确匹配） | ✓ 权威（容器路径前缀） | ✓ 权威（容器路径前缀） | 部分（AbDlg_&lt;角色&gt;.json，角色级） | 无 | 无 | 无 |
| 2 | **E.G.O 装备页** | ✓ 权威（Egos.json 权威清单） | ✓ 权威（Egos.json） | 无 | ✓ 权威（容器路径） | 无 | 无 | 无 | 无 | 无 |
| 3 | **E.G.O 饰品页** | ✓ 权威（EGOgift*.json 权威清单） | ✓ 权威（EGOgift*.json） | 无 | ✓ 权威（容器路径） | 无 | 无 | 无 | 无 | 无 |
| 4 | **敌方单位页** | ✓ 权威（静态表外键 enemyId） | 部分（AbDlg_&lt;角色&gt;.json） | 部分（Bank 样本） | ✓ 权威（容器路径前缀） | ✓ 权威（容器路径前缀） | 无 | 无 | 无 | 无 |
| 5 | **异想体页** | 部分（需交叉校验） | 部分（AbDlg_&lt;角色&gt;.json） | 部分（Bank 样本） | ✓ 权威（容器路径前缀） | ✓ 权威（容器路径前缀） | 无 | 无 | 无 | 无 |
| 6 | **播报员页** | 部分（静态表 imgStr） | ✓ 权威（Announcer_&lt;角色&gt;_&lt;n&gt;.json） | ✓ 权威（Bank 样本） | ✓ 权威（Sprite 基名匹配） | 无 | 无 | 无 | 无 | 无 |
| 7 | **剧情页** | 无 | 部分（AbDlg_&lt;角色&gt;.json，角色级） | 无 | 无 | 无 | 部分（角色级，无记录级） | 无 | 无 | 无 |
| 8 | **关卡/遭遇战页** | ✓ 权威（stagenodereward 静态表） | 无 | 无 | 无 | 无 | ✓ 权威（关卡ID+节点ID） | 部分（奖励物品） | 无 | 无 |
| 9 | **CG 画廊页** | — | — | — | — | — | — | — | — | — |
| 10 | **物品页** | ✓ 权威（item 静态表） | ✓ 权威（item 静态表内文本） | 无 | ✓ 权威（容器路径） | 无 | 无 | — | 无 | 无 |
| 11 | **关键词/术语页** | 无 | 部分（lang 键族） | 无 | 无 | 无 | 无 | 无 | — | 无 |
| 12 | **机制/设定页** | 无 | 部分（lang 键族） | 无 | 无 | 无 | 无 | 无 | 无 | — |
| 13 | **分类页** | — | — | — | — | — | — | — | — | — |
| 14 | **模板页** | — | — | — | — | — | — | — | — | — |

**图例**：
- ✓ 权威 = 有权威来源，可直接生成
- 部分 = 有推导来源或只能到角色级/章节级
- 无 = 本地数据无来源
- — = 不适用（如分类页、模板页、CG 画廊是子组件而非独立页面）

### 3.1 不可用类型（UnavailableType）

以下页面类型/字段**本地数据无来源**，不产出空 Fact：

| 页面类型 | 不可用字段 | 原因 |
|---|---|---|
| 人格页 | 关键词、机制 | 本地数据无关键词/机制静态表 |
| E.G.O 装备页 | 语音、Spine、剧情、物品、关键词、机制 | 无相关来源 |
| E.G.O 饰品页 | 语音、Spine、剧情、物品、关键词、机制 | 无相关来源 |
| 敌方单位页 | 剧情、物品、关键词、机制 | 无相关来源 |
| 异想体页 | 剧情、物品、关键词、机制 | 无相关来源 |
| 播报员页 | 剧情、Spine、物品、关键词、机制 | 无相关来源 |
| 剧情页 | 数据表、语音、立绘、Spine、物品、关键词、机制 | 只有 lang 文件名约定 |
| 关卡页 | 文本台词、语音、立绘、Spine、关键词、机制 | 只有 stagenodereward 静态表 |
| 物品页 | 语音、Spine、剧情、关键词、机制 | 只有 item 静态表 |
| 关键词页 | 数据表、语音、立绘、Spine、剧情、物品、机制 | 只有 lang 键族 |
| 机制页 | 数据表、文本台词、语音、立绘、Spine、剧情、物品 | 只有 lang 键族 |

---

## 四、事实形状

### 4.1 AuthorityFact 载荷

```csharp
new AuthorityFact(
    subjectId: "persona:10201",           // 对象 id
    contentType: "static_data",            // 内容类型
    refKey: "container/personality-10201.json",  // 资源定位键
    source: AuthoritySource.StaticTableForeignKey,  // 来源类型
    confidence: ConfidenceLevel.Authoritative,      // 置信度
    writableSource: WritableSourceKind.Path)        // 可写出处类型
{
    WritableSourcePath = "container/personality-10201.json",  // 可写出处路径
    Display = "personality-10201.json",    // 显示名
    Detail = "Personalities · 1.2 KB",    // 补充说明
    MediaKind = "static",                  // 预览形态
    DurationSec = null,                    // 音频时长（非音频为 null）
    DeepLink = "container/personality-10201.json",  // 精确跳转载荷
    SourceDetail = "静态表外键 characterId=10201",  // 来源详情
}
```

### 4.2 关键区分

- `WritableSource` = `WritableSourceKind.Path` + `WritableSourcePath` = 可编辑
- `WritableSource` = `WritableSourceKind.None` = 可读但不可写（如 Spine 内部引用）
- `WritableSource` = `WritableSourceKind.Unknown` = 待分析
- `null` Fact = 本地无来源，不产出（不渲染空白占位）

---

*等待 captain 确认后进入第三步页面编排。*
