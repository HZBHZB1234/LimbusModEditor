<script setup lang="ts">
// 页码条：服务端分页导航（只服务列表视图）
// ui-redesign r6：手写按钮/输入框换成 NButton / NInput，emoji 换 AppIcon；
// props / emits（go-to 的 0-based 语义）保持不变

import { ref, computed } from 'vue'
import { NButton, NInput } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'

const props = defineProps<{
  currentPage: number // 1-based
  pageCount: number
  totalCount: number
  queryMs: number
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'go-to', page: number): void // 0-based
}>()

const jumpInput = ref('')

const displayCurrent = computed(() => props.currentPage)
const displayPageCount = computed(() => props.pageCount)

function jump() {
  const n = parseInt(jumpInput.value, 10)
  if (!isNaN(n) && n >= 1 && n <= props.pageCount) {
    emit('go-to', n - 1)
  }
  jumpInput.value = ''
}
</script>

<template>
  <div class="page-bar">
    <NButton
      class="page-btn"
      size="small"
      :disabled="currentPage <= 1 || loading"
      @click="emit('go-to', 0)"
    >
      <AppIcon name="skipBack" :size="14" />
      首页
    </NButton>
    <NButton
      class="page-btn"
      size="small"
      :disabled="currentPage <= 1 || loading"
      @click="emit('go-to', currentPage - 2)"
    >
      <AppIcon name="chevronLeft" :size="14" />
      上一页
    </NButton>

    <span class="page-info">
      第 <strong>{{ displayCurrent }}</strong> / {{ displayPageCount }} 页
    </span>

    <span class="total-info">共 {{ totalCount.toLocaleString('zh-CN') }} 条</span>

    <div class="page-jump">
      <NInput
        v-model:value="jumpInput"
        class="jump-input"
        size="small"
        placeholder="页码"
        :style="{ width: '52px' }"
        @keydown.enter="jump"
      />
      <NButton class="page-btn small" size="small" @click="jump">跳转</NButton>
    </div>

    <NButton
      class="page-btn"
      size="small"
      :disabled="currentPage >= pageCount || loading"
      @click="emit('go-to', currentPage)"
    >
      下一页
      <AppIcon name="chevronRight" :size="14" />
    </NButton>
    <NButton
      class="page-btn"
      size="small"
      :disabled="currentPage >= pageCount || loading"
      @click="emit('go-to', pageCount - 1)"
    >
      末页
      <AppIcon name="skipForward" :size="14" />
    </NButton>

    <span v-if="queryMs > 0" class="query-time lme-mono">{{ queryMs.toFixed(0) }} ms</span>
  </div>
</template>

<style scoped>
.page-bar {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-top: 1px solid var(--lme-border);
  flex-wrap: wrap;
}

/* 分页按钮（库组件 NButton：底色/边框/圆角由 naiveTheme.ts 从 tokens 派生） */
.page-btn {
  font-size: var(--lme-font-size-sm);
}

.page-btn :deep(.app-icon) {
  margin: 0 var(--lme-gap-2xs);
}

.page-info,
.total-info {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.page-info strong {
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-semibold);
}

.page-jump {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
}

/* 页码输入（库组件 NInput，宽度在模板里给 52px） */
.jump-input :deep(.n-input__input-el) {
  text-align: center;
  font-family: var(--lme-font-mono);
}

.query-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-left: auto;
}
</style>
