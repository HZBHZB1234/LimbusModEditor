# Plan 08 — 静态数据工作台页面（新页面）+ 资源工作台剔除相关 bundle

> 覆盖需求 10：「static mod 对应的是一个特殊的 bundle，应该新建一个页面来编辑他。
> 可以在资源工作台页面剔除相关 bundle。」

## 1. 事实依据（真实加载器 + 既有实现）

- 特殊 bundle：`static_s1_0_assets_all_<32hex>.bundle`（LCTA `launcher/staticmod.py:65`）。
  它是全表唯一开启 `UseCrcForCachedBundles=true` 的条目——缓存命中时引擎对「解压后块数据拼接」
  算 CRC32 并与 catalog 记录比对，失配即清缓存重下。因此 static mod 不能只改缓存 `__data`，
  必须「重打包 bundle + 算新 CRC/size + 双写 catalog + 重建缓存条目」——这些全部由**加载器**完成
  （`staticmod.py:601-680`），编辑器只产出 `.staticmod` 包。
- catalog 定位（`staticmod.py:108-156,466-469`）：**运行时** catalog
  `LocalLow/ProjectMoon/LimbusCompany/com.unity.addressables/catalog_S1.bin`（游戏实际读取、
  加载器双写目标）中搜 bundle 名 → 取尾段 32hex 作为 content hash（= 缓存内层键）→ 其 Hash128
  记录后紧跟外层键 → crc/size 字段（带 +0x44/+0x48 或 +0x3C/+0x40 双布局自校准，编辑器
  `CatalogFileService` 已实现同名解析）。游戏安装目录 `StreamingAssets/aa/catalog.bin` 只作兜底
  ——2026-09 实测两份 catalog 可能指向不同内容哈希（安装目录 `edb72aec…` / 运行时 `62d6e466…`，
  缓存里只有后者），因此定位按候选列表逐个尝试并优先取「缓存真正命中」的那份。
- `.staticmod` 格式：zip = `manifest.json`（format=staticmod/v1, patches[], fullFiles[],
  每条可选 `container` 字段精确寻址）+ `patches/<dc>.json`（opType=jsonpatch|pathset）+
  `full/<dc>/<file>.json`。bundle 内目标是 **TextAsset**，按 `container` 精确匹配或
  `m_Name == file | "dataClass/file"` 兜底（`staticmod.py:258-299`）。
- 编辑器既有能力：`StaticModService`（Read/Write/CreateJsonPatchPackage/ApplyPatchToDocument/
  PathsetToJsonPatch，8 测试含 2 真实样本）、`CatalogFileService`、`StaticModControl`
  （过渡期页面：手工填 dataClass/file 生成补丁——没有浏览、没有可视化 diff）。

## 2. 页面设计

```
[活动栏 48][共享侧边栏 220][静态表浏览列 *][splitter][预览/编辑列]
```

- 活动栏 🧩「静态数据工作台」（plan-02 过渡页转正）。
- **数据源与定位**（`StaticBundleLocator`，新，Application/StaticMods/）：
  1. 读 catalog（复用 `CatalogFileService`）→ 找 `static_s1_0_assets_all_*.bundle` 名 →
     得内层 hash + 外层键；
  2. 在共享配置缓存根找 `<outer>/<inner>/__data`（多缓存根候选：LocalLow + `D:\Unity\...`，
     对齐 `_cache_roots()` 事实）；找不到 → 明确提示「启动一次游戏生成缓存」；
  3. 用 AssetsTools.NET 解析该 bundle，枚举全部 TextAsset（class 49）：
     `m_Name`（`dataClass/file` 或 `file`）、`m_Script` JSON、大小；**只读引用模式**，不复制。
- **浏览列**：按 dataClass 分组树（`m_Name` 含 `/` 时取前段；否则归「未分组」）+ 搜索
  （表名/键/值全文，防抖后台）+ 列表（名称/大小/已修改）。
- **预览/编辑列**：JSON 编辑（同 plan-07 键值表 + 原文双 Tab）；与官方版本的 diff 视图
  （`TextDiffService` 生成 RFC6902 供预览）；「还原此表」。
- **导出**：编辑集 → `StaticModService.CreateJsonPatchPackage`（opType=jsonpatch，manifest
  写入 `container`（容器路径）用于精确寻址）→ `Write` 到模组目录 `<名>.staticmod`；
  UI 说明真实加载器风险开关（LCTA 默认关闭，需在 Launcher 勾选启用）与 catalog 双写由加载器负责。
- **资源工作台剔除（需求 10 后半）**：
  - `UnityCacheScanService` 扫描时调用 `StaticBundleLocator` 判定当前 bundle 是否 static 条目
    （catalog 名匹配，随扫描一次性解析 catalog，零额外开销）；
  - 命中：其资产写入元数据 `staticBundle=true`；`AssetSearchService` 默认过滤掉
    （与「仅显示容器内资源」同级的新默认开关「显示静态数据表」，默认关）——资源工作台看不到，
    plan-08 页面侧按同元数据可反向检索；
  - 已有项目兼容：旧项目里已索引的 static 资产靠过滤器隐藏，不做数据删除。

## 3. 实施步骤

1. `StaticBundleLocator`（catalog 定位 + 缓存条目解析 + TextAsset 枚举），真实门控单测
   （catalog 名匹配、外层键校验、缓存命中/未命中两分支）。
2. `UnityCacheScanService` 接入标记 + `AssetSearchService` 默认过滤 + 资源工作台 UI 开关。
3. `App/StaticWorkbenchPage.xaml(.cs)`：树/搜索/编辑/diff/导出；替换 plan-02 过渡承载的
   `StaticModControl`；`StaticMod_Click` 旧入口删除；`StaticModService.CreateJsonPatchPackage`
   如需支持多文件与 container 字段的批量形态则小幅扩展（保持 manifest schema 不变）。
4. 导出思路联动：`ExportIdeaKind` 中 static 相关出口路由 `ShowPage("static")`（如无现成 kind 则不加）。

## 4. 验收标准（审查门）

- [x] 真实环境冒烟：catalog 定位成功（bundle 名 + 外层键 + 缓存 `__data` 命中）；TextAsset 枚举数
      > 0 且 `m_Name`/JSON 可读。（2026-09 修复 catalog 来源后实测：运行时 catalog
      `62d6e466…` + 外层键 `64bd0105…` + `__data` 命中，枚举 1392 张表）
- [x] 修改一张表（如 personality 类）→ 导出 `.staticmod` → `StaticModService.Read` 回读一致；
      manifest 含 container；jsonpatch ops 与 diff 视图一致。
- [x] 把导出包交给真实加载器语义（或对齐 `staticmod.py` 的单测夹具）验证：container 精确匹配、
      旧式名字兜底两种路径都能定位目标 TextAsset。（真实样本：container 精确匹配 1 命中、名字兜底 1 命中）
- [ ] 资源工作台默认视图不再出现 static bundle 的资产；开关打开后可见（新旧项目都验证）。
- [ ] 编辑器全程不写 catalog/缓存/游戏目录（代码审查 + grep）。
- [ ] build + test 全绿；USAGE 新节 + 风险提示。

## 5. 风险与边界

- 官方热修会更换 content hash 并重写 catalog（`staticmod.py:13-21`）：定位必须每次动态解析，
  **绝不**缓存 hash/偏移常量（铁律 2）。
- bundle 内 TextAsset 的 container 语义（`obj.container`）与编辑器 `containerEntry` 元数据同源，
  复用 `AssetsToolsBackend.ReadContainerMap`，不要新写解析。
- 编辑集只增不改 vanilla 快照；`full` 整文件替换通道暂不在页面暴露（jsonpatch diff 已覆盖，
  减少误用面），服务层能力保留。
- 静态表 JSON 很大（MB 级）：编辑器异步加载 + 树惰性展开（复用 plan-05 JSON 树经验）。
