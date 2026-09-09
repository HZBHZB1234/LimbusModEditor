# Plan 07 — 文本（lang）工作台页面（新页面）

> 覆盖需求 9：「文本资源以明文在特殊目录，有别于其他文件。我们应该新建一种页面，用于编辑文本。
> 具体目录可以参考 游戏目录\LimbusCompany_Data\lang 下，config.json 中指向的文件夹。」

## 1. 事实依据（本机实测 + 真实加载器）

- lang 根：`<游戏>/LimbusCompany_Data/lang`；`config.json` = `{"lang":"LLC_zh-CN","titleFont":"","contextFont":""}`
  —— `lang` 字段指向**活动语言目录**（本机 `LLC_zh-CN`）。
- 活动语言目录布局（实测）：根级数百个 `<主题>.json` + 子目录（`StoryData/` 920 文件、
  `PersonalityVoiceDlg/` 187、`BattleAnnouncerDlg/` 61、`BgmLyrics/` 15、`EGOVoiceDig/` 14、
  `Info/` 2、`Font/` 0）。另有翻译组目录（LLc-CN-LCTA、OurPlayHanHua 等）与 `search.py`、
  `T_zh-CN` 等——页面只处理 config.json 指向的目录 + 根级 config.json 本身。
- 加载器语义（LCTA `launcher/changes.py:24-52`）：模组目录中启用 `*.json` 若含 `patchs` 键
  （键=相对 lang 根路径，值=RFC6902 ops）→ 启动时对对应 lang 文件应用（先 `.bak` 备份），
  退出还原。**编辑器产出这种补丁文件**，不直改 lang 目录。
- 编辑器既有能力：`LangTextPatchService`（`ReadActiveLanguage` 读 config.json；`GenerateFromDirectories`
  目录差分；`Write` 产出补丁文档；`ApplyToDirectory` 显式应用）、`TextDiffService`（RFC6902）、
  `LangTextModControl`（过渡期页面：选两个目录生成补丁 / 应用补丁，无浏览无编辑）。

## 2. 页面设计（复用资源工作台布局骨架）

```
[活动栏 48][共享侧边栏 220][文本浏览列 *][splitter][预览/编辑列]
```

- 活动栏 📝「文本工作台」（plan-02 过渡页转正）。
- **数据源**：自动定位 lang 根（共享配置游戏目录拼接）；读取 `config.json` 显示活动语言；
  目录树 = `lang/config.json` + 活动语言目录（子目录逐层、根级文件平铺）；
  其余语言目录默认隐藏（下拉切换语言目录可选，只读浏览）。
- **浏览列**：文件搜索（文件名 + 键/值全文搜索，防抖+后台线程，模式照抄资源搜索）、
  文件列表（名称/大小/键数/已修改标记）。
- **预览/编辑列**：选中文件 → 键值表（JSON 顶层或嵌套展平，键搜索、值内联编辑）
  与原始 JSON 双 Tab；顶部「保存修改」（进内存编辑集）、「还原此文件」。
- **编辑集与导出**：
  - 编辑集 = 相对路径 → 修改后 JSON 文本（会话内存 + 项目内 `texts/` 暂存，可随项目保存）；
  - vanilla 基线 = 首次编辑该文件时快照的 lang 目录原文（存 `<程序目录>/cache/lang-vanilla/`）；
  - 「导出 lang 补丁」：编辑集文件逐个复制到临时 `vanilla/`（快照）与 `modified/`（改后）目录 →
    `LangTextPatchService.GenerateFromDirectories` → `Write` 到模组目录（如 `<项目名>-lang.json`）；
    报告每个文件的 op 数与「新文件无法以补丁承载」诊断（服务已有该诊断）；
  - 高级入口「直接应用到 lang 目录」（`ApplyToDirectory`）保留，但加显著警告：
    真实加载器负责 `.bak` 备份/还原，直接应用会改动游戏文件。
- 键编辑约束：只改**值**与新增键（RFC6902 replace/add）；删除键提供确认；数组内嵌套值
  通过 JSON 树定位（`TextDiffService` 生成 op）。

## 3. 实施步骤

1. `Application/Texts/LangTextWorkbenchService.cs`：目录树枚举（config.json 活动语言解析复用
   `ReadActiveLanguage`）、文件索引（键数、搜索）、编辑集管理（快照/暂存/项目持久化）。
2. `App/TextWorkbenchPage.xaml(.cs)`：布局（树+列表+编辑列+splitter）、键值表编辑器、
   搜索、导出/应用动作。替换 plan-02 中过渡承载的 `LangTextModControl`；
   `LangTextMod_Click` 旧入口删除。
3. 项目持久化：`ModProject` 增加轻量 `TextEdits` 段（相对路径→暂存文件引用），
   或复用 Sources+Metadata 机制（实施时取改动最小者，避免破坏 SchemaVersion 迁移）。
4. 侧边栏/导出思路文案联动：`ExportIdeaKind.LangText` 路由改 `ShowPage("text")`。

## 4. 验收标准（审查门）

- [ ] 真实 lang 目录冒烟：活动语言识别为 `LLC_zh-CN`；目录树含 StoryData 等 7 个子目录与根级文件；
      StoryData 920 文件全部可索引。
- [ ] 修改 `AbDlg_Faust.json` 某值 → 导出补丁 → 生成的 JSON `patchs` 键路径正确、ops 为合法 RFC6902；
      用 `TextDiffService.Apply` 回放到 vanilla 快照可还原修改。
- [ ] 补丁文件放进真实模组目录后与 `changes.py` 语义一致（人工核对：相对路径含子目录时键使用 `/`）。
- [ ] 默认不写游戏 lang 目录；「直接应用」有警告且成功路径有 `.bak` 行为说明。
- [ ] 大文件（`BattleSpeechBubbleDlg.json` 359KB、`Skills.json` 292KB）打开/搜索不冻结 UI（防抖+后台）。
- [ ] build + test 全绿（服务层单测：树枚举/编辑集/导出往返）；USAGE 新节。

## 5. 风险与边界

- 只处理 config.json 指向的活动语言目录；其他翻译组目录（LLC_ZH、ourplay 等）不索引不展示。
- `Font/` 空目录等边界不报错；非 JSON 文件（`search.py`）不在活动语言目录内，天然排除。
- 编码统一 UTF-8（无 BOM）读写；检测到非 UTF-8 JSON 明确报错不猜。
- 编辑集快照占缓存空间：`lang-vanilla/` 只快照被编辑的文件，提供清理入口（设置或页面内）。
