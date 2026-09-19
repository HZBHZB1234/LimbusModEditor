# 第三方依赖声明 / Third-Party Notices

## System.IO.Hashing

NuGet `System.IO.Hashing` 8.0.0（Microsoft/.NET），用于流式 IEEE CRC32 校验。
许可证为 [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)，通过 PackageReference 引用，未修改上游源码。

本仓库**当前不含任何 vendored（源码直接复制）的第三方代码**：原 `third_party/spine-csharp/`
（Spine 骨骼动画 C# 运行时）已于 2026-09-19 随 `src/LimbusModEditor.SpineRuntime/` 工程一并
移出仓库——其许可证为 **Spine Runtimes License Agreement**，属非开源许可，不宜随仓库公开分发。
源码已归档至仓库外 `_lme-removed-20260919/`，历史提交中亦已抹除。

以下逐项说明现存的第三方依赖来源、许可证与引入方式。

---

## 1. spine-ts / spine-webgl（Spine 骨骼动画前端运行时）

| 项目 | 内容 |
| --- | --- |
| 包名 | `@esotericsoftware/spine-webgl` |
| 精确版本 | **4.0.26**（锁定 `4.0.x`；游戏数据为 Spine 4.0.64，同代兼容） |
| npm | https://www.npmjs.com/package/@esotericsoftware/spine-webgl |
| 源码仓库 | [EsotericSoftware/spine-runtimes](https://github.com/EsotericSoftware/spine-runtimes) |
| 依赖链 | `@esotericsoftware/spine-webgl@4.0.26` → `@esotericsoftware/spine-core@4.0.26`（同一许可证） |
| 许可证 | **Spine Runtimes License Agreement**（https://esotericsoftware.com/spine-runtimes-license，**2025-04-05 版为权威**；npm 包内 `LICENSE` 文件为 2019-05-01 旧版，措辞与官网一致、仅日期不同，一律以官网 2025-04-05 版为准） |
| 版权 | Copyright (c) 2013-2025, Esoteric Software LLC |
| 引入方式 | npm 包（前端工程 `src/LimbusModEditor.Web/` 的 `package.json` 依赖，**非 vendored**） |

### 包内 LICENSE 原文（已读取，2026-09-16）

来源：`src/LimbusModEditor.Web/node_modules/@esotericsoftware/spine-webgl/LICENSE`（只读）。
- 版本：**2019-05-01 版**（`Last updated May 1, 2019. Replaces all prior versions.`）
- 版权：`Copyright (c) 2013-2019, Esoteric Software LLC`
- 关键条款原文：
  > Integration of the Spine Runtimes into software or otherwise creating derivative works of the Spine Runtimes is permitted under the terms and conditions of Section 2 of the Spine Editor License Agreement: http://esotericsoftware.com/spine-editor-license
  > Otherwise, it is permitted to integrate the Spine Runtimes into software or otherwise create derivative works of the Spine Runtimes (collectively, "Products"), provided that **each user of the Products must obtain their own Spine Editor license** and **redistribution of the Products in any form must include this license and copyright notice**.
- 与官网 2025-04-05 版措辞一致（仅日期与版权年份不同），**以官网 2025-04-05 版为权威**（spine-engineer 已确认）。

### 再分发要求（务必知悉）

- 允许随包分发；**必须包含许可证与版权声明**（随包附带或指向
  https://esotericsoftware.com/spine-runtimes-license 的声明均满足）。
- **每个最终用户需持自己的 Spine Editor license**。
- 与铁律 §3-5（不整段复制 GPL 代码）**不冲突**：Spine Runtimes License 为 Esoteric Software 自有许可，非 GPL。

### ⚠️ 最终用户须自持 Spine Editor 授权
前端以 spine-webgl 渲染用户导入的 Spine 骨架/图集资源，**导入并渲染这些资源的最终用户
（模组作者）必须自己持有合法的 Spine Editor 授权**。本项目仅作为工具提供解析与渲染能力，
不代表、也不替代用户应履行的 Spine 授权义务。请在软件界面或文档中向最终用户明确提示此项要求。

### 随包方式（定义，执行需报备 captain）

**目标**：发布产物 `artifacts/publish-win-x64/` 内必须实际携带许可证与版权声明。

**方案**（前端构建期拷贝，随 dist/ 发布）：
1. 前端工程 `src/LimbusModEditor.Web/` 的构建（Vite）把
   `node_modules/@esotericsoftware/spine-webgl/LICENSE` 拷贝到 `dist/licenses/spine-webgl-LICENSE`
   （实现方式：`public/licenses/` 目录或构建脚本拷贝，二选一，由 t11/t13 定）
2. 发布时 `scripts/publish.ps1` / `lme.py publish` 把 `dist/` 复制进 `artifacts/publish-win-x64/wwwroot/`，
   许可证自然随包，产物内路径：`artifacts/publish-win-x64/wwwroot/licenses/spine-webgl-LICENSE`
3. 版权声明（`Copyright (c) 2013-2025, Esoteric Software LLC`）包含在该 LICENSE 文件中，无需单独文件

**用户可见义务**（「每个用户需自持 Spine Editor 许可证」）：
- 在关于对话框 / 设置页显示 Spine 授权提示（措辞见本文件 §1「⚠️ 最终用户须自持 Spine Editor 授权」）
- 交付报告（`docs/FINAL-DELIVERY.md` 或 t21 交付报告）写明该义务

**⚠️ 未完成验收（由 t21 集成时验证）**：
- [ ] 产物内实际携带许可证：`artifacts/publish-win-x64/wwwroot/licenses/spine-webgl-LICENSE` 文件存在
  （当前 t13 前端工程尚未落地，dist/ 未构建，**本条验收尚未完成**，由 t21 在集成时核对）

---

## 目录与文件归属速查
- `third_party/spine-csharp/**` — **已移出仓库**（2026-09-19）。原为 Spine Runtimes License（vendored，未改），随 `SpineRuntime` 工程一并删除，历史提交中亦已抹除。
- `src/LimbusModEditor.SpineRuntime/**` — **已删除**（前端化重构后不再需要）。原为本仓库自有代码。
- `tests/LimbusModEditor.SpineRuntime.Tests/**` — **已删除**（同上）。
- `src/LimbusModEditor.Web/node_modules/@esotericsoftware/spine-webgl/**` — Spine Runtimes License（npm 包，git 忽略，随包分发时由构建产物携带；权威文本见官网 https://esotericsoftware.com/spine-runtimes-license）
- `artifacts/publish-win-x64/wwwroot/licenses/spine-webgl-LICENSE` — 随包许可证（**待 t13 落地后由 t21 验证存在**）
- `THIRD-PARTY-NOTICES.md` — 本文件

如上游许可证文本与本条说明冲突，以官网 https://esotericsoftware.com/spine-runtimes-license 的原文为准。
