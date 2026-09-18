/**
 * 外观偏好（主题明暗 + 强调色）—— 纯逻辑层，不含 Vue 依赖。
 *
 * 生效方式：在 `<html>` 上写 `data-theme` / `data-accent`，
 * 由 `src/styles/tokens.css` 的属性选择器切换整组设计令牌。
 * 因此本文件**不出现任何色值**：色板由 tokens.css 唯一提供。
 *
 * 持久化：localStorage（WebView2 的 userDataFolder 下，跨启动保留）。
 * 说明：后端的 `config.read/write` 只接受 5 个路径类白名单键，
 * 外观偏好不属于配置契约范围，故不走 IPC。
 */

/** 明暗模式 */
export type ThemeMode = 'dark' | 'light'

/** 强调色方案（与 tokens.css 的 `[data-accent=...]` 一一对应） */
export type AccentScheme = 'amber' | 'azure' | 'crimson' | 'emerald' | 'violet'

export interface ThemeOption {
  id: ThemeMode
  label: string
  description: string
}

export interface AccentOption {
  id: AccentScheme
  label: string
  description: string
}

export const THEME_OPTIONS: readonly ThemeOption[] = [
  { id: 'dark', label: '深色', description: '默认。适合长时间编辑与资源预览' },
  { id: 'light', label: '浅色', description: '明亮环境下更清晰' },
]

export const ACCENT_OPTIONS: readonly AccentOption[] = [
  { id: 'amber', label: '琥珀', description: '默认。暖金调，贴近原作气质' },
  { id: 'azure', label: '天青', description: '冷蓝调，冷静克制' },
  { id: 'crimson', label: '绯红', description: '高对比红调，警示感强' },
  { id: 'emerald', label: '翡翠', description: '青绿调，柔和护眼' },
  { id: 'violet', label: '紫罗兰', description: '紫调，偏创作工具风格' },
]

export const DEFAULT_THEME_MODE: ThemeMode = 'dark'
export const DEFAULT_ACCENT: AccentScheme = 'amber'

const STORAGE_KEY = 'lme.appearance.v1'

export interface Appearance {
  mode: ThemeMode
  accent: AccentScheme
}

function isThemeMode(value: unknown): value is ThemeMode {
  return THEME_OPTIONS.some((o) => o.id === value)
}

function isAccent(value: unknown): value is AccentScheme {
  return ACCENT_OPTIONS.some((o) => o.id === value)
}

/** 读取持久化的外观偏好；缺失或损坏时回落到默认值 */
export function loadAppearance(): Appearance {
  const fallback: Appearance = { mode: DEFAULT_THEME_MODE, accent: DEFAULT_ACCENT }
  if (typeof localStorage === 'undefined') return fallback
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return fallback
    const parsed = JSON.parse(raw) as Partial<Appearance>
    return {
      mode: isThemeMode(parsed.mode) ? parsed.mode : fallback.mode,
      accent: isAccent(parsed.accent) ? parsed.accent : fallback.accent,
    }
  } catch {
    return fallback
  }
}

/** 持久化外观偏好（失败不抛：无痕模式等场景下仅本次会话生效） */
export function saveAppearance(value: Appearance): void {
  if (typeof localStorage === 'undefined') return
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(value))
  } catch {
    /* 忽略写入失败 */
  }
}

/** 把外观偏好写到 `<html>`，触发 tokens.css 的令牌切换 */
export function applyAppearance(value: Appearance): void {
  if (typeof document === 'undefined') return
  const root = document.documentElement
  root.dataset.theme = value.mode
  root.dataset.accent = value.accent
}

/** 在应用挂载前调用：尽早写属性，避免首帧闪色 */
export function bootstrapAppearance(): Appearance {
  const value = loadAppearance()
  applyAppearance(value)
  return value
}

/** 当前生效的明暗模式（供第三方画布/图表取色时判断） */
export function currentThemeMode(): ThemeMode {
  if (typeof document === 'undefined') return DEFAULT_THEME_MODE
  const value = document.documentElement.dataset.theme
  return isThemeMode(value) ? value : DEFAULT_THEME_MODE
}
