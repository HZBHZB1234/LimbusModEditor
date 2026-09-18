/**
 * Naive UI 主题映射 —— 全部色值从 `src/styles/tokens.css` 派生（运行时读取 CSS 变量计算值）。
 *
 * 铁律：本文件**不得出现任何色值字面量**；设计色唯一来源是 tokens.css。
 * 实现方式：在应用挂载前读 `getComputedStyle(:root)` 拿到 tokens.css 的变量值，
 * 再喂给 Naive UI 的 `GlobalThemeOverrides`（库需要可解析的 hex/rgba 才能做派生色计算，
 * 因此不能直接传 `var(...)` 字符串）。
 *
 * 若某个 token 缺失（例如测试环境未加载样式），对应键跳过、回落到 Naive UI 自带暗色主题，
 * 绝不在 TS 里另起一套色值。
 */
import { darkTheme, type GlobalThemeOverrides } from 'naive-ui'

/** 读取 tokens.css 中某个 CSS 变量的当前计算值 */
function token(name: string): string | undefined {
  if (typeof window === 'undefined' || typeof getComputedStyle !== 'function') return undefined
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  return value.length > 0 ? value : undefined
}

/** 按 [库主题键, tokens.css 变量名] 映射，跳过缺失项 */
function derive(pairs: ReadonlyArray<readonly [string, string]>): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [key, tokenName] of pairs) {
    const value = token(tokenName)
    if (value !== undefined) out[key] = value
  }
  return out
}

/** 状态色四件套（默认/悬浮/按下/补充）；tokens 只给一个主色，四态同色 */
function pushStatus(
  out: Record<string, string>,
  prefix: 'success' | 'warning' | 'error' | 'info',
  tokenName: string,
): void {
  const value = token(tokenName)
  if (value === undefined) return
  out[`${prefix}Color`] = value
  out[`${prefix}ColorHover`] = value
  out[`${prefix}ColorPressed`] = value
  out[`${prefix}ColorSuppl`] = value
}

/**
 * 生成 Naive UI 主题覆盖对象。
 * @param accentToken 主色 token（工作台区用紫 `--lme-accent`，维基区用金 `--wiki-accent`）
 */
export function buildThemeOverrides(accentToken = '--lme-accent'): GlobalThemeOverrides {
  const accent = token(accentToken) ?? token('--lme-accent')
  const accentHover = token('--lme-accent-hover')
  const accentPressed = token('--lme-accent-muted')

  const common: Record<string, string> = derive([
    // ── 背景层级 ──
    ['baseColor', '--lme-bg-base'],
    ['bodyColor', '--lme-bg-base'],
    ['cardColor', '--lme-bg-panel'],
    ['modalColor', '--lme-bg-elevated'],
    ['popoverColor', '--lme-bg-elevated'],
    ['tableColor', '--lme-bg-panel'],
    ['tableHeaderColor', '--lme-bg-elevated'],
    ['inputColor', '--lme-bg-input'],
    ['inputColorDisabled', '--lme-bg-elevated'],
    ['actionColor', '--lme-bg-hover'],

    // ── 文字层级 ──
    ['textColorBase', '--lme-text-primary'],
    ['textColor1', '--lme-text-primary'],
    ['textColor2', '--lme-text-secondary'],
    ['textColor3', '--lme-text-muted'],
    ['textColorDisabled', '--lme-text-disabled'],
    ['placeholderColor', '--lme-text-muted'],
    ['placeholderColorDisabled', '--lme-text-disabled'],

    // ── 边框与分割 ──
    ['borderColor', '--lme-border'],
    ['dividerColor', '--lme-border'],

    // ── 圆角 / 字体 / 字号 / 阴影 / 缓动 ──
    ['borderRadius', '--lme-radius-md'],
    ['borderRadiusSmall', '--lme-radius-sm'],
    ['fontFamily', '--lme-font-family'],
    ['fontFamilyMono', '--lme-font-mono'],
    ['fontSize', '--lme-font-size-md'],
    ['fontSizeMini', '--lme-font-size-xs'],
    ['fontSizeTiny', '--lme-font-size-xs'],
    ['fontSizeSmall', '--lme-font-size-sm'],
    ['fontSizeMedium', '--lme-font-size-md'],
    ['fontSizeLarge', '--lme-font-size-lg'],
    ['fontSizeHuge', '--lme-font-size-xl'],
    ['lineHeight', '--lme-line-height-normal'],
    ['fontWeight', '--lme-font-weight-regular'],
    ['fontWeightStrong', '--lme-font-weight-semibold'],
    ['boxShadow1', '--lme-shadow-sm'],
    ['boxShadow2', '--lme-shadow-md'],
    ['boxShadow3', '--lme-shadow-lg'],
    ['cubicBezierEaseInOut', '--lme-ease-standard'],
    ['cubicBezierEaseOut', '--lme-ease-standard'],
    ['cubicBezierEaseIn', '--lme-ease-standard'],
  ])

  // 状态色（success/warning/error/info 四态）
  pushStatus(common, 'success', '--lme-success')
  pushStatus(common, 'warning', '--lme-warning')
  pushStatus(common, 'error', '--lme-error')
  pushStatus(common, 'info', '--lme-info')

  // 主色：工作台区紫、维基区金（都由 tokens 提供）
  if (accent !== undefined) common.primaryColor = accent
  if (accentHover !== undefined) {
    common.primaryColorHover = accentHover
    common.primaryColorSuppl = accentHover
  }
  if (accentPressed !== undefined) common.primaryColorPressed = accentPressed

  return {
    common: common as GlobalThemeOverrides['common'],
    Tabs: {
      barColor: accent,
      tabTextColorActiveBar: accent,
      tabTextColorBar: token('--lme-tab-text'),
      tabTextColorHoverBar: token('--lme-tab-text-hover'),
    },
    Card: {
      color: token('--lme-bg-panel'),
      borderColor: token('--lme-border'),
      borderRadius: token('--lme-radius-lg'),
      titleFontWeight: token('--lme-font-weight-semibold'),
    },
    DataTable: {
      thColor: token('--lme-bg-elevated'),
      tdColor: token('--lme-bg-panel'),
      tdColorHover: token('--lme-bg-hover'),
      borderColor: token('--lme-border'),
      thFontWeight: token('--lme-font-weight-semibold'),
    },
    Input: {
      color: token('--lme-bg-input'),
      colorFocus: token('--lme-bg-input'),
      borderHover: token('--lme-border-strong'),
      borderFocus: accent,
      boxShadowFocus: token('--lme-shadow-focus'),
    },
    Empty: {
      textColor: token('--lme-text-muted'),
      iconColor: token('--lme-text-disabled'),
    },
    Tooltip: {
      color: token('--lme-bg-elevated'),
      textColor: token('--lme-text-primary'),
    },
  }
}

/** 暗色主题（Naive UI 自带）+ 由 tokens 派生的覆盖 */
export const lmeDarkTheme = darkTheme
