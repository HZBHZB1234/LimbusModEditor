# 写回验证报告（T1：真实纹理替换端到端闭环）

> 目标：验证「读取 → 修改 → 写回 → 游戏加载」这条**唯一没有真实数据覆盖过的链路**。
> 本文档记录 2026-09-06 在本机（tester，含完整真实数据）执行的验证过程、结果与发现。
> 测试代码：`tests/LimbusModEditor.Format.Tests/RealWriteBackTests.cs`（无样本机器自动跳过）。

## 1. 结论速览

- 真实缓存 bundle 的流纹理（`m_StreamData` 指向 `.resS`）完成 PNG 替换写回：
  像素内联、流指针清零、重打包后 `VerifyBundleReferences` 通过。
- 以真实 Carra2 结构承载并导出：键 = `<缓存外层>/<内层>/<pathId>.<类型表索引>`，
  逐条目 XZ；导出物通过格式探针、可重新导入并逐字节还原。
- 已安装进 `%APPDATA%\LimbusCompanyMods`（启用状态），等待**用户启动游戏**做最终
  游戏内确认（见 §5）。
- **验证过程中发现并修复了一个严重写回缺陷**（见 §4）：此前所有 bundle 写回
  （纹理/对象/字段/Sprite 元数据）的修改内容都会被静默丢弃，产物等于原包重压缩。

## 2. 验证对象（真实样本）

| 项 | 值 |
|---|---|
| 缓存 bundle | `<Unity缓存>/01de8dd39e3f135d24fa5e307c996d81/5e6bda62639c3ab3967403f5eebbbaaa/__data` |
| 内嵌 SerializedFile | `CAB-14ff94ebf9666a49b236156be52763c1` |
| 对象 PathId | `-4132312488608918971` |
| 对象类型 | Texture2D（类型表索引 0，类 ID 28） |
| 纹理 | `Fx_T_Shape_LineFlash_01`，128×128，DXT1（格式 10） |
| 原始流 | `archive:/CAB-14ff94eb…/CAB-14ff94eb….resS`（offset/size 流式存储） |
| 原始 bundle SHA256 | `AB48A74428E83FE437550C4EC00DCBE23758CFE5A3F026CFB156B48C672C67DC` |
| 写回后 SHA256 | `599CBA8A628106AC3D398AE8801F7F69843FC25366775A754D2C78673514E345` |

替换内容：与原图平均色距离最远的高可见度纯色（本次为 `#FFFFFF` 白色），
尺寸与原纹理一致，格式保持 DXT1（由后端按原 `m_TextureFormat` 重新编码）。

## 3. 执行的验证步骤与断言

全部写操作只发生在 `artifacts/realdata-verify/` 副本上，Unity 缓存原件只读。

1. **读取**：`InspectBundle` 枚举对象 → `ReadBundleObjectFields` 确认
   `m_StreamData.path` 非空（流纹理）→ `ReadTexture` 走 `.resS` 流读出像素。
2. **修改**：`ReplaceBundleTextureFromPng` —— PNG 按原格式重新编码 →
   写入内联像素数组 → `m_StreamData` 的 path/offset/size 清零 → 重打包
   （按原压缩类型 LZ4/LZ4HC）。
3. **写回校验**：
   - 重打包前后对象表（container/pathId/类型）逐项一致；
   - `VerifyBundleReferences(原, 改)` 全部依赖可解析（`Ok = true`）；
   - 重读字段树：`m_StreamData.path == ""`、`size == 0`、内联数组 32768 字节；
   - `ReadTexture` 解码后的图像与替换图平均差 ≤ 8/40（DXT 有损容差），
     与原图平均差 > 25（肉眼可辨）；
   - `ReadBundleSerializedObject` 取写回后对象的原始序列化字节，其类型表索引
     与 vanilla bundle 上同一对象完全一致（真实加载器按此做类型校验，见 §4）。
4. **Carra2 承载与导出**：
   - 键：`01de8dd39e3f135d24fa5e307c996d81/5e6bda62639c3ab3967403f5eebbbaaa/-4132312488608918971.0`
     （第三段后缀 = 类型表索引，与 LCTA `launcher/patch.py` 的 `obj.type_id`
     比对语义一致）；
   - 载荷：写回后对象的原始字节，XZ（`FORMAT_XZ`，与加载器解压方式一致）；
   - 附带 `carra.json` 标记（与真实模组布局一致）；
   - 导出物 SHA256 `45F5D0BAC7375B7F2DC26DA2BE463FCEDE83D30A2837EFD2D41F32EDF8AD6E22`；
   - 断言：格式探针识别 ✓、重新导入条目/资产键一致 ✓、解压后逐字节等于写回
     对象数据 ✓、条目为 XZ 流 ✓、缓存对齐（键仍指向存在的缓存 bundle）✓。
5. **安装**：复制到 `%APPDATA%\LimbusCompanyMods\LME-写回验证-5e6bda62.carra2`
   （启用状态；现有其他模组均为 `_disable`，不会互相干扰）。

## 4. 发现并修复的写回缺陷（重要）

**现象**：首轮验证中，替换后的 bundle 重打包成功（SHA 变化）但重新读取时
`m_StreamData` 与像素数组**保持原样**——修改被静默丢弃。

**根因**（对照 AssetsTools.NET v3 源码，`AssetBundleFile.cs`）：
`AssetFileInfo.SetNewData(AssetTypeValueField)` 与
`AssetBundleDirectoryInfo.SetNewData(...)` 只登记 **Replacer**，真正应用 Replacer
的是**未压缩 `Write` 路径**；而 `Pack(writer, compType, …)` 仅重新压缩当前
`DataReader`（原始数据），**完全不处理 Replacer**。旧实现
`WritePackedBundle` 直接 `Pack`，导致所有 bundle 内修改丢失。

**修复**（`src/LimbusModEditor.Formats.Unity/AssetsToolsBackend.cs` 的
`WritePackedBundle`，覆盖全部 bundle 写回方法）：
1. `bundle.file.Write(writer, -1)` 先写**未压缩** UnityFS（此步应用全部 Replacer）；
2. 重新加载该未压缩产物，`Pack(writer, bundle.originalCompression)` 按原压缩类型
   （真实 bundle 为 LZ4/LZ4HC）重新打包。

同时新增适配层 API `AssetsToolsBackend.ReadBundleSerializedObject`：读取 bundle 内
对象的原始序列化字节 + 类型表索引（Carra2 键与加载器校验所需事实）。

## 5. 游戏内最终确认（待用户执行）

本报告的机器可验证部分已全部通过；「游戏内可见变化」需要真实启动游戏：

1. 通过 LCTA 启动器正常启动游戏（加载器会把模组对象补丁进缓存 bundle，
   并自动备份 `__original`）。
2. 进入战斗后观察技能/攻击特效中的线条闪光（`Fx_T_Shape_LineFlash_01` 参与的
   特效）：模组生效时该纹理显示为**纯白色高亮块状**，与原图案明显不同。
3. 验证完成后移除模组：
   - 把 `%APPDATA%\LimbusCompanyMods\LME-写回验证-5e6bda62.carra2` 重命名加
     `_disable` 后缀或直接删除；
   - 加载器下次启动时会依据 `__original` 自动还原缓存 bundle
     （`launcher/patch.py` 的 `cleanup_assets` 逻辑）。

## 6. 复现方法

```bash
# 只验证（不安装；产物写入 artifacts/realdata-verify/）
dotnet test tests/LimbusModEditor.Format.Tests --filter "FullyQualifiedName~RealWriteBackTests"

# 验证并安装进真实模组目录（游戏内确认用）
LME_REALDATA_INSTALL=1 dotnet test tests/LimbusModEditor.Format.Tests --filter "FullyQualifiedName~RealWriteBackTests"
```

产物：`source.bundle`（缓存副本）、`modified.bundle`（写回产物）、
`replacement.png`（替换图）、`LME-写回验证.carra2`（导出模组）、
`verify-report.json`（本次运行的机器可读报告，含全部哈希）。

## 7. 覆盖范围与边界

- 本轮覆盖：**已有对象的原始数据替换**（Texture2D 纹理）经 Carra2 通道的完整闭环。
- 未覆盖（继续遵守「不猜测」边界）：SpriteAtlas 网格重写、Mesh/AnimationClip 写回、
  Bank/FSB 音频替换的游戏内验证（机制同链路，待真实需求驱动）、
  「新增对象」（`patch.py` 的 added-object 分支）。
- `modified.bundle`（编辑器自身的 bundle 写回产物）也通过 `InspectBundle`/
  `ReadTexture`/引用校验验证可读；但游戏实际加载的是 Carra2 载荷打回 vanilla
  bundle 的结果，两者均已覆盖。
