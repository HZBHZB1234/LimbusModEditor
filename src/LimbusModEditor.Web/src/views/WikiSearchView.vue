<script setup lang="ts">
// 维基搜索结果页

import { ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { ipc } from '@/ipc'
import type { WikiSearchResult } from '@/ipc'

const route = useRoute()
const keyword = ref((route.query.q as string) || '')
const results = ref<WikiSearchResult[]>([])
const loading = ref(false)

async function doSearch() {
  if (!keyword.value.trim()) return
  loading.value = true
  try {
    const result = await ipc.request<{ total: number; results: WikiSearchResult[] }>(
      'wiki.search',
      { keyword: keyword.value, offset: 0, limit: 200 },
    )
    results.value = result.results
  } catch {
    results.value = []
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  if (keyword.value) doSearch()
})
</script>

<template>
  <div class="wiki-search">
    <h2 class="page-title">搜索</h2>
    <div class="search-box">
      <input
        v-model="keyword"
        class="search-input"
        type="text"
        placeholder="搜索维基页面..."
        @keydown.enter="doSearch"
      />
      <button class="search-btn" @click="doSearch">搜索</button>
    </div>
    <div v-if="loading" class="loading">搜索中...</div>
    <div v-else-if="results.length === 0 && keyword" class="empty">无结果</div>
    <div v-else class="results-list">
      <div v-for="r in results" :key="r.pageId" class="result-item">
        <div class="result-title">{{ r.title }}</div>
        <div class="result-snippet">{{ r.snippet }}</div>
        <div class="result-category">{{ r.category }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.wiki-search { padding: var(--lme-gap-lg); }
.page-title { font-size: var(--lme-font-size-xl); font-weight: 600; color: var(--lme-text-primary); margin: 0 0 var(--lme-gap-md); }
.search-box { display: flex; gap: var(--lme-gap-sm); margin-bottom: var(--lme-gap-lg); }
.search-input { flex: 1; padding: var(--lme-gap-sm) var(--lme-gap-md); background: var(--lme-bg-input); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-sm); color: var(--lme-text-primary); font-size: var(--lme-font-size-md); }
.search-btn { padding: var(--lme-gap-sm) var(--lme-gap-lg); background: var(--lme-accent); border: none; border-radius: var(--lme-radius-sm); color: #fff; cursor: pointer; font-size: var(--lme-font-size-sm); }
.loading, .empty { color: var(--lme-text-muted); padding: var(--lme-gap-lg); text-align: center; }
.results-list { display: flex; flex-direction: column; gap: var(--lme-gap-sm); }
.result-item { padding: var(--lme-gap-md); background: var(--lme-bg-panel); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-md); }
.result-title { font-size: var(--lme-font-size-md); font-weight: 600; color: var(--lme-text-primary); }
.result-snippet { font-size: var(--lme-font-size-sm); color: var(--lme-text-secondary); margin-top: var(--lme-gap-xs); }
.result-category { font-size: var(--lme-font-size-xs); color: var(--lme-text-muted); margin-top: var(--lme-gap-xs); }
</style>
