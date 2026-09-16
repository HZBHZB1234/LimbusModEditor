// UI 状态 store：列宽、视图偏好等

import { defineStore } from 'pinia'

interface UiState {
  previewColumnWidth: number
  browserColumnMinWidth: number
}

export const useUiStateStore = defineStore('uiState', {
  state: (): UiState => ({
    previewColumnWidth: 360,
    browserColumnMinWidth: 280,
  }),

  actions: {
    setPreviewColumnWidth(width: number) {
      // 钳制 260–2000（与 UiStateService 一致）
      this.previewColumnWidth = Math.max(260, Math.min(2000, width))
    },
  },
})
