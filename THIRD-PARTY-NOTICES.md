# 第三方依赖声明 / Third-Party Notices

本仓库中包含以「vendor（ vendored，源码直接复制）」方式引入的第三方代码，集中存放于
`third_party/` 目录。以下逐项说明来源、许可证、引入方式及本仓库对其所做的改动。

---

## 1. spine-csharp（Spine 骨骼动画核心运行时，C#）

| 项目 | 内容 |
| --- | --- |
| 上游仓库 | [EsotericSoftware/spine-runtimes](https://github.com/EsotericSoftware/spine-runtimes) |
| 引入目录 | `third_party/spine-csharp/`（`src/` 下纯 C# 核心源码 + `LICENSE` + `README.md`） |
| 对应分支 | `4.0` |
| 对应提交 | `425ce416bb218b28caeec47b317aa57cd7140375` |
| 兼容 Spine 版本 | 由 `spine-csharp` 导出的 **Spine 4.0.xx** 数据（JSON / 图集 atlas） |
| 许可证 | **Spine Runtimes License Agreement**（见 `third_party/spine-csharp/LICENSE`） |

### 本仓库对其所做的改动
**未对 vendored 源码做任何修改。** `third_party/spine-csharp/src/**/*.cs` 以「原样包含」方式被
`src/LimbusModEditor.SpineRuntime/LimbusModEditor.SpineRuntime.csproj` 通过
`<Compile Include="..\..\third_party\spine-csharp\src\**\*.cs" />` 编译进独立类库。
仅通过 csproj 的 `NoWarn` 抑制 vendored 源码在 `Nullable=disable` 下产生的 `CS8632` 警告，
类库自身代码保持零警告。

### 许可证关键义务（务必知悉）
依据 Spine Runtimes License 与 `third_party/spine-csharp/README.md`：

- 你可以**免费**将 Spine Runtimes 集成进你的软件；但**使用你软件的最终用户必须各自持有
  自己的 [Spine 授权](https://esotericsoftware.com/spine-purchase)**。你需要让最终用户知晓这一要求。
- 若要将包含 Spine Runtimes 的软件分发给**没有 Spine 授权**的第三方，你需要在集成时**已持有
  Spine 授权**；且不得允许他人修改该运行时或用它创建新软件（他人如需，须自行购买 Spine 授权）。

### ⚠️ 最终用户须自持 Spine Editor 授权
本项目（`LimbusModEditor`）作为《Limbus Company》模组编辑器，允许用户导入/解析由
**Spine Editor** 导出的骨架与图集资源。根据 Spine 许可条款，**导入并渲染这些资源的最终用户
（模组作者）必须自己持有合法的 Spine Editor 授权**。本项目仅作为工具提供解析与离线渲染能力，
不代表、也不替代用户应履行的 Spine 授权义务。请在软件界面或文档中向最终用户明确提示此项要求。

---

## 2. SkiaSharp（2D 图形渲染）

| 项目 | 内容 |
| --- | --- |
| 上游项目 | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| 引入方式 | NuGet 包 `SkiaSharp` `3.119.0`（通过 `PackageReference` 引用，**非 vendored**） |
| 许可证 | [MIT License](https://github.com/mono/SkiaSharp/blob/main/LICENSE.md) |

`SkiaSharp` 仅作为 `LimbusModEditor.SpineRuntime` 类库与对应测试工程的 NuGet 依赖出现，
未被复制进仓库源码。其许可义务随 NuGet 包自动满足（MIT，宽松许可）。

---

## 目录与文件归属速查
- `third_party/spine-csharp/**` — Spine Runtimes License（vendored，未改）
- `src/LimbusModEditor.SpineRuntime/**` — 本仓库自有代码（MIT 风格，见仓库主许可证）
- `tests/LimbusModEditor.SpineRuntime.Tests/**` — 本仓库自有测试代码
- `THIRD-PARTY-NOTICES.md` — 本文件

如上游许可证文本与本条说明冲突，以 `third_party/spine-csharp/LICENSE` 中的官方原文为准。
