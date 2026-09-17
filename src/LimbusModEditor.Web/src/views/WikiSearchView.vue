<script setup lang="ts">
// 维基搜索结果页（与其它维基页统一：WikiShell 外壳 + 同一套标题/输入框/空态样式）
import { ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import { WikiPageCategoryLabels } from '@/ipc/types'
import type { WikiSearchResult, WikiPageCategory } from '@/ipc'

const route = useRoute()
const router = useRouter()

const keyword = ref((route.query.q as string) || '')
const input = ref(keyword.value)
const results = ref<WikiSearchResult[]>([])
const total = ref(0)
const loading = ref(false)
const searched = ref(false)

const breadcrumbs = computed(() => [
  { label: '维基', route: '/wiki' },
  { label: '搜索', route: '/wiki/search' },
])

function categoryLabel(category: WikiPageCategory): string {
  return WikiPageCategoryLabels[category] ?? category
}

async function doSearch() {
  const q = keyword.value.trim()
  if (!q) return
  loading.value = true
  try {
    const result = await ipc.request<{ total: number; results: WikiSearchResult[] }>(
      'wiki.search',
      { keyword: q, offset: 0, limit: 200 },
    )
    results.value = result.results
    total.value = result.total
    searched.value = true
    // 同步地址栏，刷新/分享时保留关键词
    if ((route.query.q as string) !== q) {
      router.replace({ path: '/wiki/search', query: { q } })
    }
  } catch {
    results.value = []
    total.value = 0
    searched.value = true
  } finally {
    loading.value = false
  }
}

function submit() {
  keyword.value = input.value
  doSearch()
}

function navigateToPage(id: string) {
  router.push(`/wiki/page/${id}`)
}

watch(
  () => route.query.q,
  (q) => {
    const next = (q as string) || ''
    if (next && next !== keyword.value) {
      keyword.value = next
      input.value = next
      doSearch()
    }
  },
)

// 首次进入：带 q 就直接搜；没带 q 展示空态等待输入
if (keyword.value.trim()) doSearch()
</script>

<template>
  <WikiShell :breadcrumbs="breadcrumbs">
    <div class="wiki-search">
      <div class="wiki-search-container">
        <header class="search-header">
          <h1 class="page-title">搜索</h1>
          <p v-if="searched && !loading" class="results-info">
            {{ total > 0 ? `找到 ${total} 个与「${keyword}」相关的页面` : `没有与「${keyword}」相关的页面` }}
          </p>
        </header>

        <div class="search-box">
          <input
            v-model="input"
            class="search-input"
            type="text"
            placeholder="搜索维基页面…"
            @keyup.enter="submit"
          />
          <button class="search-btn" :disabled="!input.trim() || loading" @click="submit">
            搜索
          </button>
        </div>

        <div v-if="loading" class="loading-state">搜索中…</div>

        <div v-else-if="!keyword.trim()" class="empty-state">
          <div class="empty-title">输入关键词开始搜索</div>
          <div class="empty-desc">支持页面标题与内容匹配</div>
        </div>

        <div v-else-if="results.length === 0" class="empty-state">
          <div class="empty-title">未找到匹配页面</div>
          <div class="empty-desc">尝试调整搜索关键词</div>
        </div>

        <div v-else class="results-list">
          <div
            v-for="r in results"
            :key="r.pageId"
            class="result-item"
            @click="navigateToPage(r.pageId)"
          >
            <div class="result-top">
              <span class="result-title">{{ r.title }}</span>
              <span class="result-category">{{ categoryLabel(r.category) }}</span>
            </div>
            <div v-if="r.snippet" class="result-snippet">{{ r.snippet }}</div>
          </div>
        </div>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.wiki-search {
  height: 100%;
  overflow-y: auto;
  background: var(--lme-bg-base);
}

.wiki-search-container {
  max-width: var(--wiki-content-max-width);
  margin: 0 auto;
  padding: var(--lme-gap-xl);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
}

.search-header {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  padding-bottom: var(--lme-gap-md);
  border-bottom: 1px solid var(--wiki-section-head-border);
}

.page-title {
  margin: 0;
  font-size: var(--wiki-title-size);
  font-weight: 700;
  line-height: var(--wiki-title-line);
  color: var(--wiki-title);
}

.results-info {
  margin: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.search-box {
  display: flex;
  gap: var(--lme-gap-sm);
}

.search-input {
  flex: 1;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-md);
  font-family: var(--lme-font-family);
}

.search-input:focus {
  outline: none;
  border-color: var(--wiki-accent);
}

.search-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--wiki-accent);
  border: 1px solid var(--wiki-accent);
  border-radius: var(--lme-radius-sm);
  color: var(--wiki-chip-active-text);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  white-space: nowrap;
  transition: background 0.15s;
}

.search-btn:hover:not(:disabled) {
  background: var(--wiki-accent-strong);
}

.search-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-xl);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-md);
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-xl);
  text-align: center;
}

.empty-title {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.empty-desc {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.results-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.result-item {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-md);
  background: var(--wiki-card-bg);
  border: 1px solid var(--wiki-card-border);
  border-radius: var(--lme-radius-md);
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}

.result-item:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
}

.result-top {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
}

.result-title {
  flex: 1;
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--wiki-result-title);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.result-item:hover .result-title {
  text-decoration: underline;
}

.result-category {
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-chip-text);
  padding: 1px 8px;
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  border-radius: var(--wiki-chip-radius);
  flex-shrink: 0;
}

.result-snippet {
  font-size: var(--lme-font-size-sm);
  color: var(--wiki-result-snippet);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
</style>
