# Spine 数据缺失：详细调查与恢复报告

- 日期：2026-09-18
- 数据源（**全程只读**）：`artifacts/publish-win-x64/cache/{unity-cache-index,relation-index}.db`
  + `C:\Users\tester\AppData\LocalLow\Unity\ProjectMoon_LimbusCompany`（Unity 临时缓存，1,471 个 `__data`）
- 面向的问题：**「详细汇报 spine 文件缺少的状况与原因」**，以及需求方传闻 **「把一个 prefab 改为 json 就能跑了」**
- 所有结论都带**实测命令与原始输出**；一次性验证脚本放在系统临时目录（不入库），见 §7

---

## 0. 一句话结论（结论先行）

1. **需求方的说法不成立**：prefab 的序列化正文只有 **43 字节**，是 Unity `GameObject` 的头（内含 `10103_gacksung` 这个名字而已），**一个字节的 JSON 都没有**。把它另存为 `.json` 交给 JSON 解析器会直接报
   `UnicodeDecodeError: 'utf-8' codec can't decode byte 0x88 in position 11`。改名只会让错误信息从「找不到」变成「读不懂」。
2. **Spine 三件套一直都在**，就在同一个 bundle 里：走 **prefab 的引用链**（`GameObject → SkeletonGraphic → SkeletonDataAsset → 骨架 TextAsset / 图集 TextAsset / 纹理`）就能拿到。已实测打开：骨架 725 B（`spine 4.0.64` 真 JSON）、图集 152 B、纹理页 1 张 1,493,194 B。
3. **以前只找到 1 套，是三个叠加原因**（详见 §3）：
   - 这套素材**没有登记在 bundle 的容器表（m_Container）里** → 索引库里它们的 `container_entry` 是 `NULL`（全库 1,275,623 行里 **95.9% 无名字**），按名字 / 按同目录必然查不到；
   - 老的那条取数路径只有「**同目录三件套**」一种口径，天然覆盖不到没有名字的对象；
   - **组合根把 `SpineDataGateway` 指到了一个空的错库**（`spine-data.db`，0 字节、连 `assets` 表都没有）→ 连最后的兜底查询也是必失败（`SQLite Error 1: 'no such table: assets'`）。
4. **全库到底有多少套**（两个口径，实测，见 §4）：
   - **页面绑定口径**（维基 174 条绑定 / 122 个 prefab 路径）：现在能取出 **116 套 = 94.3%**。
   - **全库素材口径**（不看页面绑定，按正文普查全部 TextAsset）：读到 **797 个骨架 JSON**（合计 27.7 MB）+ **795 个图集**，分布在 303 个 bundle，同名同 bundle 可直接配成 **653 套**（下限，还有 12.6% 的 bundle 文件已被 Unity 清掉）。
5. **已经实现并接通**：`SpineData/` 网关新增「prefab 引用链」取数路径 + 修好组合根库名，**前端 API 形状没动**。真实页面 `persona:10103` 现已导出三件套地址（§6）。

---

## 1. prefab 里到底是什么（字节级证据）

取对象：`Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab`

| 项 | 实测值 |
|---|---|
| 索引库行 | `path_id=-4643219839723933324`、`type_id=1`（GameObject）、`size=43` |
| 所在 bundle | `...\ProjectMoon_LimbusCompany\535dd83b5e981000076283d14541cb7d\afe3c48d7a26d68004f2d76c3f4a11f1\__data`（93,408,868 字节） |
| bundle 内 SerializedFile | 1 个：`CAB-ef36192114d682290257a0c0f4851c86`（该 bundle 在索引库里登记了 **1,187 行对象**，其中只有 **11 行有名字**，就是 11 个 prefab） |

这 43 个字节的全部内容（`PROBE_VERBOSE=1` 打印）：

```
#1 pathId=-4643219839723933324 size=43 名=[10103_gacksung]
  hex: 0100 0000 0000 0000 7471 0188 0346 5FC7
       0000 0000 0E00 0000 3130 3130 335F 6761
       636B 7375 6E67 0000 0000 01
  utf8?: ........tq...F_.........10103_gacksung.....
```

把它当 JSON（改名后的真实结果）：

```
>>> json.loads(open('10103_gacksung.json','rb').read())
UnicodeDecodeError: 'utf-8' codec can't decode byte 0x88 in position 11: invalid start byte
```

**判定**：`size=43` 里唯一的可读信息是名字 `10103_gacksung`（`0x0E=14` 是字符串长度）。这是 Unity 序列化的 `GameObject` 头，**不是 Spine 骨架**，也不是文本。**「prefab 改名 .json」不成立**。

---

## 2. 真正的三件套在哪里：顺着引用链往下摸

同一个 bundle 里，从 prefab 根节点出发逐跳跟 PPtr（`_reader.ReadReferences` + `ClassIdOf` 打印）：

```
#1   GameObject       10103_gacksung                        size=43
  └ #4  Transform                                            → m_Children[]
      └ #4  Transform                                        → m_Children[]
          └ #1  GameObject      "SkeletonGraphic (back)"     ← 真正的骨架挂点
              └ #114 MonoBehaviour (SkeletonGraphic)         size=252  name=back_SkeletonData ...
                  └ #114 MonoBehaviour (SkeletonDataAsset)  "back_SkeletonData"
                      ├ #49 TextAsset  name=back            ← 骨架 JSON（740 字节）
                      └ #114 MonoBehaviour (AtlasAsset)
                          ├ #49 TextAsset  name=back.atlas  ← 图集文本（172 字节）
                          └ #21 Material   back_Material
                              └ #28 Texture2D  name=back    ← 纹理页
                                  ↳ archive:/CAB-ef36192114d682290257a0c0f4851c86/CAB-ef36192114d682290257a0c0f4851c86.resS
```

抓到的正文（探针原文，节选）：

```
#49 pathId=-3341109191163697221 size=740 名=[back | "skeleton": { | "hash": "+yI9H3yUoJo", | "spine": "4.0.64", ...]
{.."skeleton": {...  "hash": "+yI9H3yUoJo",   "spine": "4.0.64", ...},
 "bones": [...{ "name": "root", "scaleX": 0.5505, "scaleY": 0.5505 }...],
 "slots": [...{ "name": "Background", ...

#49 pathId=-6414534378254716526 size=172 名=[back.atlas | back.png | size:7460,6634 | filter:Linear,Linear | pma:true | Back_obj | bounds:...]
back.png
size:7460,6634
filter:Linear,Linear
pma:true
Back_obj
bounds:3742,10,6614,3708
rotate:90
```

即：**Spine-Unity 4.0.64 的标准三件套**，什么都没缺，只是没有登记名字。

---

## 3. 为什么以前只找到 1 套（三个叠加原因，全部实测）

### 3.1 原因一：这套素材没有登记到 bundle 容器表，索引库里查不到名字

```sql
SELECT CASE WHEN container_entry IS NULL THEN '无名字(未登记)' ELSE '有名字(登记)' END, COUNT(*)
FROM assets GROUP BY 1;
-- 无名字(未登记) 1,224,247      有名字(登记) 51,376       → 95.9% 的资源没有名字
```

具体到现场：

| 检查项 | 实测 |
|---|---|
| `container_entry LIKE '%Prefab/SpineIllustPrefab/%'` 的行数 | **123 行，全部 `type_id=1`（GameObject），全都是 `.prefab`** —— 目录里没有骨架 / 图集 / 纹理的任何一行 |
| 同一个 bundle（上述 93 MB 那个） | 索引登记 **1,187 个对象**，其中只有 **11 行有名字**（11 个 prefab），其余 1,176 个对象 `container_entry IS NULL` |

所以**按名字搜**、**按同目录列**（`LIKE '目录/%'`）都必然颗粒无收——旧实现正是这两条路。

### 3.2 原因二：旧取数路径只有「同目录三件套」一种口径

`SpineDataGateway.LoadSpineData` 原来只会：定位同目录 → 找 `*.json` + `*.atlas.txt` + `*.png`。
这套素材三样都没名字，于是必然走到「同目录里找不到骨架 JSON」。更早一轮的结论（「134 条绑定指向的 prefab 目录里没有任何骨架 JSON」）到这一步为止都**是对的**，只是没继续往下看引用链。

### 3.3 原因三（最致命）：组合根把网关指到了错的、空的库

```csharp
// src/LimbusModEditor.App/WebView2MainWindow.xaml.cs:119（CLI 同源，WikiExportIpcCommand.cs:59）
var spineData = new SpineDataGateway(Path.Combine(AppEnvironment.Current.CacheDirectory, "spine-data.db"));
```

`spine-data.db` 是 0 字节空文件，**没有任何写入方**（全仓库 grep 只有这两处引用）。而 `SpineAssetLocator` 查的是 `assets / bundles / strings` 三张表，库名必须是 `unity-cache-index.db`。实测日志：

```
2026-09-18 12:57:16 WARN  FindSiblings(SpineAssetLocator.cs:42) 查询 Spine 资源目录失败：Assets/Resources_moved/Prefab/SpineIllustPrefab
Microsoft.Data.Sqlite.SqliteException: SQLite Error 1: 'no such table: assets'
```

结论：**即使把引用链走到较好，只要这个库名不改，维基 Spine 覆盖仍然是 0%**。本轮一并修掉（§5）。

---

## 4. 全库到底有多少套三件套（两个口径）

### 4.1 页面绑定口径：**116 / 122 = 95%（全部 prefab 123 行 → 116 套）**

| 口径 | 数值 |
|---|---|
| `relation-index.db` 里 `kind='Spine'` 的绑定 | **174 条**，去重后 **122 个 ref_key** |
| 索引库 `Prefab/SpineIllustPrefab/*.prefab` 行 | 123 行 / **122 个不同路径**（`11112_gacksung.prefab` 在两个 bundle 里各有一份） |
| 这 122 个 ref_key 里，**bundle 文件在本机还在的** | **116 个** |

用**生产代码**（`SpineDataGatewayFactory.Create` → `GetSpineDataByPathAsync`）逐个跑这 123 行：

```
=== 普查结果（真实执行）===
命中 116/123（94.3%）· 纹理页合计 234 · 总耗时 261.8s

样例：
  [91/123] ✓ 10915_gacksung.prefab 骨架 267807 字节 · 图集 3878 字节 · 纹理 3 页（2.9s）
  [95/123] ✓ 11008_gacksung.prefab 骨架 55115 字节 · 图集 748 字节 · 纹理 3 页（3.4s）
  [112/123] ✓ 11115_gacksung.prefab 骨架 441851 字节 · 图集 3444 字节 · 纹理 2 页（3.2s）
```

未命中的 7 个，逐个查了原因：

| prefab | bundle 文件还在吗 | 真实原因 |
|---|---|---|
| `10116 / 10216 / 10615 / 10815 / 11013 / 11216` | **否** | Unity 临时缓存被清，`__data` 不在本机（字节都读不到）——重下拉/跑一次游戏就会回来 |
| `10616` | 是 | **这一版立绘本身不是 Spine**：引用链里只有 `Image` + `Sprite`（`cinqassoceast_honglu_BG`），没有 `SkeletonGraphic` |

即：**凡是本机还有 bundle 的页面上，Spine 三件套现在 100% 能取到**（116/116）。

### 4.2 全库素材口径：**797 骨架 + 795 图集 ≥ 653 套**（下限）

方法：**不按名字**，把索引库里 `type_id=49`（TextAsset，共 5,659 行，分布在 360 个 bundle）逐个读出正文判定：
骨架必须同时含 `"skeleton"` + `"bones"` 且能被 JSON 解析；图集必须首行是 `.png/.jpg` 且跟随 `size:/filter:/pma:/format:/scale:` 指令（这条判据专门用来排掉上一轮 65 个假命中里的 64 个 Unity `spriteatlasv2`）。

```
索引库里 TextAsset(type 49) 共 5659 个，分布在 360 个 bundle
=== 全库正文普查结果（真实执行）===
扫到 bundle 310 个（缺失 50，解包失败 0）
读出 TextAsset 正文 1688 个
Spine 骨架 JSON：797 个
Spine 图集：795 个
总耗时 9.2s
```

后处理：`含图集的 bundle 303 个 · 同名同 bundle 配成套数 653 · 骨架字节合计 27.7 MB`。
样本名字（`fairy_ism`、`contempt_ryoshu_idle`、`kimsakkat_idle`、`hatequeen_donquixote`、`student_dog_meursault`…）显示这套素材覆盖的远不止人格立绘（敌人/剧情也在用）。

> **为什么是下限**：1,471 个 bundle 里有 **185 个（12.6%） `__data` 已被 Unity 缓存清理**，其中就包含 3,969 个 TextAsset 行。它们不是「没有」，是「这台机器上此刻不在」。

---

## 5. 本轮改了什么（代码改动清单）

| 文件 | 改动 |
|---|---|
| `src/LimbusModEditor.Formats.Unity/AssetsToolsBackend.cs` | 新增 `CreateBundleSession(string bundlePath)` 与 `BundleObjectReader` 会话（同一个 bundle 只解一次包，逐跳读引用/TextAsset/classId 复用它）；把 `ReadBundleTextAsset` 的字段处理抽成共用的 `ReadTextAssetFields`、抽出 `CollectReferencesOf` |
| `src/LimbusModEditor.Application/SpineData/SpinePrefabChainResolver.cs`（新增） | prefab 引用链取数：BFS 遍历 PPtr（上限 400 节点 / 8 跳），**按正文**判定骨架与图集，按 `materials.Array[i]` 定纹理页序（拿不到则退回发现顺序）；`SpineAtlasPageNames` 从图集正文读页名 |
| `src/LimbusModEditor.Application/SpineData/SpineDataGateway.cs` | `LoadSpineData` 先试引用链（`FollowPrefabChain`），失败才回原「同目录三件套」路径并给出带「也没从引用链里找到」的中文原因；新增 `ReadTexturePng(bundleDataPath, pathId)` 重载（只有 path_id 时也能解码纹理页） |
| `src/LimbusModEditor.App/WebView2MainWindow.xaml.cs` | **修组合根**：网关库名 `spine-data.db` → `WorkbenchCachePaths.UnityCacheIndexFileName`（并写清了原因注释） |
| `src/LimbusModEditor.Cli/WikiExportIpcCommand.cs` | 同上（CLI 这条正是本报告的 §6 验证入口） |
| `tests/LimbusModEditor.Application.Tests/SpinePrefabChainRecoveryTests.cs`（新增） | 真实数据回归：取索引库前 3 个 `SpineIllustPrefab` prefab，断言有骨架、图集非空、有纹理且每页 > 0 字节（缺真实数据时自动跳过，CI 保持绿） |

**前端未改**：`src/LimbusModEditor.Web/` 一行没动；`spine.locate` / `wiki.getPage` 的响应形状也未改（`WikiMediaResolver` 本来就会把 `SpineRawData` 落成 `skeletonUrl/atlasUrl/textureUrls`，现在只是终于有值了）。

---

## 6. 真实页面验证：`persona:10103`（李箱 · SwordGroup）

把 CLI 发布到临时目录、把它的 `cache/` 用联接指到既有 `artifacts/publish-win-x64/cache`，走真正的 IPC `wiki.getPage`：

```
> LimbusModEditor.Cli.exe wiki-export-ipc MyMod.lmeproj "persona:10103" page-10103.json
  persona:10103 ? 43.6s

# logs/current.log
13:11:39 INFO FollowPrefabChain(SpineDataGateway.cs:221)
  prefab 引用链取到 Spine 三件套：Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab
  · 骨架 725 字节 · 图集 152 字节 · 纹理 1 页

# wwwroot/data/wiki（真的落盘）
       725  8ea88b3de48b34ee5ec9bfd542c114dc.json
       152  8ea88b3de48b34ee5ec9bfd542c114dc.atlas.txt
 1,493,194  9fa808fb4fed6a53b4ec91e2fe697b80.png

# 导出 payload（节选）
"bindings": [{ "refKey": ".../10103_gacksung.prefab", "kind": "Spine", "mediaKind": "spine",
  "skeletonUrl": "https://lme.data/wiki/8ea88b3de48b34ee5ec9bfd542c114dc.json",
  "atlasUrl":    "https://lme.data/wiki/8ea88b3de48b34ee5ec9bfd542c114dc.atlas.txt",
  "textureUrls": { "back.png": "https://lme.data/wiki/9fa808fb4fed6a53b4ec91e2fe697b80.png",
                   "back":     "https://lme.data/wiki/9fa808fb4fed6a53b4ec91e2fe697b80.png" } }]
```

对照组（**未修组合根**时，同一页、同一份数据）：

```
13:03:37 WARN  FindSiblings(SpineAssetLocator.cs:42) 查询 Spine 资源目录失败 → SQLite Error 1: 'no such table: assets'
13:03:37 DEBUG ResolveSpineAsync(WikiMediaResolver.cs:392) 维基 Spine 解析失败（降级为无地址）
→ 页面里 0 个 lme.data 的骨架/图集地址
```

---

## 7. 复现方法（全部可重跑）

一次性验证工程在系统临时目录，**不入库**：`C:\Users\tester\AppData\Local\Temp\spineprobe\`
（`spineprobe.csproj` 直接引用本仓库的工程，所以跑的是**生产代码**，不是复制粘贴的旁）

```bash
# 编译（含产量代码的最新改动）
cd %TEMP%\spineprobe && dotnet build -c Release --nologo

DB=E:\desktop\work\LimbusModEditor\artifacts\publish-win-x64\cache\unity-cache-index.db

# 1) prefab 自身字节 + 引用链逐跳（字节证据 §1、链接结构 §2）
dotnet run -c Release --no-build -- bundle            "<bundle __data 路径>"
dotnet run -c Release --no-build -- serializedfiles   "<bundle __data 路径>"
PROBE_VERBOSE=1 dotnet run -c Release --no-build -- prefab "<bundle>" "<CAB 名>" <pathId> 8

# 2) 单条页面绑定：生产网关能不能取出三件套
dotnet run -c Release --no-build -- chain  "$DB" "Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab"

# 3) 全量：123 个 prefab 逐个跑（261.8s → 116/123 · 234 页）
dotnet run -c Release --no-build -- census "$DB"

# 4) 全库：按正文（不按名字）普查所有 TextAsset → 797 骨架 / 795 图集
dotnet run -c Release --no-build -- library "$DB"     # 明细写入 %TEMP%\spineprobe\library-census.txt
```

上面 §3/§4 用到的 SQL 都是这几条（一律以 `mode=ro` 打开）：

```sql
-- 命名覆盖率（3.1）
SELECT CASE WHEN container_entry IS NULL THEN 'unnamed' ELSE 'named' END, COUNT(*) FROM assets GROUP BY 1;
-- 目录里到底有什么（3.1）
SELECT type_id, COUNT(*) FROM assets WHERE container_entry LIKE '%Prefab/SpineIllustPrefab/%' GROUP BY 1;
-- 页面绑定口径（4.1）
SELECT COUNT(*), COUNT(DISTINCT ref_key) FROM links WHERE kind='Spine';
-- 全库 TextAsset（4.2）
SELECT COUNT(*) FROM assets WHERE type_id=49;
```

---

## 8. 剩余缺口与下一步建议

1. **6 个 prefab（`10116/10216/10615/10815/11013/11216`）取不到，纯属本机缓存被清**（`__data` 不在）。这不是代码能补的；补 Unity 缓存（跑一次游戏或让/platform 重新下载）后这 6 个会自动恢复——届时可达 **122/122**。
2. **`10616` 这一版立绘本来就不是 Spine**（只有 Image/Sprite），页面显示空白/无动画是**数据事实**，建议在页面上按「同目录/引用链都找不到」的中文原因降级展示，而不是硬凑一套别的素材给它（不造假数据）。
3. **全库 653 套（下限）目前没有被任何页面绑定**：§4.2 里那些 `fairy_ism`、`contempt_ryoshu_idle`… 是敌人/剧情用的 Spine。要不要给它们也建绑定、是另一个产品决策；取数路径已经是现成的（同一个 `ISpineDataGateway`）。
4. **性能**：单个 prefab 平均 **2.1 s**（123 套 / 261.8 s），开销主要在大 bundle 解包（93 MB 那个）。维基一页配额是每类 4 条，够用；若要整表批量导出，建议加一层按 `refKey` 的结果缓存（现在没做，避免过早设计）。
5. **不再建议**任何形式的「按名字猜」路径：全库 95.9% 的资源没有名字，名字这条路天生覆盖不到这批数据。

---

## 9. 实测证伪：「按文件名数字前缀 = 页面 id」不可用（2026-09-18 追加）

上一轮报告 §8.3 曾建议"按人物/敌人 id 前缀把未绑定 Spine 挂到对应页面"。**该建议经实测证伪，已废弃**，改用既有权威反查表。

### 9.1 反例（用**已有的 174 条 ground-truth 绑定**做交叉验证）

| 反例 | 现象 |
|---|---|
| `abnormality:1080` | 实际持有 `10802_gacksung` —— 前缀 `10802` ≠ 页面 id `1080` |
| `ego_gift:1011` | 实际持有 `10110/10112/10113/10114/10115/10116/11011` 共 **6 个**不同 `_gacksung` —— 一对多，前缀规则无法表达 |
| `11108` / `11209` | 前缀**同时命中** `enemy:*` 与 `persona:*` —— **歧义**，必然编造关系 |

**冲突率：50 / 174 与真值冲突**；前缀规则覆盖率仅 **564/798**，且会**编造关系**（违反"不得编造关系"口径）。

### 9.2 采用的替代方案：权威反查表

`relation-index.db.subjects_by_ref`（由既有分析器 `RelationStore.PersistGraph` 从 `links` 派生，**不是新算的、不是猜的**）：

- 未绑定 798 条中 **746 条**在反查表里有权威映射，且**全部指向真实存在的 wiki 页面**；
- 覆盖率 **746/798**（vs 前缀规则 564/798），**与真值零冲突**；
- 余下 **52 条**无映射 → 进「未归类」，**不硬塞给任何角色**。

> 结论：判断"某个 Spine 属于谁"**只能**依据权威来源（反查表 / 静态表外键 / lang 权威清单），**禁止**用文件名数字前缀推断。
> 注意挂点总数随本机 Unity 缓存变化（本项目先后观测到 941 / 920），任何计数都应以**当次实测**为准并附命令。
