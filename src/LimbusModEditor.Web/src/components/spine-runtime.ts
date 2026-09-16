// Spine WebGL 运行时封装
// 版本：@esotericsoftware/spine-webgl@4.0.26（与游戏数据 Spine 4.0.64 同代）
// 许可：Spine Runtimes License Agreement（随包分发，每位用户须自持 Spine Editor 许可证）
//
// 注意：node_modules 中的类型定义与 4.0.26 运行时 API 存在差异（t40 验证器确认）：
// - 类型定义: updateWorldTransform(physics: Physics), 运行时: updateWorldTransform()
// - 类型定义: setupPose(), 运行时: setToSetupPose()
// - 类型定义包含 Physics 枚举，运行时已移除
// 以下类型断言仅用于修正已知的类型定义错误，不作他用。

import {
  SceneRenderer,
  OrthoCamera,
  SkeletonJson,
  SkeletonBinary,
  TextureAtlas,
  AtlasAttachmentLoader,
  Skeleton,
  AnimationState,
  AnimationStateData,
  GLTexture,
} from '@esotericsoftware/spine-webgl'

export const spine = {
  SceneRenderer,
  OrthoCamera,
  SkeletonJson,
  SkeletonBinary,
  TextureAtlas,
  AtlasAttachmentLoader,
  Skeleton,
  AnimationState,
  AnimationStateData,
  GLTexture,
  // 运行时实际存在但类型定义缺失的方法
  skeletonSetToSetupPose: (s: Skeleton) => (s as any).setToSetupPose(),
  skeletonUpdateWorldTransform: (s: Skeleton) => (s as any).updateWorldTransform(),
}
