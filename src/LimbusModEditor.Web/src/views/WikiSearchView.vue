<script setup lang="ts">
// 维基搜索结果页（与其它维基页统一：WikiShell 外壳 + Naive UI 呈现层）
// IPC：wiki.search（方法名与参数语义零改动，仅以既有 offset/limit 做服务端分页）
// 深链：?q=（零改动）
// 后端能力：WikiPageQueryService.SearchEntries → SQL LIMIT/OFFSET + COUNT，返回 total（真分页）
// ui-redesign r6：页头走 PageHeader，三态走 StateBlock，箭头符号换 AppIcon。
import { ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NButton, NInput, NList, NListItem, NPagination, NTag } from 'naive-ui'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import PageHeader from '@/components/PageHeader.vue'
import StateBlock from '@/components/StateBlock.vue'
import AppIcon from '@/components/AppIcon.vue'
import { WikiPageCategoryLabels } from '@/ipc/types'
import type { WikiSearchResult, WikiPageCategory, WikiSearchResponse } from '@/ipc'

const route = useRoute()
const router = useRouter()

// 每页条数：原实现一次性 limit:200，改为分页后每页 20 条
const PAGE_SIZE = 20

const keyword = ref((route.query.q as string) || '')
const input = ref(keyword.value)
const results = ref<WikiSearchResult[]>([])
const total = ref(0)
const currentPage = ref(1)
const loading = ref(false)
const searched = ref(false)

// 并发保护：仅最后一次请求可写入状态
let generation = 0

const totalPageCount = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

const breadcrumbs = computed(() => [
  { label: '维基', route: '/wiki' },
  { label: '搜索', route: '/wiki/search' },
])

function categoryLabel(category: WikiPageCategory): string {
  return WikiPageCategoryLabels[category] ?? category
}

async function doSearch(page = 1) {
  const q = keyword.value.trim()
  if (!q) return
  const gen = ++generation
  loading.value = true
  try {
    const result = await ipc.request<WikiSearchResponse>('wiki.search', {
      keyword: q,
      // 服务端分页：offset 为已跳过条数，limit 为每页条数
      offset: (page - 1) * PAGE_SIZE,
      limit: PAGE_SIZE,
    })
    if (gen !== generation) return
    results.value = result.results
    total.value = result.total
    currentPage.value = page
    searched.value = true
    // 同步地址栏，刷新/分享时保留关键词（深链零改动）
    if ((route.query.q as string) !== q) {
      router.replace({ path: '/wiki/search', query: { q } })
    }
  } catch {
    if (gen !== generation) return
    results.value = []
    total.value = 0
    currentPage.value = 1
    searched.value = true
  } finally {
    if (gen === generation) loading.value = false
  }
}

function submit() {
  keyword.value = input.value
  currentPage.value = 1
  doSearch(1)
}

function goToPage(page: number) {
  if (loading.value) return
  if (page < 1 || page > totalPageCount.value) return
  if (page === currentPage.value) return
  doSearch(page)
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
      currentPage.value = 1
      doSearch(1)
    }
  },
)

// 首次进入：带 q 就直接搜；没带 q 展示空态等待输入
if (keyword.value.trim()) doSearch(1)
</script>

<template>
  <WikiShell :breadcrumbs="breadcrumbs">
    <div class="wiki-search">
      <!-- 顶部细进度条（统一工具类 .lme-loadingbar） -->
      <div v-if="loading" class="lme-loadingbar" aria-hidden="true" />

      <PageHeader
        class="search-header"
        icon="search"
        title="搜索"
        description="在本地维基的全部页面里按标题与正文搜索。"
        hint="输入关键词后回车；结果按分类打标，点一行进入详情页。"
        hint-key="wiki-search"
      />

      <div class="wiki-search-container">
        <div class="search-box">
          <NInput
            v-model:value="input"
            class="search-input"
            placeholder="搜索维基页面…"
            clearable
            @keyup.enter="submit"
          />
          <NButton
            class="search-btn"
            type="primary"
            :disabled="!input.trim() || loading"
            @click="submit"
          >
            搜索
          </NButton>
        </div>

        <p v-if="searched && !loading" class="results-info">
          {{
            total > 0
              ? `找到 ${total} 个与「${keyword}」相关的页面 · 第 ${currentPage} / ${totalPageCount} 页`
              : `没有与「${keyword}」相关的页面`
          }}
        </p>

        <StateBlock
          v-if="loading"
          class="loading-state"
          state="loading"
          title="正在搜索…"
        />

        <StateBlock
          v-else-if="!keyword.trim()"
          class="empty-state"
          state="empty"
          icon="search"
          title="输入关键词开始搜索"
          description="支持页面标题与正文匹配；也可以先从左侧分类导航逐类浏览"
        />

        <StateBlock
          v-else-if="results.length === 0"
          class="empty-state"
          state="empty"
          icon="search"
          title="未找到匹配页面"
          description="换个更短的关键词（如只留名词），或从左侧分类页里逐条找"
        />

        <template v-else>
          <NList class="results-list" clickable hoverable>
            <NListItem
              v-for="r in results"
              :key="r.pageId"
              class="result-item"
              @click="navigateToPage(r.pageId)"
            >
              <div class="result-top">
                <span class="result-title">{{ r.title }}</span>
                <NTag class="result-category" size="small" :bordered="false">
                  {{ categoryLabel(r.category) }}
                </NTag>
              </div>
              <div v-if="r.snippet" class="result-snippet">{{ r.snippet }}</div>
            </NListItem>
          </NList>

          <div v-if="totalPageCount > 1" class="pagination">
            <NButton
              class="page-btn"
              size="small"
              :disabled="currentPage <= 1 || loading"
              @click="goToPage(currentPage - 1)"
            >
              <AppIcon name="chevronLeft" :size="13" /> 上一页
            </NButton>
            <NPagination
              class="page-pager"
              :page="currentPage"
              :page-count="totalPageCount"
              :page-slot="7"
              size="small"
              show-quick-jumper
              @update:page="goToPage"
            />
            <NButton
              class="page-btn"
              size="small"
              :disabled="currentPage >= totalPageCount || loading"
              @click="goToPage(currentPage + 1)"
            >
              下一页 <AppIcon name="chevronRight" :size="13" />
            </NButton>
            <span class="page-info">共 {{ total }} 条 / 第 {{ currentPage }} 页</span>
          </div>
        </template>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.wiki-search {
  position: relative;
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

/* ── 页头：改用统一 PageHeader（.search-header 保留作回归定位） ── */

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
}

.search-btn {
  white-space: nowrap;
}

/* 三态由 StateBlock 呈现，这里只留页内间距 */
.loading-state {
  padding: var(--lme-gap-xl);
}

.empty-state {
  padding: var(--lme-gap-xl);
}

.results-list {
  background: transparent;
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

/* 分类胶囊：NTag 套 wiki-chip 皮肤（跟随全局强调色） */
.result-category {
  flex-shrink: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-chip-text);
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  border-radius: var(--wiki-chip-radius);
}

.result-snippet {
  font-size: var(--lme-font-size-sm);
  color: var(--wiki-result-snippet);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* ── 分页 ── */
.pagination {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md) 0;
  flex-wrap: wrap;
}

.page-pager {
  margin-left: var(--lme-gap-xs);
}

/* 翻页按钮内的图标与文字间距 */
.page-btn :deep(.n-button__content) {
  gap: var(--lme-gap-2xs);
}

.page-info {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}
</style>
