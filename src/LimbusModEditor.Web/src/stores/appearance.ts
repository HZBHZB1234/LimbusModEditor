/**
 * 外观 store —— 明暗模式 + 强调色方案。
 *
 * 生效链路：
 *   setMode/setAccent → 先写 `<html data-theme|data-accent>`（DOM 立即生效）
 *                     → 再改 state（触发 themeOverrides getter 重新读取 CSS 变量）
 *
 * 顺序很重要：Naive UI 的主题覆盖是运行时读 `getComputedStyle` 得到的，
 * 必须先改 DOM 再让 getter 失效，否则会拿到上一套令牌的值。
 */
import { defineStore } from 'pinia'
import { buildThemeOverrides, naiveBaseTheme } from '@/theme/naiveTheme'
import {
  applyAppearance,
  loadAppearance,
  saveAppearance,
  type AccentScheme,
  type Appearance,
  type ThemeMode,
} from '@/theme/preferences'
import type { GlobalTheme, GlobalThemeOverrides } from 'naive-ui'

const initial: Appearance = loadAppearance()

interface AppearanceState {
  mode: ThemeMode
  accent: AccentScheme
}

export const useAppearanceStore = defineStore('appearance', {
  state: (): AppearanceState => ({
    mode: initial.mode,
    accent: initial.accent,
  }),

  getters: {
    /** Naive UI 基础主题：深色 darkTheme / 浅色 null（库默认亮色） */
    baseTheme(state): GlobalTheme | null {
      return naiveBaseTheme(state.mode)
    },

    /**
     * Naive UI 主题覆盖。
     * 依赖 mode/accent：两者变化时重新读取已切换过的 CSS 变量。
     */
    themeOverrides(state): GlobalThemeOverrides {
      // 显式读一次 state，建立依赖
      void state.mode
      void state.accent
      return buildThemeOverrides()
    },

    isDark: (state): boolean => state.mode === 'dark',
  },

  actions: {
    setMode(mode: ThemeMode): void {
      if (this.mode === mode) return
      // 先落 DOM，再改 state（见文件头说明）
      applyAppearance({ mode, accent: this.accent })
      this.mode = mode
      saveAppearance({ mode, accent: this.accent })
    },

    setAccent(accent: AccentScheme): void {
      if (this.accent === accent) return
      applyAppearance({ mode: this.mode, accent })
      this.accent = accent
      saveAppearance({ mode: this.mode, accent })
    },

    toggleMode(): void {
      this.setMode(this.mode === 'dark' ? 'light' : 'dark')
    },

    /** 恢复到出厂外观（深色 + 琥珀） */
    reset(): void {
      applyAppearance({ mode: 'dark', accent: 'amber' })
      this.mode = 'dark'
      this.accent = 'amber'
      saveAppearance({ mode: 'dark', accent: 'amber' })
    },
  },
})
