# Plan 05 — 预览强化：多格式预览管线

> 覆盖需求 7：「强化资源预览功能，允许预览各种格式的资源。」

前置：plan-01 已修复「缓存 bundle 内资源读不到/预览失败」的读取层，并建立
`AssetPropertyService`。本计划把「三种预览形态」升级为**可扩展的预览提供者管线**。

## 1. 现状（证据）

- 预览形态硬编码三种：图像缩略图（150px 高 Border）/ 只读文本框 / 音频试听按钮
  （`MainWindow.xaml:270-285` + `UpdatePreviewAsync`，`MainWindow.xaml.cs:980-1043`）。
- 已有但未进预览区的能力：`HexPreviewWindow`（4KiB 十六进制转储）、`BankInspectorWindow`
  （FSB 结构）、`ObjectSummaryWindow`（Mesh/Animation/Font 摘要）、`UnityFieldEditorWindow`
  （字段树，编辑向）——全部是独立模态窗口。
- 解码能力现状（Formats.Unity）：Texture2D（RGB24/RGBA32/BGRA32/DXT1/DXT5 + R8/RGB565/
  RGBA4444/RG16 等，含 .resS 流）；ETC/ASTC 明确不支持（不伪造）。

## 2. 目标形态（按资源类型）

| 类型 | 预览形态 | 依赖 |
|---|---|---|
| Texture2D | 图像 + 缩放/平移/棋盘格透明底 + 尺寸/格式信息行 + 原图↔替换图切换 | 已有解码 |
| Sprite | 图集裁剪子图（同上交互）+ rect 叠加显示 | plan-01 B |
| Text/JSON | 文本（行号、等宽字体）+ JSON 树视图切换 + 键搜索 | plan-01 A |
| AudioClip | 试听按钮 + **波形图**（解码 WAV → PCM → Polyline 包络）+ 时长/采样率/声道 | plan-01 C + FMOD DLL |
| Bank FSB | FSB 结构卡（codec/样本表/偏移），单样本试听 | `Fsb5Parser` 已有 |
| Mesh/Animation/Font | 摘要卡（复用 `UnityObjectSummaryBuilder`，含复制/导出 JSON） | 已有 |
| MonoBehaviour | 只读字段树（复用 `ReadBundleObjectFields`，展开/折叠，值高亮已修改字段） | 已有 |
| 未知/二进制 | 十六进制转储（内嵌，替代弹窗）+ 「无专用预览」说明 | `HexPreviewWindow` 逻辑复用 |

明确**不做**：ETC/ASTC 解码（无可再分发库，维持不猜测）；Mesh 3D 渲染（先摘要；
真实样本驱动后续）；FMOD 波形只对已解码 WAV 绘制（不解码ogg裸流）。

## 3. 实施步骤

1. **提供者接口**（`Application/Assets/Preview/`）：

   ```csharp
   public interface IAssetPreviewProvider
   {
       bool CanPreview(AssetRecord asset);
       Task<AssetPreview> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken ct);
   }
   public sealed record AssetPreview(string Kind, object? Visual, string? Text, string InfoLine, ...);
   ```

   注册表按顺序尝试（Texture → Sprite → Audio → Text → MonoBehaviour → Mesh/… → Hex 兜底），
   `MainWindow`/`AssetsWorkbenchPage` 的 `UpdatePreviewAsync` 改为查注册表（保留
   `_previewGeneration` 代际守卫与后台线程模式）。
2. **UI 容器**：预览区改为 `ContentControl` + 按Kind切换的 DataTemplate；
   信息行 `PreviewInfoText` 保留。图像模板含 `ScrollViewer`+`ScaleTransform`
   （滚轮缩放、拖拽平移、双击复位）；文本模板加行号（`ItemsControl` 或双列 TextBlock）。
3. **波形绘制**：解码 WAV（既有 `DecodeFsbToWaveAsync` 产物）解析 PCM → 降采样包络
   （每像素取 min/max）→ `Polyline`；播放时用 `MediaPlayer.Clock` 画游标（可后置）。
4. **JSON 树**：`System.Text.Json` 解析 → `TreeView`（键+类型+值截断，点击定位原文本行）；
   解析失败回退纯文本（不猜修复）。
5. **十六进制内嵌**：把 `HexPreviewWindow` 的转储生成逻辑抽为服务（4KiB 截断 + 哈希），窗口保留。
6. **右键菜单/双击动作同步**：双击动作按「有专用预览的类型」优先预览，其次既有默认动作
   （`ActivateDefaultAction`）；「十六进制预览」「FSB 结构」等菜单项保留（弹窗作为深度工具）。
7. **测试**：合成样本单测（每个 provider 的 Kind 判定 + 输出契约）+ plan-01 的
   真实样本覆盖矩阵扩展一列「预览 Kind」。

## 4. 验收标准（审查门）

- [ ] 真实缓存中各类型抽选 ≥1 个：Texture/Sprite/Text/JSON/AudioClip/MonoBehaviour/Mesh/Font/未知二进制，
      预览区显示对应形态且信息行正确。
- [ ] 图像支持缩放/平移/棋盘格；替换后可切换原图↔替换图。
- [ ] 音频波形可见、试听正常、切选/关窗停止并删临时文件（现有约束不回退）。
- [ ] 未知类型不空白：显示说明 + 十六进制内嵌视图。
- [ ] 40 万级列表连续快速切换选中项无卡顿、无过期预览覆盖（代际守卫仍生效）。
- [ ] build + test 全绿；ROADMAP P3.4 状态更新。

## 5. 风险与边界

- 大 JSON（`BattleSpeechBubbleDlg.json` 359KB 量级、Skills 类）树构建要惰性/异步，防止 UI 冻结。
- 波形绘制只对 ≤10MB WAV 全量绘制，超过则降采样率说明截断。
- 预览提供者全部只读；任何「看起来能编辑」的交互不得出现（编辑仍走既有替换/字段编辑管道）。
