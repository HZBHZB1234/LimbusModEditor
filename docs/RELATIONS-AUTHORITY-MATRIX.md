# t43 第一步：权威来源清单 + 覆盖度矩阵 + 旧规则处置

> 日期：2026-09-16
> 作者：relations-engineer
> 状态：分析完成，等待 captain 拍板

---

## 一、权威来源清单

以下盘点本地数据中「能不靠 id 拟合就把内容归到正确对象名下」的依据。

### 1.1 Unity m_Container（容器路径前缀）

| 路径前缀 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `/Prefab/SD/Personality/<id>_<character>_<style>Appearance.prefab` | 人格（Persona） | **权威** | 人格身份来源；`SubjectRelationAnalyzer.ReadPersonaIdentityFromAsset` |
| `/Prefab/SD/Enemy/<id>_<name>Appearance.prefab` | 敌人（Enemy） | **权威** | 敌人身份来源；`ReadPrefabIdentities` |
| `/Prefab/SD/Abnormality/<id>_<name>Appearance.prefab` | 异想体（Abnormality） | **权威** | 异想体身份来源；`ReadPrefabIdentities` |
| `/Sprite/BattleAnnouncer/<name>_announcer.png` | 播报员（Announcer） | **权威** | 播报员身份来源；`ReadAnnouncerIdentities` |
| `/Sprite/Unit/Profile/Ego/<id>.png` | E.G.O 装备 | **推导** | 路径含 EGO 标记，但非身份来源 |
| `/Sprite/EgoGiftIcon/<id>.png` | E.G.O 饰品 | **推导** | 路径含饰品标记，但非身份来源 |
| `/PersonalityVideo/<id>.mp4` | 人格 | **权威** | 人格身份来源；`ReadPersonaIdentityFromAsset` |
| `/Story/Spine/**` 目录 | Spine 动画 | **推导** | 目录约定，但需解析 prefab 引用确认 |
| `/Prefab/SpineIllustPrefab/<id>_gacksung.prefab` | 人格立绘 | **权威** | Spine 立绘 prefab |

**限制**：m_Container 只给出「路径 → 对象」的映射，不能处理：
- 没有容器路径的资源（松散文件）
- 资源引用关系（哪些纹理被哪些 prefab 使用）

### 1.2 Lang 文件名与键路径

| 文件名模式 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `Voice_<prefix>_<character>_<id>.json` | 人格 | **权威** | `ReadPersonaIdentityFromVoiceFile`；样本名一对一 |
| `AbDlg_<character>.json` | 该角色的全部人格 | **推导** | `LinkLangFiles` ③；按角色 token 归属 |
| `Announcer_<character>_<n>.json` | 该角色的播报员 | **推导** | `LinkLangFiles` ④；按角色 token 归属 |
| `Egos.json` | E.G.O 装备 | **权威** | `ReadAnchorIdentities`；EGO id 集合就是实体清单 |
| `EGOgift*.json` | E.G.O 饰品 | **权威** | `ReadAnchorIdentities`；饰品 id 集合就是实体清单 |
| `Skills.json` / `Passives.json` | 人格（通过外键） | **推导** | 数值 id 是外键，不能当实体清单 |
| `PersonalityVoiceDlg/<角色>.json` | 人格 | **推导** | 文件名含角色 token |

**限制**：
- 角色 token 归并依赖已知别名表（`CharacterAliases`），新角色/新拼写会漏
- `AbDlg_`/`Announcer_` 的文件名角色 token 需要精确匹配

### 1.3 Lang 权威清单

| 清单 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `Egos.json` 的 id 集合 | E.G.O 装备身份 | **权威** | 实测 17 个 20xxx 数字不在 Egos.json 里（幽灵 EGO） |
| `EGOgift*.json` 的 id 集合 | E.G.O 饰品身份 | **权威** | 同一饰品分层 id（9701/19701/29701）归一化到 4 位基准 |
| `Skills.json` 的 skillId | 人格/敌人 | **推导** | 外键，需 join |
| `Passives.json` 的 passiveId | 人格/敌人 | **推导** | 外键，需 join |

### 1.4 静态表显式外键字段

| 外键字段 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `characterId` | 人格/敌人/异想体 | **权威** | 静态表里的显式外键，硬关系 |
| `skillId` | 技能 | **权威** | 静态表里的显式外键 |
| `EGOId` | E.G.O 装备 | **权威** | 静态表里的显式外键 |
| `imgStr` | 播报员 | **推导** | 静态表里的播报员图基名 |
| `id` + 上下文 | 视情况 | **推导** | 需结合表结构判断 |

**限制**：
- 需要解析 JSON 结构，不能只靠正则
- 字段名可能不统一（`characterId` vs `character_id`）

### 1.5 Bank 目录与样本命名约定

| 约定 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `<id>.assets.bank` | 人格/敌人 | **推导** | 文件名含 id，需匹配已知集合 |
| `Voice_<prefix>_<character>_<id>_*.bank` | 人格 | **权威** | 样本名含角色 token + id |
| `announcer_<encounter>_<characterId>_<n>` | 播报员 | **推导** | 需结合静态表 imgStr 反查 |
| 样本名 == 台词 id | 人格（精确） | **权威** | 实测一对一；`LinkAudio` ① |

**限制**：
- 文件名中的 id 需要已知集合才能归属
- announcer 样本需要静态表辅助

### 1.6 Spine 引用关系

| 来源 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| `/Prefab/SpineIllustPrefab/<id>_gacksung.prefab` | 人格立绘 | **权威** | 路径约定 |
| `StorySpine_*` 目录 | CG Spine | **推导** | 目录约定 |
| Prefab 内部真实引用 | Spine 骨骼/贴图 | **权威** | 解析 prefab 的 ScriptableObject 引用链 |
| `.psb` 文件 | 立绘 PSB | **推导** | 文件名约定 |
| `*_SkeletonData.asset` | Spine 数据 | **推导** | 文件名约定 |

**限制**：
- 解析 prefab 引用链需要 Unity 反序列化，工作量大
- 目录约定可能不可靠

### 1.7 纹理 / SpriteAtlas 引用关系

| 来源 | 能归到谁名下 | 可靠度 | 实测依据 |
|---|---|---|---|
| Sprite → Texture 引用 | 图像归属 | **权威** | Unity 资产数据库里的引用链 |
| SpriteAtlas → 子图 | 图集归属 | **权威** | Unity 资产数据库里的引用链 |
| 容器路径前缀 | 图像归属 | **推导** | `/Sprite/Unit/Profile/` 等 |

**限制**：
- 需要 Unity 资产数据库（AssetDatabase）或反序列化
- 引用链可能跨 bundle

---

## 二、覆盖度矩阵

以维基内容类型为行，本地数据源为列。量出能自动生成 / 只能部分 / 彻底没有。

| 维基内容类型 | 人格 | 敌人 | 异想体 | 播报员 | E.G.O 装备 | E.G.O 饰品 |
|---|---|---|---|---|---|---|
| **实体页信息** | | | | | | |
| 信息框字段（CV/性别/身高等） | 部分 | 部分 | 无 | 无 | 部分 | 部分 |
| 一句话摘要 | 部分 | 部分 | 部分 | 无 | 部分 | 部分 |
| 封面立绘 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **数据表** | | | | | | |
| 静态数据表 | ✓ | ✓ | ✓ | 部分 | ✓ | ✓ |
| 技能/被动数据 | ✓ | ✓ | ✓ | 无 | ✓ | 无 |
| **文本台词** | | | | | | |
| 人格语音台词 | ✓ | ✓ | 部分 | 无 | 无 | 无 |
| 播报员台词 | 无 | 无 | 无 | ✓ | 无 | 无 |
| 剧情文本 | 部分 | 部分 | 部分 | 无 | 无 | 无 |
| **语音** | | | | | | |
| 战斗语音 | ✓ | ✓ | 部分 | 无 | 无 | 无 |
| 移动/待机语音 | ✓ | 部分 | 无 | 无 | 无 | 无 |
| 播报语音 | 无 | 无 | 无 | ✓ | 无 | 无 |
| **立绘与图集** | | | | | | |
| 人格立绘（Spine） | ✓ | ✓ | ✓ | 无 | 无 | 无 |
| CG 立绘 | ✓ | 部分 | 部分 | 无 | 无 | 无 |
| 头像/缩略图 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **Spine 动画** | | | | | | |
| 人格立绘 Spine | ✓ | ✓ | ✓ | 无 | 无 | 无 |
| CG Spine | 部分 | 部分 | 无 | 无 | 无 | 无 |
| **剧情/关卡** | | | | | | |
| 遭遇战 | 无 | 部分 | 无 | 无 | 无 | 无 |
| 异想体日志 | 无 | 无 | 部分 | 无 | 无 | 无 |
| **物品与关键词** | | | | | | |
| E.G.O 装备 | 无 | 无 | 无 | 无 | ✓ | 无 |
| E.G.O 饰品 | 无 | 无 | 无 | 无 | 无 | ✓ |

**图例**：
- ✓ = 能自动生成（有权威来源）
- 部分 = 能部分生成（有推导来源或需要交叉校验）
- 无 = 没有可靠来源

**真实规模参考**：
- 1,275,623 资源 / 1,459 bundle / 1,531 bank / 2,048 lang 文件 / 1,392 静态表 / 1,313 对象 / 62,970 链接

---

## 三、旧 id 拟合规则清单 + 已知错配实例 + 处置建议

### 3.1 `NumericWindows`（扫 4-5 位数字窗口）

**规则**：从文本中扫描所有 4-5 位连续数字子串，然后匹配已知数字 id 集合。

**代码位置**：`SubjectRelationAnalyzer.NumericWindows`（line 110-140）

**已知错配风险**：
1. **静态表里的被动 id（7 位）**：如 `1010101`，会被拆成多个 4-5 位子串（`10101`、`01010` 等），可能意外命中人格 id
2. **EGO 装备 id（5 位）**：如 `20101`，可能与人格 id 的前 5 位碰撞
3. **容器路径里的随机数字**：如 `/Assets/Art/UI/Icon/12345.png`，可能命中某个对象的 id
4. **版本号/时间戳**：如 `20230227`（发布日期），会拆出多个 4-5 位窗口

**处置建议**：**降为交叉校验**
- 仅当路径/文本已自带类别标记时才使用
- 同一数字属于多个类别时必须标 `Ambiguous`
- 静态表放宽到 4-8 位时需要额外过滤

### 3.2 `MatchNumeric`（数字索引匹配）

**规则**：把扫描到的数字与已知身份的数字索引匹配，带上类别。

**代码位置**：`SubjectRelationAnalyzer.MatchNumeric`（line 811-827）

**已知错配风险**：
1. **4 位 vs 5 位前缀碰撞**：敌人 4 位 id（如 `8050`）可能与人格 5 位 id 的前 4 位（如 `80501`）碰撞
2. **E.G.O 饰品归一化碰撞**：`id % 10000` 后可能与异想体 4 位 id 碰撞（当前实测交集为 0，但空间共享）

**处置建议**：**保留但受限**
- 仅匹配「键就是该数字」的身份
- 4 位窗口不命中 5 位前缀（已实现）
- E.G.O 饰品只认 lang 权威清单

### 3.3 `LinkAssets`（资源链接中的数字匹配）

**规则**：扫描资源容器路径中的数字，匹配已知对象。

**代码位置**：`SubjectRelationAnalyzer.LinkAssets`（line 458-484）

**已知错配风险**：
1. **UI 图标路径**：`/Assets/Art/UI/Icon/10201.png` 可能意外命中人格 10201
2. **共享资源路径**：`/Assets/Art/Shared/Texture/10201.png` 不属于任何对象

**处置建议**：**降为交叉校验**
- 需要路径自带类别标记（如 `/Prefab/SD/Personality/`）
- 中立路径（无类别标记）需要额外过滤

### 3.4 `LinkAudio`（音频样本名匹配）

**规则**：扫描 bank 样本名中的数字，匹配已知对象。

**代码位置**：`SubjectRelationAnalyzer.LinkAudio`（line 500-553）

**已知错配风险**：
1. **announcer 样本**：`announcer_<encounter>_<characterId>_<n>` 需要静态表 imgStr 反查
2. **共享音效**：样本名可能含数字但不属于任何对象

**处置建议**：**保留但受限**
- 精确匹配（样本名 == 台词 id）保留为权威
- 数字窗口匹配需要已知集合

### 3.5 `LinkStaticTables`（静态表文本匹配）

**规则**：扫描静态表文本中的数字（放宽到 4-8 位），匹配已知对象。

**代码位置**：`SubjectRelationAnalyzer.LinkStaticTables`（line 556-591）

**已知错配风险**：
1. **被动 id（7 位）**：`1010101` 会被拆成多个窗口
2. **EGO 装备 id（5 位）**：可能与人格 id 碰撞
3. **版本号/时间戳**：静态表里可能有发布日期等数字

**处置建议**：**降为交叉校验**
- 仅当命中已知 anchor 集合时才链接
- 需要结合表结构判断外键字段

### 3.6 `LinkLangFiles`（Lang 文件名匹配）

**规则**：扫描 lang 文件名中的数字或角色 token，匹配已知对象。

**代码位置**：`SubjectRelationAnalyzer.LinkLangFiles`（line 601-671）

**已知错配风险**：
1. **角色 token 精确匹配**：依赖已知别名表，新角色会漏
2. **`AbDlg_`/`Announcer_` 前缀**：需要文件名严格符合约定

**处置建议**：**保留**
- 角色 token 精确匹配是可靠的（不做模糊匹配）
- 别名表可扩展

### 3.7 `id % 10000` 归一化（E.G.O 饰品）

**规则**：同一饰品在 lang 里分层（9701/19701/29701），归一化到 4 位基准。

**代码位置**：`SubjectRelationAnalyzer.ReadAnchorIdentities`（line 310-316）

**已知错配风险**：
1. **与异想体 4 位 id 碰撞**：当前实测交集为 0，但空间共享
2. **归一化后不是 4 位**：低于 1000 的 id 被过滤

**处置建议**：**保留**
- 权威来源（lang Egos.json）已验证
- 归一化仅用于饰品类别

---

## 四、处置建议汇总

| 规则 | 处置 | 理由 |
|---|---|---|
| `NumericWindows` | 降为交叉校验 | 数字窗口容易误命中无关数字 |
| `MatchNumeric` | 保留但受限 | 仅匹配已知身份，需类别标记 |
| `LinkAssets` | 降为交叉校验 | 需要路径自带类别标记 |
| `LinkAudio` | 保留但受限 | 精确匹配可靠，数字窗口需已知集合 |
| `LinkStaticTables` | 降为交叉校验 | 需要结合表结构判断外键 |
| `LinkLangFiles` | 保留 | 角色 token 精确匹配可靠 |
| `id % 10000` 归一化 | 保留 | 权威来源已验证 |

---

## 五、下一步建议

1. **新引擎应优先使用权威来源**：
   - Unity m_Container 路径前缀
   - Lang 文件名约定（Voice_/AbDlg_/Announcer_）
   - Lang 权威清单（Egos.json）
   - 静态表显式外键字段

2. **id 拟合仅用于交叉校验**：
   - 当权威来源无法覆盖时
   - 需要标注置信度（`RelationPreviewKind.Derived`）
   - 需要标注歧义（`RelationPreviewKind.Ambiguous`）

3. **需要新的能力**：
   - 解析 JSON 结构（静态表外键）
   - 解析 prefab 引用链（Spine 归属）
   - 纹理/Sprite 引用链（图像归属）

---

*等待 captain 拍板后进入第二步。*
