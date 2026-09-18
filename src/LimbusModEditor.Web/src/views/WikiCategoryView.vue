<script setup lang="ts">
// 维基分类页面（WikiCategoryView）
// 分类索引视图：标题+描述、搜索筛选、网格/列表、分页
// 数据来源：ipc.request('wiki.categoryIndex', { category })
// 布局：WikiShell 包裹；呈现层使用 Naive UI（ui-redesign r5，维基区主色为金）
// IPC 与深链（?category）零改动。

import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NAlert, NButton, NCard, NEmpty, NInput, NPagination, NSelect, NSpin, NTag } from 'naive-ui'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import type {
  CategoryIndex,
  CategoryPageRef,
  WikiPageCategory,
} from '@/ipc'

const route = useRoute()
const router = useRouter()

// ── 数据状态 ──
const categoryIndex = ref<CategoryIndex | null>(null)
const isLoading = ref(false)
const errorMessage = ref('')

// ── 搜索与筛选 ──
const searchText = ref('')
const sortKind = ref<'default' | 'title' | 'titleDesc'>('default')
const viewMode = ref<'grid' | 'list'>('grid')

// ── 分页 ──
const pageSize = 200
const currentPage = ref(1)

// ── 计算属性 ──

const category = computed(() => route.params.category as WikiPageCategory)

const categoryLabel = computed(() => {
  const map: Record<string, string> = {
    persona: '人格',
    enemy: '敌方单位',
    abnormality: '异想体',
    ego: 'E.G.O 装备',
    ego_gift: 'E.G.O 饰品',
    announcer: '播报员',
    story: '剧情',
    stage: '关卡',
    item: '物品',
    mechanism: '机制',
    keyword: '关键词',
  }
  return map[category.value] ?? category.value
})

const categoryDescription = computed(() => {
  const map: Record<string, string> = {
    persona: '编辑人格相关的信息框、分节、关联资源绑定。',
    enemy: '敌方单位属性、行为模式、掉落与关联机制。',
    abnormality: '异想体档案、事件选项、奖励与风险等级。',
    ego: 'E.G.O 装备的等级、属性、材料需求与获取方式。',
    ego_gift: 'E.G.O 饰品的稀有度、效果与装备位置。',
    announcer: '播报员语音包与触发条件配置。',
    story: '主线与支线剧情文本、分支与演出配置。',
    stage: '关卡布局、敌人编队、波次与胜利条件。',
    item: '消耗品、材料、钥匙物品及其效果描述。',
    mechanism: '游戏内核心机制说明、公式与数值表。',
    keyword: '关键词标签及其语义定义与关联规则。',
  }
  return map[category.value] ?? `${categoryLabel.value}分类下的所有维基页面。`
})

// 搜索过滤
const filteredPages = computed(() => {
  if (!categoryIndex.value) return []
  let pages = [...categoryIndex.value.pages]

  // 搜索过滤
  const keyword = searchText.value.trim().toLowerCase()
  if (keyword) {
    pages = pages.filter(
      (p) =>
        p.title.toLowerCase().includes(keyword) ||
        (p.subtitle ?? '').toLowerCase().includes(keyword),
    )
  }

  // 排序
  if (sortKind.value === 'title') {
    pages.sort((a, b) => a.title.localeCompare(b.title, 'zh-CN'))
  } else if (sortKind.value === 'titleDesc') {
    pages.sort((a, b) => b.title.localeCompare(a.title, 'zh-CN'))
  }

  return pages
})

const totalPages = computed(() => filteredPages.value.length)
const totalPageCount = computed(() => Math.ceil(totalPages.value / pageSize))

const paginatedPages = computed(() => {
  const start = (currentPage.value - 1) * pageSize
  return filteredPages.value.slice(start, start + pageSize)
})

// 排序下拉选项（NSelect 用；文案与原 <option> 逐字一致）
const sortOptions = [
  { label: '默认排序', value: 'default' },
  { label: '名称升序', value: 'title' },
  { label: '名称降序', value: 'titleDesc' },
]

// ── 方法 ──

async function loadCategoryIndex(cat: WikiPageCategory) {
  isLoading.value = true
  errorMessage.value = ''
  categoryIndex.value = null
  currentPage.value = 1
  searchText.value = ''
  sortKind.value = 'default'

  try {
    const result = await ipc.request<CategoryIndex>('wiki.categoryIndex', {
      category: cat,
    })
    categoryIndex.value = result
  } catch (e: unknown) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
    categoryIndex.value = {
      category: cat,
      label: categoryLabel.value,
      pages: [],
    }
  } finally {
    isLoading.value = false
  }
}

function goToPage(page: number) {
  if (page < 1 || page > totalPageCount.value) return
  currentPage.value = page
  // 滚动到顶部
  const el = document.querySelector('.wiki-category')
  if (el) el.scrollTop = 0
}

function goToFirstPage() {
  currentPage.value = 1
}

function navigateToPage(id: string) {
  router.push(`/wiki/page/${id}`)
}

// 封面加载失败 → 记入失效集合，卡片回落中性占位（不伪造图片）
const brokenCovers = ref(new Set<string>())

function onCoverError(pageId: string) {
  const next = new Set(brokenCovers.value)
  next.add(pageId)
  brokenCovers.value = next
}

function performSearch() {
  currentPage.value = 1
}

let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null

function onSearchInput() {
  if (searchDebounceTimer) clearTimeout(searchDebounceTimer)
  searchDebounceTimer = setTimeout(() => {
    performSearch()
  }, 200)
}

function sortLabel(kind: string): string {
  const map: Record<string, string> = {
    default: '默认排序',
    title: '名称升序',
    titleDesc: '名称降序',
  }
  return map[kind] ?? kind
}

// ── 监听路由参数变化 ──
watch(
  () => route.params.category,
  (newCat) => {
    if (newCat) {
      loadCategoryIndex(newCat as WikiPageCategory)
    }
  },
)

// ── 生命周期 ──
onMounted(() => {
  loadCategoryIndex(category.value)
})

onUnmounted(() => {
  if (searchDebounceTimer) clearTimeout(searchDebounceTimer)
})
</script>

<template>
  <WikiShell>
    <div class="wiki-category">
      <div class="wiki-category-container">
        <!-- ── 分类头部 ── -->
        <header class="category-header">
          <h1 class="category-title"><span class="title-prefix">分类:</span>{{ categoryLabel }}</h1>
          <p class="category-description">{{ categoryDescription }}</p>
        </header>

        <!-- ── 错误提示 ── -->
        <NAlert
          v-if="errorMessage"
          class="error-banner"
          type="error"
          :bordered="false"
          :title="errorMessage"
        />

        <!-- ── 工具栏：搜索 + 排序 + 视图切换 ── -->
        <div class="toolbar">
          <div class="toolbar-search">
            <NInput
              v-model:value="searchText"
              class="search-input"
              clearable
              :placeholder="`在「${categoryLabel}」中搜索页面…`"
              @input="onSearchInput"
            />
          </div>
          <div class="toolbar-controls">
            <label class="control-label">排序</label>
            <NSelect
              v-model:value="sortKind"
              class="control-select"
              size="small"
              :options="sortOptions"
            />
            <div class="view-toggle">
              <NButton
                class="toggle-btn"
                :class="{ active: viewMode === 'grid' }"
                :type="viewMode === 'grid' ? 'primary' : 'default'"
                size="small"
                title="网格视图"
                @click="viewMode = 'grid'"
              >
                ▦
              </NButton>
              <NButton
                class="toggle-btn"
                :class="{ active: viewMode === 'list' }"
                :type="viewMode === 'list' ? 'primary' : 'default'"
                size="small"
                title="列表视图"
                @click="viewMode = 'list'"
              >
                ☰
              </NButton>
            </div>
          </div>
        </div>

        <!-- ── 结果统计（对齐灰机措辞） ── -->
        <div v-if="isLoading" class="loading-state">
          <NSpin size="small" />
          <span>加载分类数据中…</span>
        </div>

        <div v-else class="results-info">
          <span v-if="searchText.trim()">
            找到 <strong>{{ totalPages }}</strong> 个匹配页面
          </span>
          <span v-else>
            以下 <strong>{{ totalPages }}</strong> 个页面属于本分类
          </span>
          <span v-if="totalPageCount > 1" class="page-info">
            · 第 {{ currentPage }} / {{ totalPageCount }} 页
          </span>
        </div>

        <!-- ── 网格视图 ── -->
        <div v-if="!isLoading && viewMode === 'grid'" class="page-grid">
          <NCard
            v-for="page in paginatedPages"
            :key="page.id"
            class="page-card"
            size="small"
            hoverable
            content-style="padding: 0;"
            @click="navigateToPage(page.id)"
          >
            <template #cover>
              <div class="page-thumb">
                <img
                  v-if="page.thumbnailUrl"
                  :src="page.thumbnailUrl"
                  :alt="page.title"
                  class="thumb-img"
                />
                <div v-else class="thumb-placeholder">
                  <span class="thumb-icon">📄</span>
                </div>
              </div>
            </template>
            <div class="page-info">
              <span class="page-title">{{ page.title }}</span>
              <span v-if="page.subtitle" class="page-subtitle">{{ page.subtitle }}</span>
            </div>
          </NCard>

          <!-- 空状态 -->
          <NEmpty
            v-if="paginatedPages.length === 0"
            class="empty-state"
            :description="searchText.trim() ? '未找到匹配页面' : '该分类暂无页面'"
          >
            <template #extra>
              <span class="empty-desc">
                {{ searchText.trim() ? '尝试调整搜索关键词' : '可从此处开始创建新页面' }}
              </span>
            </template>
          </NEmpty>
        </div>

        <!-- ── 列表视图 ── -->
        <div v-if="!isLoading && viewMode === 'list'" class="page-list">
          <div class="list-header">
            <span class="col-thumb">缩略图</span>
            <span class="col-title">标题</span>
            <span class="col-subtitle">副标题</span>
          </div>
          <div
            v-for="page in paginatedPages"
            :key="page.id"
            class="list-row"
            @click="navigateToPage(page.id)"
          >
            <div class="col-thumb">
              <div class="list-thumb">
                <img
                  v-if="page.thumbnailUrl && !brokenCovers.has(page.id)"
                  :src="page.thumbnailUrl"
                  :alt="page.title"
                  class="thumb-img"
                  @error="onCoverError(page.id)"
                />
                <span v-else class="thumb-icon-small">📄</span>
              </div>
            </div>
            <span class="col-title">{{ page.title }}</span>
            <span class="col-subtitle">{{ page.subtitle || '—' }}</span>
          </div>

          <!-- 空状态 -->
          <div v-if="paginatedPages.length === 0" class="empty-state-list">
            <NEmpty :description="searchText.trim() ? '未找到匹配页面' : '该分类暂无页面'" />
          </div>
        </div>

        <!-- ── 分页 ── -->
        <div v-if="!isLoading && totalPageCount > 1" class="pagination">
          <NButton
            class="page-btn"
            size="small"
            :disabled="currentPage <= 1"
            @click="goToPage(currentPage - 1)"
          >
            ‹ 上一页
          </NButton>
          <NButton
            class="page-btn"
            size="small"
            :disabled="currentPage <= 1"
            @click="goToFirstPage"
          >
            首页
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
            :disabled="currentPage >= totalPageCount"
            @click="goToPage(currentPage + 1)"
          >
            下一页 ›
          </NButton>
        </div>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.wiki-category {
  height: 100%;
  overflow-y: auto;
  background: var(--lme-bg-base);
}

.wiki-category-container {
  max-width: 1100px;
  margin: 0 auto;
  padding: var(--lme-gap-xl);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
}

/* ── 分类头部（对齐灰机分类页：金色大标题 + 描述 + 分隔线） ── */
.category-header {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding-bottom: var(--lme-gap-lg);
  border-bottom: 1px solid var(--wiki-section-head-border);
}

.category-title {
  margin: 0;
  font-size: var(--wiki-title-size);
  font-weight: 700;
  line-height: var(--wiki-title-line);
  color: var(--wiki-title);
}

.category-title .title-prefix {
  font-weight: 400;
  color: var(--wiki-hero-subtitle);
}

.category-description {
  margin: 0;
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-muted);
}

/* ── 错误提示 ── */
.error-banner {
  padding: var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  border-radius: var(--lme-radius-md);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
}

/* ── 工具栏 ── */
.toolbar {
  display: flex;
  gap: var(--lme-gap-md);
  align-items: center;
  flex-wrap: wrap;
}

.toolbar-search {
  flex: 1;
  min-width: 200px;
}

.search-input {
  width: 100%;
}

.toolbar-controls {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.control-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.control-select {
  width: 130px;
}

.view-toggle {
  display: flex;
  gap: 2px;
}

/* ── 加载状态 ── */
.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-xl);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-md);
}

/* ── 结果统计 ── */
.results-info {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.results-info strong {
  color: var(--lme-text-primary);
  font-weight: 600;
}

.page-info {
  color: var(--lme-text-muted);
}

/* ── 网格视图 ── */
.page-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--lme-gap-md);
}

.page-card {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  cursor: pointer;
}

/* NCard 自带卡面/描边/圆角（主题由 tokens 派生）；此处只补悬浮色与维基卡皮肤变量 */
.page-card:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
  transform: translateY(var(--wiki-card-lift));
}

.page-thumb {
  width: 100%;
  aspect-ratio: 16 / 9;
  background: var(--wiki-thumb-bg);
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;
}

.thumb-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.thumb-placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 100%;
}

.thumb-icon {
  font-size: 32px;
  opacity: 0.4;
  color: var(--wiki-thumb-icon);
}

/* 真实封面加载失败时也不伪造：降级为中性占位 */
.thumb-img {
  font-size: 0;
}

.thumb-img:not([src]) {
  display: none;
}

.page-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
}

.page-title {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.page-subtitle {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 列表视图 ── */
.page-list {
  display: flex;
  flex-direction: column;
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  overflow: hidden;
}

.list-header {
  display: flex;
  align-items: center;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-xs);
  font-weight: 600;
  color: var(--lme-text-muted);
  text-transform: uppercase;
}

.col-thumb {
  width: 64px;
  flex-shrink: 0;
}

.col-title {
  flex: 2;
  min-width: 120px;
}

.col-subtitle {
  flex: 3;
  min-width: 120px;
}

.list-row {
  display: flex;
  align-items: center;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  cursor: pointer;
  transition: background 0.15s;
}

.list-row:last-child {
  border-bottom: none;
}

.list-row:hover {
  background: var(--lme-bg-hover);
}

.list-thumb {
  width: 48px;
  height: 32px;
  border-radius: var(--lme-radius-sm);
  overflow: hidden;
  background: var(--wiki-thumb-bg);
  display: flex;
  align-items: center;
  justify-content: center;
}

.thumb-icon-small {
  font-size: var(--lme-font-size-md);
  opacity: 0.5;
}

.list-row .col-title {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.list-row .col-subtitle {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 空状态 ── */
.empty-state {
  grid-column: 1 / -1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-xl);
  text-align: center;
}

.empty-desc {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  max-width: 280px;
  line-height: 1.5;
}

.empty-state-list {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-xl);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
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

/* ── 响应式 ── */
@media (max-width: 900px) {
  .page-grid {
    grid-template-columns: repeat(3, 1fr);
  }
}

@media (max-width: 640px) {
  .page-grid {
    grid-template-columns: repeat(2, 1fr);
  }
  .toolbar {
    flex-direction: column;
    align-items: stretch;
  }
  .toolbar-controls {
    justify-content: flex-end;
  }
}
</style>
