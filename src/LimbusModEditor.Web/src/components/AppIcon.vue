<script setup lang="ts">
/**
 * 统一图标组件 —— 全应用唯一的图标出口（取代此前的 emoji）。
 *
 * - 颜色继承 `currentColor`，因此色值仍由 tokens.css 决定，调用点不写颜色。
 * - 尺寸/线宽可调，默认对齐 13px 正文（16px 图标 / 1.75 线宽）。
 * - 默认 `aria-hidden`：图标是装饰性的，可读名称由外层按钮的 title / aria-label 提供。
 *   语义图标请显式传 `label`，此时会带 `role="img"` 与 `aria-label`。
 */
import { computed } from 'vue'
import { ICONS, type IconName } from './icons'

const props = withDefaults(
  defineProps<{
    /** 语义图标名（见 components/icons.ts） */
    name: IconName
    /** 边长（px） */
    size?: number
    /** 描边线宽 */
    stroke?: number
    /** 传入后图标具备语义（读屏可读），否则视为装饰 */
    label?: string
  }>(),
  { size: 16, stroke: 1.75, label: undefined },
)

const component = computed(() => ICONS[props.name])
</script>

<template>
  <component
    :is="component"
    class="app-icon"
    :size="size"
    :stroke-width="stroke"
    :aria-hidden="label ? undefined : 'true'"
    :aria-label="label"
    :role="label ? 'img' : undefined"
    focusable="false"
  />
</template>

<style scoped>
.app-icon {
  flex-shrink: 0;
  vertical-align: middle;
  /* 与相邻文字基线对齐：lucide 图标为方形，下沉 1px 视觉更稳 */
  position: relative;
  top: -0.5px;
}
</style>
