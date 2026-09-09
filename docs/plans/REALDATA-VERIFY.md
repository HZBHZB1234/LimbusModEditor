# 真实数据验证记录（plan-01 / plan-08）

本文件记录**真实游戏数据**上的修复前后对照证据。探针测试：
`tests/LimbusModEditor.Format.Tests/RealPreviewCoverageTests.cs`（`LME_SCAN_BUDGET` 门控，默认 12 个 bundle；
无真实缓存的机器自动跳过），矩阵同时写入 `%TEMP%/lme-preview-coverage.txt`。

本机环境（2026-09 实测）：

| 项 | 值 |
|---|---|
| 游戏目录 | `C:\Program Files (x86)\Steam\steamapps\common\Limbus Company` |
| Unity 缓存根 | `%LocalAppData%Low\Unity\ProjectMoon_LimbusCompany`（1459 个 `<outer>/<inner>/__data`） |
| catalog | `<游戏>/LimbusCompany_Data/StreamingAssets/aa/catalog.bin`（5,307,548 字节；本机无 catalog_S1.bin） |
| 静态 bundle | catalog 中唯一：`static_s1_0_assets_all_edb72aecf54cf3d92152f862727b6819.bundle`，外层键 `64bd0105e9544bef32ddae650b8bcb26`（**每次动态解析，不硬编码**） |

## plan-01：预览链路修复前后矩阵（同一 12 个 bundle 抽样）

| 类型 | 修复前（基线，`MainWindow.UpdatePreviewAsync` 的调用方式） | 修复后 |
|---|---|---|
| Texture2D (class 28) | OK：697×314 PNG（193,414 字节） | OK（不变） |
| Sprite (class 213) | **FAIL**：`ReadTexturePng(spritePathId)` 返回 null（Sprite ≠ Texture2D） | **OK**：622×182 PNG，裁剪区域 == `m_RD.textureRect`（纹理 697×314，格式 4/RGBA32） |
| TextAsset (class 49) | **FAIL**：class 49 未映射 → `AssetType.Unknown`；且 `TextPreviewService` 直读 `SourcePath`（缓存 `__data` 二进制） | **OK**：`m_Name=fairy_ism`，16,859 字节，正文 UTF-8 可解码（spine 骨架 JSON） |
| AudioClip (class 83) | **FAIL**：UI 只支持 `fsb/` 前缀的 Bank 音频 | 实现 `ReadBundleAudioClipData`（三种已验证负载形态 + fail fast）；**本机缓存实测无 class 83 样本**（600 个 bundle 扫描内 0 个：游戏音频在 FMOD bank 里），因此真实矩阵该行标注「无样本」，合成单测覆盖 |

### 真实字段结构证据（探针 dump，非猜测）

```text
=== TextAsset (class 49) ===            m_Name : string, m_Script : string（类型树为 string）
=== Sprite (class 213) ===
  m_Rect            : x=0 y=0 width=697 height=314          ← 逻辑尺寸
  m_RD.texture      : PPtr m_FileID=0 m_PathID=-1327712819131583028   ← 同文件 Texture2D
  m_RD.textureRect  : x=39.076122 y=71.07612 width=621.8478 height=181.84775  ← 图集内实际像素区域
  被引用纹理         : 697×314 format=4（RGBA32）bytes=875432
=== Texture2D (class 28) ===
  m_Width=697 m_Height=314 m_TextureFormat=4 m_MipCount=1
  m_StreamData.path = archive:/CAB-…/CAB-….resS（流式像素）
```

**结论（与 plan-01 原文的偏差，以实测为准）**：裁剪使用 `m_RD.textureRect`（缺失时回退 `m_Rect`），
因为真实样本里 `m_Rect` 是 Sprite 逻辑尺寸（含透明留白），`textureRect` 才是纹理内像素区域；
两者在无留白时相同，plan-01 写的「尺寸 == m_Rect」在真实图集样本上不成立。

## plan-08：静态 bundle 定位（真实 catalog）

| 步骤 | 实测结果 |
|---|---|
| catalog 名匹配 | 命中 `static_s1_0_assets_all_edb72aecf54cf3d92152f862727b6819.bundle` |
| 内层键 | `edb72aecf54cf3d92152f862727b6819`（= 缓存内层键） |
| 外层键 | `64bd0105e9544bef32ddae650b8bcb26`（记录区 +0x10 长度前缀） |
| 缓存命中 | **本机未命中**（缓存里没有该内层键目录）→ 定位器给出「请启动一次游戏生成缓存后重试」的可执行提示，不抛异常 |
| 扫描打标记 | 集成测试用真实 bundle 字节 + 合成 catalog 验证：`staticBundle=true` 写入索引并随回灌恢复 |
| 默认过滤 | `AssetSearchQuery` 默认隐藏静态资源，`ShowStaticTables: true` 可见 |

## 复跑方式

```text
dotnet test LimbusModEditor.slnx --nologo --filter "FullyQualifiedName~RealPreviewCoverageTests"
dotnet test LimbusModEditor.slnx --nologo --filter "FullyQualifiedName~AssetPropertyServiceTests"
dotnet test LimbusModEditor.slnx --nologo --filter "FullyQualifiedName~StaticBundleLocatorTests"
dotnet test LimbusModEditor.slnx --nologo --filter "FullyQualifiedName~AssetPreviewRegistryTests"
dotnet test LimbusModEditor.slnx --nologo --filter "FullyQualifiedName~RealWorkbenchGateTests"
```

矩阵文件：`%TEMP%/lme-preview-coverage.txt`。

## plan-05 / 06 / 07 / 08 审查门实测（波次 C）

| 门 | 实测结果 |
|---|---|
| 预览 Kind 矩阵（真实缓存 12 bundle） | Texture→Image、Sprite→Image、TextAsset→Text/Json、MonoBehaviour→Rows；未知二进制→Hex（`AssetPreviewRegistryTests.Real_cache_samples_map_to_expected_preview_kinds`） |
| 波形与十六进制 | 合成 16-bit PCM → 包络峰值/时长正确；非 WAV 明确给原因；十六进制 4 KiB 截断 + ASCII 边栏（`AssetPreviewRegistryTests`） |
| 音频 bank 样本试听 | 真实 bank 目录 → 音频 bank → SNDH 切片 → FSB5 → **FMOD 2.2.26 解码成功**（WAV RIFF/WAVE 头、包络非空、时长 > 0）；越界子样本索引明确报错（`RealWorkbenchGateTests`） |
| FMOD 2.x 兼容修复 | 游戏自带 `fmodstudio.dll`（2.2.26）要求 `FMOD_System_Create(system, headerversion)`；此前单参数调用返回 `FMOD_ERR_HEADER_MISMATCH(20)`。现按 DLL 文件版本推导 `FMOD_VERSION`，失败再退回 1.x 单参数调用 |
| .rebank 结构 | `rebank.json` + `{整数索引}/{*.wav}`，`base_bank` 为纯文件名（无路径分隔符），wav 数 > 0，回读往返一致（与 LCTA `webutils/bank/rebank.py` 的 `iter_rebank_wavs` 解析规则一致） |
| lang 真实表回放 | 真实 `LLC_zh-CN`（config.json 活动语言）：修改 `AbDlg_Faust.json` 一个值 → 导出补丁（键为相对路径、ops 合法 RFC6902）→ `TextDiffService.Apply` 回放到 vanilla == 修改后文档；还原后编辑集为空 |
| 静态 bundle 默认过滤 | 资源工作台默认视图隐藏 `staticBundle=true` 资产，勾选「显示静态数据表」后可见；索引持久化后回灌仍保持标记（`StaticBundleLocatorTests`） |
| 不写游戏目录 | 工作台页面写盘点全部为：临时文件、用户选择的对话框路径、项目目录（sources/banks、banks/）、程序 cache/、模组目录（对话框默认值）；`catalog`/Unity 缓存/游戏目录无任何写入（唯一例外是文本工作台显式警告后的「直接应用到 lang 目录」） |

