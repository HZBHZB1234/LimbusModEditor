<script setup lang="ts">
// 维基提示块：页首「需要改进」/注意事项等，左侧竖条
// 纯展示；文本为空时整块不渲染（不占位、不编造）
// ui-redesign r6：图标改走 AppIcon（语义名），不再接受 emoji 字符。
import { computed } from 'vue'
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'

const props = withDefaults(
  defineProps<{
    /** 提示正文；空/纯空白时不渲染 */
    text: string
    /** 形态：info（默认，强调色竖条）| warning（警示） */
    variant?: 'info' | 'warning'
    /** 可选图标（语义名，见 components/icons.ts），缺省不显示 */
    icon?: IconName
  }>(),
  { variant: 'info' },
)

const hasText = computed(() => props.text.trim() !== '')
</script>

<template>
  <div v-if="hasText" class="wiki-notice" :class="variant">
    <span v-if="icon" class="wiki-notice-icon">
      <AppIcon :name="icon" :size="16" />
    </span>
    <p class="wiki-notice-text">{{ text }}</p>
  </div>
</template>

<style scoped>
.wiki-notice {
  display: flex;
  align-items: flex-start;
  gap: var(--lme-gap-md);
  margin: 0 0 var(--lme-gap-xl);
  padding: var(--lme-gap-md) var(--lme-gap-lg);
  background: var(--wiki-notice-bg);
  border-left: 4px solid var(--wiki-notice-border);
  border-radius: 0 var(--lme-radius-md) var(--lme-radius-md) 0;
}

.wiki-notice.warning {
  border-left-color: var(--lme-warning);
}

.wiki-notice.warning .wiki-notice-icon {
  color: var(--lme-warning);
}

.wiki-notice-icon {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  margin-top: 3px;
  color: var(--lme-accent);
}

.wiki-notice-text {
  margin: 0;
  line-height: 1.7;
  color: var(--wiki-notice-text);
}
</style>
