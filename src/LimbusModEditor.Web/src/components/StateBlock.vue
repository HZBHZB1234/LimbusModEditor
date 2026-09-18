<script setup lang="ts">
/**
 * 状态块 —— 加载 / 空 / 错误三种状态的统一呈现。
 *
 * 相比裸 `NEmpty`，这里强制要求给一句「下一步该做什么」的指引（description），
 * 避免用户看到空白后不知道怎么办。
 */
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'

withDefaults(
  defineProps<{
    /** 状态类型 */
    state: 'loading' | 'empty' | 'error'
    /** 主标题（空/错误必填；加载中可选） */
    title?: string
    /** 指引文案：告诉用户下一步做什么 */
    description?: string
    /** 覆盖默认图标 */
    icon?: IconName
  }>(),
  { title: undefined, description: undefined, icon: undefined },
)
</script>

<template>
  <div class="state-block" :class="'state-' + state">
    <!-- 加载中：转圈 -->
    <span v-if="state === 'loading'" class="sb-spinner">
      <AppIcon name="loader" :size="22" />
    </span>

    <!-- 空 / 错误：图标徽章 -->
    <span v-else class="sb-badge">
      <AppIcon
        :name="icon ?? (state === 'error' ? 'error' : 'search')"
        :size="22"
        :stroke="1.6"
      />
    </span>

    <p v-if="title" class="sb-title">{{ title }}</p>
    <p v-if="description" class="sb-desc">{{ description }}</p>

    <div v-if="$slots.actions" class="sb-actions">
      <slot name="actions" />
    </div>

    <slot />
  </div>
</template>

<style scoped>
.state-block {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-2xl) var(--lme-gap-lg);
  text-align: center;
  color: var(--lme-text-muted);
}

/* 铺满容器时用（列表/预览区整体空态） */
.state-block.fill {
  flex: 1;
  min-height: 0;
}

.sb-spinner {
  display: inline-flex;
  color: var(--lme-accent);
  animation: lme-spin 1.1s linear infinite;
}

.sb-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  border-radius: var(--lme-radius-full);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  color: var(--lme-text-disabled);
}

.state-error .sb-badge {
  background: var(--lme-error-subtle);
  border-color: transparent;
  color: var(--lme-error);
}

.sb-title {
  margin: 0;
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-secondary);
  line-height: var(--lme-line-height-tight);
}

.state-error .sb-title {
  color: var(--lme-error);
}

.sb-desc {
  margin: 0;
  max-width: 460px;
  font-size: var(--lme-font-size-sm);
  line-height: var(--lme-line-height-relaxed);
  color: var(--lme-text-muted);
}

.sb-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-xs);
  flex-wrap: wrap;
  justify-content: center;
}
</style>
