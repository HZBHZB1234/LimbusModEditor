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
 * 后端 DTO 当前未下发 mediaUrl / audioUrl，因此这里读不到就返回 null：
 * 调用方（WikiAudioPlayer / WikiGallery / WikiSpineViewer）按降级处理
 * （显示「无可用地址」或不渲染），不占位、不编造。
 *
 * 待后端 DTO 下发 mediaUrl / audioUrl 后本函数自动生效，调用方无需改动。
 */
export function resolveMediaUrl(binding: ResourceBinding): string | null {
  const candidate =
    (binding as { mediaUrl?: string }).mediaUrl ?? (binding as { audioUrl?: string }).audioUrl
  return typeof candidate === 'string' && candidate !== '' ? candidate : null
}
