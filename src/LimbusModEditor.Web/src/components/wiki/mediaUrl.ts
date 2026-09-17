// 维基媒体地址解析（实体页与剧情页共用）
// 二进制统一走 lme.data 虚拟主机，禁止 base64
import type { ResourceBinding } from '@/ipc/types'

/**
 * 把 binding 解析成可播放 / 可显示的地址。
 *
 * 实测：resource_bindings.deep_link / ref_key 是**原始容器路径，不是 URL**
 * （Audio 形如 `...\Voice_*.bank\0事件名`，Image/Spine 形如 `Assets/...`），
 * 前端**不许**据此拼接 lme.data 地址 —— 拼出来就是不存在的地址。
 *
 * 地址一律由后端下发：Image 用 mediaUrl，Audio 用 audioUrl（缺则回落 mediaUrl），
 * Spine 用 skeletonUrl / atlasUrl / textureUrls。读不到就返回 null：
 * 调用方（WikiAudioPlayer / WikiGallery / WikiSpineViewer）按降级处理
 * （显示「无可用地址」或不渲染），不占位、不编造。
 */

/** 图片 / 预览地址 */
export function resolveMediaUrl(binding: ResourceBinding): string | null {
  return firstUrl(binding.mediaUrl)
}

/** 音频地址：优先后端解码出的 WAV（audioUrl），缺则回落到通用预览地址 */
export function resolveAudioUrl(binding: ResourceBinding): string | null {
  return firstUrl(binding.audioUrl) ?? firstUrl(binding.mediaUrl)
}

export interface SpineAssets {
  skeletonUrl: string
  atlasUrl: string
  textureUrls: Record<string, string>
}

/** Spine 三件套齐备才返回；缺任一返回 null（调用方整块不渲染，不做半成品动画） */
export function resolveSpineAssets(binding: ResourceBinding): SpineAssets | null {
  const skeletonUrl = firstUrl(binding.skeletonUrl)
  const atlasUrl = firstUrl(binding.atlasUrl)
  const textureUrls = binding.textureUrls ?? undefined
  if (!skeletonUrl || !atlasUrl || !textureUrls || Object.keys(textureUrls).length === 0) {
    return null
  }
  return { skeletonUrl, atlasUrl, textureUrls }
}

function firstUrl(value: string | null | undefined): string | null {
  return typeof value === 'string' && value !== '' ? value : null
}
