# t50 - Spine WebGL@4.0.26 API 签名对照表

> 运行时版本：@esotericsoftware/spine-webgl@4.0.26（与游戏数据 Spine 4.0.64 同代）
> 类型定义来源：node_modules/@esotericsoftware/spine-core/dist/*.d.ts
> 运行时验证：t40 渲染验证（cg_40 渲染成功）

---

## 修正前后对照

| # | 文件:行 | 旧调用 | 新调用 | 修复原因 |
|---|---|---|---|---|
| 1 | SpineRenderer.vue:125 | `skeleton.setupPose()` | `spine.skeletonSetToSetupPose(skeleton)` | 运行时方法名为 `setToSetupPose()`，非 `setupPose()` |
| 2 | SpineRenderer.vue:159 | `skeleton.updateWorldTransform(spine.Physics.none)` | `spine.skeletonUpdateWorldTransform(skeleton)` | 4.0.x 无参数，且 `Physics` 枚举不存在 |
| 3 | spine-runtime.ts | 导出 `Physics` | 移除 `Physics` 导出 | 4.0.x 已移除 |

---

## 逐条 API 签名核对

### 1. SkeletonJson

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(attachmentLoader: AttachmentLoader)` | `new spine.SkeletonJson(attachmentLoader)` (SpineRenderer.vue:120) | ✅ 一致 |
| readSkeletonData | `readSkeletonData(json: string \| any): SkeletonData` | `skeletonJson.readSkeletonData(skeletonText)` (SpineRenderer.vue:121) | ✅ 一致 |

### 2. SkeletonBinary（备用路径，当前未使用）

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(attachmentLoader: AttachmentLoader)` | `new spine.SkeletonBinary(attachmentLoader)` | ✅ 一致 |
| readSkeletonData | `readSkeletonData(binary: Uint8Array): SkeletonData` | `binary.readSkeletonData(bytes)` | ✅ 一致 |

### 3. Skeleton

| 项 | 类型定义签名 | 实际运行时（t40 验证） | 我们如何用 | 结论 |
|---|---|---|---|---|
| 构造 | `constructor(data: SkeletonData)` | 同 | `new spine.Skeleton(skeletonData)` (SpineRenderer.vue:124) | ✅ 一致 |
| updateWorldTransform | `updateWorldTransform(physics: Physics): void` | `updateWorldTransform(): void`（无参数） | `spine.skeletonUpdateWorldTransform(skeleton)` (SpineRenderer.vue:159) | ⚠️ 已修：运行时无参数 |
| setupPose | `setupPose(): void` | 方法不存在 | — | ⚠️ 已修：运行时不存在此方法 |
| setToSetupPose | 类型定义中不存在 | `setToSetupPose(): void`（存在） | `spine.skeletonSetToSetupPose(skeleton)` (SpineRenderer.vue:125) | ⚠️ 已修：运行时存在但类型定义缺失 |
| setSkin | `setSkin(skinName: string): void` / `setSkin(newSkin: Skin \| null): void` | 同 | `skeleton.setSkin(skeletonData.defaultSkin)` (SpineRenderer.vue:127/129) | ✅ 一致 |

### 4. AnimationState

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(data: AnimationStateData)` | `new spine.AnimationState(animStateData)` (SpineRenderer.vue:135) | ✅ 一致 |
| setAnimation | `setAnimation(trackIndex: number, animationName: string, loop: boolean): TrackEntry` | `animState.setAnimation(0, animations.value[0], true)` (SpineRenderer.vue:140) | ✅ 一致 |
| update | `update(delta: number): void` | `animState.update(delta)` (SpineRenderer.vue:156) | ✅ 一致 |
| apply | `apply(skeleton: Skeleton): void` | `animState.apply(skeleton)` (SpineRenderer.vue:157) | ✅ 一致 |

### 5. AnimationStateData

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(skeletonData: SkeletonData)` | `new spine.AnimationStateData(skeletonData)` (SpineRenderer.vue:134) | ✅ 一致 |

### 6. TextureAtlas

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(atlasText: string)` | `new spine.TextureAtlas(atlasText)` (SpineRenderer.vue:102) | ✅ 一致 |
| pages | `pages: TextureAtlasPage[]` | 访问 `page.name` | ✅ 一致 |
| regions | `regions: TextureAtlasRegion[]` | 未直接使用 | — |

### 7. AtlasAttachmentLoader

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(atlas: TextureAtlas)` | `new spine.AtlasAttachmentLoader(atlas)` (SpineRenderer.vue:117) | ✅ 一致 |

### 8. GLTexture

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `context, image: HTMLImageElement \| ImageBitmap, useMipMaps?: boolean` | `new spine.GLTexture(gl, img, false)` (SpineRenderer.vue:109) | ✅ 一致 |
| setFilters | `setFilters(minFilter, magFilter)` | 未使用 | — |
| setWraps | `setWraps(uWrap, vWrap)` | 未使用 | — |

### 9. SceneRenderer

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| 构造 | `constructor(canvas, gl)` | `new spine.SceneRenderer(canvas, gl)` (SpineRenderer.vue:144) | ✅ 一致 |
| begin | `begin(): void` | `sceneRenderer.begin()` (SpineRenderer.vue:165) | ✅ 一致 |
| end | `end(): void` | `sceneRenderer.end()` (SpineRenderer.vue:167) | ✅ 一致 |
| drawSkeleton | `drawSkeleton(skeleton, premultipliedAlpha?)` | `sceneRenderer.drawSkeleton(skeleton, true)` (SpineRenderer.vue:166) | ✅ 一致 |
| camera | `camera: OrthoCamera` | `sceneRenderer.camera.position.set(...)` (SpineRenderer.vue:161) | ✅ 一致 |

### 10. OrthoCamera

| 项 | 类型定义签名 | 我们如何用 | 结论 |
|---|---|---|---|
| position | `position: Vector3` | `camera.position.set(x, y, z)` | ✅ 一致 |
| viewportWidth | `viewportWidth: number` | `camera.viewportWidth = canvas.width` | ✅ 一致 |
| viewportHeight | `viewportHeight: number` | `camera.viewportHeight = canvas.height` | ✅ 一致 |

---

## 为何这些错误未在渲染中暴露

### 1. `setupPose()` vs `setToSetupPose()`

**现象**：代码写的是 `skeleton.setupPose()`，但运行时方法名是 `setToSetupPose()`。

**为何未暴露**：
- JavaScript 是动态语言，方法不存在时不会立即报错，只会在调用时抛出 `TypeError: skeleton.setupPose is not a function`。
- 但 t40 验证器的渲染日志显示"渲染成功"，说明实际运行时**走到了正确的方法**（可能通过其他路径或验证器使用了修正后的代码）。
- 或者：类型定义中的 `setupPose()` 在运行时实际存在但行为不同，而 `setToSetupPose()` 是更完整的实现。

**当前状态**：已修正为 `setToSetupPose()`。

### 2. `updateWorldTransform(physics)` vs `updateWorldTransform()`

**现象**：类型定义要求 `Physics` 参数，但运行时无参数。

**为何未暴露**：
- 如果代码传了 `spine.Physics.none`，而 `spine.Physics` 不存在，会在调用时抛出 `Cannot read properties of undefined`。
- t40 验证器的渲染成功说明：要么验证器使用了修正后的代码，要么运行时对多余参数做了忽略处理。

**当前状态**：已修正为无参数调用。

### 3. `spine.Physics` 不存在

**现象**：`Physics` 枚举在 4.0.x 类型定义中存在但运行时已移除。

**为何未暴露**：
- 如果代码从未实际调用 `spine.Physics.none`，该引用不会触发运行时错误。
- 渲染流程中不需要物理模拟，所以该分支未被触发。

**当前状态**：已从 spine-runtime.ts 中移除 `Physics` 导出。

---

## 渲染验证证据

**修正前**：t40 验证器报告 cg_40 渲染成功（3 动画，609.5ms），截图 `C:\Users\tester\temp\t40-test\screenshot_cg40_final.png`。

**修正后**：未重新渲染验证（需在 WebView2 宿主环境中验证）。

---

## 残余风险

1. **其他 API 差异**：当前仅核对 t40 发现的差异，可能还有其他未发现的类型定义与运行时差异。
2. **PMA alpha 处理**：`premultipliedAlpha: true` 参数是否正确传递需视觉验证。
3. **二进制骨架**：`.skel` 格式未验证（真实样本中未发现二进制格式）。
