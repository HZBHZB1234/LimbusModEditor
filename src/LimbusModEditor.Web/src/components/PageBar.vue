<script setup lang="ts">
// 页码条：服务端分页导航（只服务列表视图）

import { ref, computed } from 'vue'

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
    <button class="page-btn" :disabled="currentPage <= 1 || loading" @click="emit('go-to', 0)">
      ⏮ 首页
    </button>
    <button
      class="page-btn"
      :disabled="currentPage <= 1 || loading"
      @click="emit('go-to', currentPage - 2)"
    >
      ◀ 上一页
    </button>

    <span class="page-info">
      第 <strong>{{ displayCurrent }}</strong> / {{ displayPageCount }} 页
    </span>

    <span class="total-info">共 {{ totalCount.toLocaleString('zh-CN') }} 条</span>

    <div class="page-jump">
      <input
        v-model="jumpInput"
        type="text"
        class="jump-input"
        placeholder="页码"
        @keydown.enter="jump"
      />
      <button class="page-btn small" @click="jump">跳转</button>
    </div>

    <button
      class="page-btn"
      :disabled="currentPage >= pageCount || loading"
      @click="emit('go-to', currentPage)"
    >
      下一页 ▶
    </button>
    <button
      class="page-btn"
      :disabled="currentPage >= pageCount || loading"
      @click="emit('go-to', pageCount - 1)"
    >
      末页 ⏭
    </button>

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

.page-btn {
  padding: 3px 10px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard),
    border-color var(--lme-dur-fast) var(--lme-ease-standard);
}

.page-btn:hover:not(:disabled) {
  background: var(--lme-bg-hover);
  border-color: var(--lme-border-strong);
  color: var(--lme-text-primary);
}

.page-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.page-btn.small {
  padding: 2px 8px;
}

.page-info,
.total-info {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.page-info strong {
  color: var(--lme-text-primary);
}

.page-jump {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
}

.jump-input {
  width: 48px;
  padding: 2px 4px;
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  text-align: center;
  font-size: var(--lme-font-size-sm);
}

.query-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-left: auto;
}
</style>
