// 预览 store：管理选中资源的预览状态
// 含代际守卫（generation guard）与并发闸门（concurrency gate）

import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ipc } from '@/ipc'
import type { AssetPreviewResult, AssetPreviewKind } from '@/ipc'

interface PreviewState {
  loading: boolean
  error: string | null
  result: AssetPreviewResult | null
  generation: number
  // 性能测量
  lastDecodeMs: number
  lastDecodeBytes: number
}

export const usePreviewStore = defineStore('preview', () => {
  const state = ref<PreviewState>({
    loading: false,
    error: null,
    result: null,
    generation: 0,
    lastDecodeMs: 0,
    lastDecodeBytes: 0,
  })

  // 并发闸门：同时最多 N 个大图解码
  const MAX_CONCURRENT_DECODE = 4
  // 有界队列：等待队列最长 N，超出时丢弃队尾（最新）—— 优先保证当前页响应
  const MAX_QUEUE_LENGTH = 4
  let activeDecodes = 0
  const decodeQueue: Array<() => void> = []

  function acquireSlot(): Promise<void> {
    return new Promise((resolve) => {
      if (activeDecodes < MAX_CONCURRENT_DECODE) {
        activeDecodes++
        resolve()
      } else {
        // 背压：队列满时丢弃队尾（最新入队者优先），避免快速切换时积压
        if (decodeQueue.length >= MAX_QUEUE_LENGTH) {
          decodeQueue.pop() // 丢弃最新等待者
        }
        decodeQueue.push(() => {
          activeDecodes++
          resolve()
        })
      }
    })
  }

  function releaseSlot() {
    activeDecodes--
    const next = decodeQueue.shift()
    if (next) next()
  }

  async function loadPreview(assetId: string) {
    const gen = ++state.value.generation
    state.value.loading = true
    state.value.error = null
    state.value.result = null
    const t0 = performance.now()

    try {
      const result = await ipc.request<AssetPreviewResult>('asset.preview', { assetId })

      if (gen !== state.value.generation) return // 代际守卫

      state.value.result = result
      state.value.lastDecodeMs = performance.now() - t0
    } catch (e: unknown) {
      if (gen !== state.value.generation) return
      state.value.error = e instanceof Error ? e.message : String(e)
    } finally {
      if (gen === state.value.generation) {
        state.value.loading = false
      }
    }
  }

  function clearPreview() {
    // F-06 fix: 释放旧图片 URL，避免浏览器继续持有已解码图片
    const oldUrl = state.value.result?.binaryUrl
    if (oldUrl && oldUrl.startsWith('blob:')) {
      URL.revokeObjectURL(oldUrl)
    }
    state.value.result = null
    state.value.error = null
    state.value.loading = false
  }

  return {
    state,
    loadPreview,
    clearPreview,
    acquireSlot,
    releaseSlot,
  }
})
