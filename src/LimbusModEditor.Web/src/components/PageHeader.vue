<script setup lang="ts">
/**
 * 页头 —— 每个工作台顶部的统一标题区。
 *
 * 组成：图标 + 标题 + 一句话说明（这个页面能做什么）+ 右侧操作区 + 可选指引条。
 * 目的是让用户进入任何一页都能立刻知道「我在哪、这里能做什么、下一步点什么」。
 *
 * 指引条（hint）可关闭，关闭状态按页面 key 记在 localStorage，不打扰回头客。
 */
import { computed, ref } from 'vue'
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'

const props = withDefaults(
  defineProps<{
    /** 页面图标 */
    icon: IconName
    /** 页面标题 */
    title: string
    /** 一句话说明：本页能做什么 */
    description?: string
    /** 操作指引：建议的下一步（可关闭） */
    hint?: string
    /** 指引条语气 */
    hintTone?: 'info' | 'warning'
    /** 指引条关闭记忆键；给了才允许关闭并持久化 */
    hintKey?: string
    /** 紧凑模式（用于嵌在卡片内的次级标题） */
    compact?: boolean
  }>(),
  { hintTone: 'info', compact: false, description: undefined, hint: undefined, hintKey: undefined },
)

const HINT_STORE_PREFIX = 'lme.hint.dismissed.'

function hintDismissed(key: string): boolean {
  try {
    return localStorage.getItem(HINT_STORE_PREFIX + key) === '1'
  } catch {
    return false
  }
}

const hintClosed = ref(props.hintKey ? hintDismissed(props.hintKey) : false)

const showHint = computed(() => Boolean(props.hint) && !hintClosed.value)

function dismissHint() {
  hintClosed.value = true
  if (props.hintKey) {
    try {
      localStorage.setItem(HINT_STORE_PREFIX + props.hintKey, '1')
    } catch {
      /* 忽略写入失败 */
    }
  }
}
</script>

<template>
  <header class="page-header" :class="{ compact }">
    <div class="ph-main">
      <span class="ph-icon">
        <AppIcon :name="icon" :size="compact ? 15 : 17" :stroke="1.9" />
      </span>

      <div class="ph-titles">
        <h1 class="ph-title">{{ title }}</h1>
        <p v-if="description" class="ph-desc">{{ description }}</p>
      </div>

      <div class="ph-meta">
        <slot name="meta" />
      </div>

      <div class="ph-actions">
        <slot name="actions" />
      </div>
    </div>

    <div v-if="showHint" class="ph-hint" :class="'tone-' + hintTone">
      <AppIcon :name="hintTone === 'warning' ? 'warning' : 'hint'" :size="14" class="ph-hint-icon" />
      <span class="ph-hint-text">{{ hint }}</span>
      <slot name="hint-actions" />
      <button v-if="hintKey" class="ph-hint-close" title="不再提示" @click="dismissHint">
        <AppIcon name="close" :size="13" />
      </button>
    </div>
  </header>
</template>

<style scoped>
.page-header {
  flex-shrink: 0;
  background: var(--lme-toolbar-bg);
  border-bottom: 1px solid var(--lme-toolbar-border);
}

.ph-main {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  min-height: var(--lme-toolbar-height);
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
}

.compact .ph-main {
  min-height: 0;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
}

.ph-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-md);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
}

.compact .ph-icon {
  width: 24px;
  height: 24px;
}

.ph-titles {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.ph-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  line-height: var(--lme-line-height-tight);
  color: var(--lme-text-primary);
  white-space: nowrap;
}

.compact .ph-title {
  font-size: var(--lme-font-size-md);
}

.ph-desc {
  margin: 0;
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ph-meta {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin-left: var(--lme-gap-sm);
  flex-shrink: 0;
}

.ph-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin-left: auto;
  flex-shrink: 0;
}

/* ── 指引条 ── */
.ph-hint {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 6px var(--lme-gap-lg);
  border-top: 1px solid var(--lme-border-subtle);
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
}

.ph-hint.tone-info {
  background: var(--lme-accent-subtle);
  color: var(--lme-text-secondary);
}

.ph-hint.tone-info .ph-hint-icon {
  color: var(--lme-accent);
}

.ph-hint.tone-warning {
  background: var(--lme-warning-subtle);
  color: var(--lme-text-secondary);
}

.ph-hint.tone-warning .ph-hint-icon {
  color: var(--lme-warning);
}

.ph-hint-text {
  flex: 1;
  min-width: 0;
}

.ph-hint-close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  padding: 0;
  border: none;
  border-radius: var(--lme-radius-xs);
  background: none;
  color: var(--lme-text-muted);
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.ph-hint-close:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}
</style>
