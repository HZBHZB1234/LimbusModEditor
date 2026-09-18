/**
 * Naive UI 主题映射 —— 全部取值从 `src/styles/tokens.css` 派生
 * （运行时读取 CSS 变量的计算值，因此主题/强调色切换后自动跟随）。
 *
 * 铁律：本文件**不得出现任何色值字面量**；设计色唯一来源是 tokens.css。
 * 实现方式：读 `getComputedStyle(:root)` 拿到令牌值，再喂给 Naive UI 的
 * `GlobalThemeOverrides`（库需要可解析的 hex/rgba 才能做派生色计算，
 * 因此不能直接传 `var(...)` 字符串）。
 *
 * 若某个令牌缺失（例如测试环境未加载样式），对应键跳过、回落到 Naive UI 自带主题，
 * 绝不在 TS 里另起一套色值。
 */
import { darkTheme, type GlobalTheme, type GlobalThemeOverrides } from 'naive-ui'
import type { ThemeMode } from './preferences'

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

/** 可空对象：值为 undefined 时由 Naive UI 自行派生 */
function pack(pairs: ReadonlyArray<readonly [string, string]>): Record<string, string> {
  return derive(pairs)
}

/**
 * 生成 Naive UI 主题覆盖对象。
 *
 * 主色不再分区（旧版工作台紫 / 维基金），统一取 `--lme-accent`，
 * 由 `<html data-accent>` 决定具体方案。
 */
export function buildThemeOverrides(): GlobalThemeOverrides {
  const accent = token('--lme-accent')
  const accentHover = token('--lme-accent-hover')
  const accentPressed = token('--lme-accent-muted')

  const common: Record<string, string> = pack([
    // ── 背景层级 ──
    ['baseColor', '--lme-bg-base'],
    ['bodyColor', '--lme-bg-base'],
    ['cardColor', '--lme-bg-panel'],
    ['modalColor', '--lme-bg-elevated'],
    ['popoverColor', '--lme-bg-elevated'],
    ['tableColor', '--lme-bg-panel'],
    ['tableHeaderColor', '--lme-bg-elevated'],
    ['inputColor', '--lme-bg-input'],
    ['inputColorDisabled', '--lme-bg-inset'],
    ['actionColor', '--lme-bg-hover'],
    ['actionColorHover', '--lme-bg-hover'],
    ['tagColor', '--lme-bg-elevated'],
    ['avatarColor', '--lme-accent'],

    // ── 文字层级 ──
    ['textColorBase', '--lme-text-primary'],
    ['textColor1', '--lme-text-primary'],
    ['textColor2', '--lme-text-secondary'],
    ['textColor3', '--lme-text-muted'],
    ['textColorDisabled', '--lme-text-disabled'],
    ['placeholderColor', '--lme-text-muted'],
    ['placeholderColorDisabled', '--lme-text-disabled'],
    ['iconColor', '--lme-text-muted'],
    ['iconColorHover', '--lme-text-primary'],
    ['iconColorPressed', '--lme-text-secondary'],

    // ── 边框与分割 ──
    ['borderColor', '--lme-border'],
    ['dividerColor', '--lme-border'],
    ['hoverColor', '--lme-bg-hover'],

    // ── 圆角 / 字体 / 字号 / 行高 / 字重 ──
    ['borderRadius', '--lme-radius-md'],
    ['borderRadiusSmall', '--lme-radius-sm'],
    ['fontFamily', '--lme-font-family'],
    ['fontFamilyMono', '--lme-font-mono'],
    ['fontSize', '--lme-font-size-md'],
    ['fontSizeMini', '--lme-font-size-2xs'],
    ['fontSizeTiny', '--lme-font-size-xs'],
    ['fontSizeSmall', '--lme-font-size-sm'],
    ['fontSizeMedium', '--lme-font-size-md'],
    ['fontSizeLarge', '--lme-font-size-lg'],
    ['fontSizeHuge', '--lme-font-size-xl'],
    ['lineHeight', '--lme-line-height-normal'],
    ['fontWeight', '--lme-font-weight-regular'],
    ['fontWeightStrong', '--lme-font-weight-semibold'],

    // ── 海拔 / 缓动 ──
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

  // 主色（由 <html data-accent> 决定）
  if (accent !== undefined) common.primaryColor = accent
  if (accentHover !== undefined) {
    common.primaryColorHover = accentHover
    common.primaryColorSuppl = accentHover
  }
  if (accentPressed !== undefined) common.primaryColorPressed = accentPressed

  return {
    common: common as GlobalThemeOverrides['common'],

    Button: pack([
      ['borderRadiusTiny', '--lme-radius-sm'],
      ['borderRadiusSmall', '--lme-radius-md'],
      ['borderRadiusMedium', '--lme-radius-md'],
      ['borderRadiusLarge', '--lme-radius-md'],
      ['fontWeight', '--lme-font-weight-medium'],
      ['fontWeightStrong', '--lme-font-weight-semibold'],
      ['heightTiny', '--lme-control-height-sm'],
      ['heightSmall', '--lme-control-height-sm'],
      ['heightMedium', '--lme-control-height-md'],
      ['heightLarge', '--lme-control-height-lg'],
      ['colorTertiary', '--lme-bg-elevated'],
      ['colorTertiaryHover', '--lme-bg-hover'],
      ['textColorGhost', '--lme-text-secondary'],
      ['textColorGhostHover', '--lme-accent'],
      ['textColorGhostPressed', '--lme-accent-hover'],
      ['textColorTextHover', '--lme-text-primary'],
      ['border', '--lme-border'],
      ['borderHover', '--lme-accent-border'],
      ['borderFocus', '--lme-accent'],
    ]),

    Card: pack([
      ['color', '--lme-bg-panel'],
      ['colorModal', '--lme-bg-elevated'],
      ['borderColor', '--lme-border'],
      ['borderRadius', '--lme-radius-lg'],
      ['titleFontWeight', '--lme-font-weight-semibold'],
      ['titleFontSizeSmall', '--lme-font-size-md'],
      ['titleFontSizeMedium', '--lme-font-size-md'],
      ['titleTextColor', '--lme-text-primary'],
      ['paddingSmall', '--lme-gap-md'],
    ]),

    Input: pack([
      ['color', '--lme-bg-input'],
      ['colorFocus', '--lme-bg-input'],
      ['colorDisabled', '--lme-bg-inset'],
      ['border', '--lme-border'],
      ['borderHover', '--lme-border-strong'],
      ['borderFocus', '--lme-accent'],
      ['boxShadowFocus', '--lme-shadow-focus'],
      ['placeholderColor', '--lme-text-muted'],
      ['caretColor', '--lme-accent'],
      ['textColor', '--lme-text-primary'],
    ]),

    Select: pack([
      ['menuBoxShadow', '--lme-shadow-lg'],
      ['peersBorderColor', '--lme-border'],
      ['peersBorderColorHover', '--lme-border-strong'],
      ['peersBorderColorFocus', '--lme-accent'],
      ['peersBoxShadowFocus', '--lme-shadow-focus'],
    ]),

    DataTable: pack([
      ['thColor', '--lme-bg-elevated'],
      ['thColorHover', '--lme-bg-hover'],
      ['tdColor', '--lme-bg-panel'],
      ['tdColorHover', '--lme-bg-hover'],
      ['tdColorStriped', '--lme-bg-inset'],
      ['borderColor', '--lme-border'],
      ['thFontWeight', '--lme-font-weight-semibold'],
      ['thTextColor', '--lme-text-secondary'],
      ['tdTextColor', '--lme-text-primary'],
    ]),

    Tabs: pack([
      ['barColor', '--lme-accent'],
      ['tabTextColorActiveBar', '--lme-accent'],
      ['tabTextColorBar', '--lme-tab-text'],
      ['tabTextColorHoverBar', '--lme-tab-text-hover'],
      ['tabFontWeightActive', '--lme-font-weight-semibold'],
      ['paneTextColor', '--lme-text-primary'],
      ['colorSegment', '--lme-bg-inset'],
      ['tabColorSegment', '--lme-accent'],
      ['tabTextColorSegment', '--lme-text-secondary'],
      ['tabTextColorActiveSegment', '--lme-accent-contrast'],
      ['tabTextColorHoverSegment', '--lme-text-primary'],
    ]),

    Tag: pack([
      ['borderRadius', '--lme-radius-full'],
      ['color', '--lme-bg-elevated'],
      ['colorBordered', '--lme-bg-panel'],
      ['textColor', '--lme-text-secondary'],
      ['border', '--lme-border'],
      ['fontWeight', '--lme-font-weight-medium'],
    ]),

    Tooltip: pack([
      ['color', '--lme-bg-elevated'],
      ['textColor', '--lme-text-primary'],
      ['borderRadius', '--lme-radius-md'],
      ['boxShadow', '--lme-shadow-lg'],
    ]),

    Popover: pack([
      ['color', '--lme-bg-elevated'],
      ['borderRadius', '--lme-radius-lg'],
      ['boxShadow', '--lme-shadow-lg'],
      ['textColor', '--lme-text-primary'],
    ]),

    Dropdown: pack([
      ['color', '--lme-bg-elevated'],
      ['borderRadius', '--lme-radius-lg'],
      ['boxShadow', '--lme-shadow-lg'],
      ['optionTextColorHover', '--lme-text-primary'],
      ['optionTextColorActive', '--lme-accent'],
      ['optionColorPending', '--lme-bg-hover'],
      ['dividerColor', '--lme-border'],
    ]),

    Menu: pack([
      ['color', '--lme-bg-elevated'],
      ['itemColorActive', '--lme-accent-subtle'],
      ['itemColorActiveHover', '--lme-accent-subtle'],
      ['itemColorHover', '--lme-bg-hover'],
      ['itemTextColor', '--lme-text-secondary'],
      ['itemTextColorHover', '--lme-text-primary'],
      ['itemTextColorActive', '--lme-accent'],
      ['itemTextColorActiveHover', '--lme-accent'],
      ['itemIconColor', '--lme-text-muted'],
      ['itemIconColorActive', '--lme-accent'],
      ['borderRadius', '--lme-radius-md'],
      ['arrowColor', '--lme-accent'],
    ]),

    Modal: pack([
      ['color', '--lme-bg-elevated'],
      ['borderRadius', '--lme-radius-xl'],
      ['boxShadow', '--lme-shadow-xl'],
      ['titleTextColor', '--lme-text-primary'],
      ['titleFontWeight', '--lme-font-weight-semibold'],
      ['textColor', '--lme-text-secondary'],
      ['closeIconColor', '--lme-text-muted'],
      ['closeIconColorHover', '--lme-text-primary'],
    ]),

    Dialog: pack([
      ['color', '--lme-bg-elevated'],
      ['borderRadius', '--lme-radius-xl'],
      ['boxShadow', '--lme-shadow-xl'],
      ['titleTextColor', '--lme-text-primary'],
      ['textColor', '--lme-text-secondary'],
      ['iconColorInfo', '--lme-info'],
      ['iconColorSuccess', '--lme-success'],
      ['iconColorWarning', '--lme-warning'],
      ['iconColorError', '--lme-error'],
    ]),

    Drawer: pack([
      ['color', '--lme-bg-elevated'],
      ['textColor', '--lme-text-primary'],
      ['titleTextColor', '--lme-text-primary'],
      ['boxShadow', '--lme-shadow-xl'],
    ]),

    Notification: pack([
      ['color', '--lme-bg-elevated'],
      ['borderRadius', '--lme-radius-lg'],
      ['boxShadow', '--lme-shadow-lg'],
      ['titleTextColor', '--lme-text-primary'],
      ['textColor', '--lme-text-secondary'],
      ['closeIconColor', '--lme-text-muted'],
    ]),

    Message: pack([
      ['color', '--lme-bg-elevated'],
      ['colorInfo', '--lme-bg-elevated'],
      ['colorSuccess', '--lme-bg-elevated'],
      ['colorWarning', '--lme-bg-elevated'],
      ['colorError', '--lme-bg-elevated'],
      ['colorLoading', '--lme-bg-elevated'],
      ['textColor', '--lme-text-primary'],
      ['textColorInfo', '--lme-text-primary'],
      ['textColorSuccess', '--lme-text-primary'],
      ['textColorWarning', '--lme-text-primary'],
      ['textColorError', '--lme-text-primary'],
      ['textColorLoading', '--lme-text-primary'],
      ['borderRadius', '--lme-radius-lg'],
      ['boxShadow', '--lme-shadow-lg'],
    ]),

    List: pack([
      ['color', '--lme-bg-panel'],
      ['colorHover', '--lme-bg-hover'],
      ['borderColor', '--lme-border'],
      ['borderRadius', '--lme-radius-md'],
      ['textColor', '--lme-text-primary'],
    ]),

    Switch: pack([
      ['railColor', '--lme-bg-active'],
      ['railColorActive', '--lme-accent'],
      ['buttonColor', '--lme-bg-panel'],
      ['boxShadowFocus', '--lme-shadow-focus'],
    ]),

    Checkbox: pack([
      ['border', '--lme-border-strong'],
      ['borderFocus', '--lme-accent'],
      ['borderChecked', '--lme-accent'],
      ['color', '--lme-accent'],
      ['colorChecked', '--lme-accent'],
      ['checkMarkColor', '--lme-accent-contrast'],
      ['boxShadowFocus', '--lme-shadow-focus'],
      ['textColor', '--lme-text-primary'],
    ]),

    Radio: pack([
      ['buttonBorderColor', '--lme-border'],
      ['buttonBorderColorActive', '--lme-accent'],
      ['buttonTextColorActive', '--lme-accent'],
      ['buttonColorActive', '--lme-accent-subtle'],
      ['dotColorActive', '--lme-accent'],
      ['boxShadowFocus', '--lme-shadow-focus'],
      ['textColor', '--lme-text-primary'],
    ]),

    Pagination: pack([
      ['itemColor', '--lme-bg-panel'],
      ['itemColorHover', '--lme-bg-hover'],
      ['itemColorActive', '--lme-accent-subtle'],
      ['itemBorder', '--lme-border'],
      ['itemBorderHover', '--lme-accent-border'],
      ['itemBorderActive', '--lme-accent'],
      ['itemTextColor', '--lme-text-secondary'],
      ['itemTextColorHover', '--lme-text-primary'],
      ['itemTextColorActive', '--lme-accent'],
      ['itemTextColorPressed', '--lme-accent-hover'],
    ]),

    Progress: pack([
      ['railColor', '--lme-progressbar-bg'],
      ['fillColor', '--lme-progressbar-fill'],
      ['textColorLineInner', '--lme-text-primary'],
      ['textColorCircle', '--lme-text-primary'],
    ]),

    Alert: pack([
      ['borderRadius', '--lme-radius-md'],
      ['titleTextColor', '--lme-text-primary'],
      ['contentTextColor', '--lme-text-secondary'],
      ['iconColorInfo', '--lme-info'],
      ['iconColorSuccess', '--lme-success'],
      ['iconColorWarning', '--lme-warning'],
      ['iconColorError', '--lme-error'],
      ['colorInfo', '--lme-info-subtle'],
      ['colorSuccess', '--lme-success-subtle'],
      ['colorWarning', '--lme-warning-subtle'],
      ['colorError', '--lme-error-subtle'],
    ]),

    Collapse: pack([
      ['titleTextColor', '--lme-text-primary'],
      ['titleTextColorHover', '--lme-accent'],
      ['contentTextColor', '--lme-text-secondary'],
      ['dividerColor', '--lme-border'],
      ['arrowColor', '--lme-text-muted'],
      ['borderRadius', '--lme-radius-md'],
      ['itemMargin', '--lme-gap-sm'],
    ]),

    Tree: pack([
      ['nodeTextColor', '--lme-text-primary'],
      ['nodeTextColorHover', '--lme-accent'],
      ['nodeColorHover', '--lme-bg-hover'],
      ['nodeColorActive', '--lme-accent-subtle'],
      ['nodeColorPressed', '--lme-bg-active'],
      ['nodeBorderRadius', '--lme-radius-sm'],
      ['arrowColor', '--lme-text-muted'],
      ['lineColor', '--lme-border'],
    ]),

    Empty: pack([
      ['textColor', '--lme-text-muted'],
      ['iconColor', '--lme-text-disabled'],
      ['iconSizeMedium', '--lme-font-size-3xl'],
      ['fontSizeMedium', '--lme-font-size-sm'],
    ]),

    Spin: pack([
      ['color', '--lme-accent'],
      ['textColor', '--lme-text-secondary'],
    ]),

    Divider: pack([
      ['color', '--lme-border'],
      ['textColor', '--lme-text-muted'],
    ]),

    Breadcrumb: pack([
      ['itemTextColor', '--lme-text-muted'],
      ['itemTextColorHover', '--lme-accent'],
      ['itemTextColorActive', '--lme-text-secondary'],
      ['separatorColor', '--lme-text-disabled'],
    ]),

    Layout: pack([
      ['color', '--lme-bg-base'],
      ['siderColor', '--lme-bg-panel'],
      ['headerColor', '--lme-bg-panel'],
      ['footerColor', '--lme-bg-panel'],
      ['siderBorderColor', '--lme-border'],
      ['headerBorderColor', '--lme-border'],
      ['footerBorderColor', '--lme-border'],
    ]),

    Descriptions: pack([
      ['thColor', '--lme-bg-elevated'],
      ['tdColor', '--lme-bg-panel'],
      ['thTextColor', '--lme-text-secondary'],
      ['tdTextColor', '--lme-text-primary'],
      ['borderColor', '--lme-border'],
      ['borderRadius', '--lme-radius-md'],
      ['thFontWeight', '--lme-font-weight-medium'],
    ]),

    Form: pack([
      ['labelTextColor', '--lme-text-secondary'],
      ['labelFontWeight', '--lme-font-weight-medium'],
      ['feedbackTextColor', '--lme-text-muted'],
    ]),

    Badge: pack([
      ['color', '--lme-accent'],
      ['colorInfo', '--lme-info'],
      ['colorSuccess', '--lme-success'],
      ['colorWarning', '--lme-warning'],
      ['colorError', '--lme-error'],
      ['textColor', '--lme-accent-contrast'],
      ['fontSize', '--lme-font-size-2xs'],
    ]),

    Scrollbar: pack([
      ['color', '--lme-scrollbar-thumb'],
      ['colorHover', '--lme-scrollbar-thumb-hover'],
      ['railColor', '--lme-scrollbar-track'],
    ]),

    Skeleton: pack([
      ['color', '--lme-bg-hover'],
      ['colorEnd', '--lme-bg-active'],
    ]),

    Slider: pack([
      ['railColor', '--lme-bg-active'],
      ['railColorHover', '--lme-bg-active'],
      ['fillColor', '--lme-accent'],
      ['fillColorHover', '--lme-accent-hover'],
      ['handleColor', '--lme-accent'],
      ['indicatorColor', '--lme-bg-elevated'],
      ['indicatorTextColor', '--lme-text-primary'],
    ]),

    Steps: pack([
      ['indicatorColorProcess', '--lme-accent'],
      ['indicatorColorFinish', '--lme-success'],
      ['indicatorColorError', '--lme-error'],
      ['indicatorColorWait', '--lme-bg-active'],
      ['titleTextColorProcess', '--lme-text-primary'],
      ['titleTextColorFinish', '--lme-text-primary'],
      ['titleTextColorError', '--lme-error'],
      ['titleTextColorWait', '--lme-text-muted'],
      ['descriptionTextColorProcess', '--lme-text-secondary'],
      ['descriptionTextColorWait', '--lme-text-disabled'],
      ['lineColor', '--lme-border'],
    ]),

    Result: pack([
      ['titleTextColor', '--lme-text-primary'],
      ['textColor', '--lme-text-secondary'],
      ['iconColorInfo', '--lme-info'],
      ['iconColorSuccess', '--lme-success'],
      ['iconColorWarning', '--lme-warning'],
      ['iconColorError', '--lme-error'],
    ]),

    Timeline: pack([
      ['lineColor', '--lme-border'],
      ['contentTextColor', '--lme-text-secondary'],
      ['titleTextColor', '--lme-text-primary'],
    ]),

    Image: pack([
      ['toolbarBoxShadow', '--lme-shadow-lg'],
      ['toolbarColor', '--lme-bg-elevated'],
      ['toolbarIconColor', '--lme-text-primary'],
    ]),

    Code: pack([
      ['textColor', '--lme-text-primary'],
      ['borderRadius', '--lme-radius-md'],
    ]),

    Log: pack([
      ['loaderFontSize', '--lme-font-size-sm'],
      ['loaderTextColor', '--lme-text-secondary'],
    ]),

    InputNumber: pack([
      ['border', '--lme-border'],
      ['borderHover', '--lme-border-strong'],
      ['borderFocus', '--lme-accent'],
      ['boxShadowFocus', '--lme-shadow-focus'],
    ]),

    Statistic: pack([
      ['labelTextColor', '--lme-text-muted'],
      ['valueTextColor', '--lme-text-primary'],
      ['valueFontSize', '--lme-font-size-2xl'],
    ]),

    Upload: pack([
      ['draggerColor', '--lme-bg-inset'],
      ['draggerBorder', '--lme-border-strong'],
      ['draggerBorderHover', '--lme-accent'],
      ['itemColor', '--lme-bg-panel'],
      ['itemColorHover', '--lme-bg-hover'],
      ['itemBorderRadius', '--lme-radius-md'],
      ['itemTextColor', '--lme-text-secondary'],
    ]),

    Cascader: pack([
      ['menuBoxShadow', '--lme-shadow-lg'],
      ['menuBorderRadius', '--lme-radius-lg'],
      ['optionTextColorActive', '--lme-accent'],
      ['optionColorPending', '--lme-bg-hover'],
    ]),

    AutoComplete: pack([
      ['menuBoxShadow', '--lme-shadow-lg'],
      ['peersBorderColorFocus', '--lme-accent'],
    ]),
  }
}

/** 取 Naive UI 基础主题：深色用自带 darkTheme，浅色用 null（库默认亮色） */
export function naiveBaseTheme(mode: ThemeMode): GlobalTheme | null {
  return mode === 'dark' ? darkTheme : null
}
