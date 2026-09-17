<script setup lang="ts">
// 维基首页（WikiHomeView）
// Hero 区域 + 分类卡片网格 + 最近编辑 + 统计概览
// 数据来源：ipc.request('wiki.home', {})
// 布局：WikiShell 包裹

import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import type {
  HomeData,
  HomeCategoryCard,
  HomeRecentPage,
  WikiStats,
  ProgressPayload,
  WikiGenerateResponse,
  WikiGenerateStatusResponse,
  WikiPageCategory,
} from '@/ipc'
import { WikiPageCategoryLabels } from '@/ipc/types'

const router = useRouter()

// ── 数据状态 ──
const homeData = ref<HomeData | null>(null)
const isLoading = ref(false)
const errorMessage = ref('')

// 搜索
const searchKeyword = ref('')

// ── 页面生成（wiki.generate）──
// 触发点选「Wiki 首页显式按钮」而不是「打开项目后自动跑」：
// 真实数据下生成要读 127 万条资源行，耗时以分钟计，放在启动路径上会拖慢打开项目；
// 而维基页是派生内容，用户点一下再生成既可控又可看进度。首次进入页面时若库里还没有
// 内容（generateStatus.ready = false），就用横幅提示，而不是偷偷后台跑。
const isGenerating = ref(false)
const generateMessage = ref('')
const generateError = ref('')
const generateProgress = ref<ProgressPayload | null>(null)
const generateStatus = ref<WikiGenerateStatusResponse | null>(null)
const generateOperationId = 'wiki-generate'

// ── 方法 ──

async function loadHomeData() {
  isLoading.value = true
  errorMessage.value = ''
  try {
    const result = await ipc.request<HomeData>('wiki.home', {})
    homeData.value = result
  } catch (e: unknown) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
    // 离线降级：展示骨架占位
    homeData.value = {
      categories: [],
      recentPages: [],
      stats: { totalPages: 0, totalEntries: 0, totalBindings: 0, userEdits: 0 },
    }
  } finally {
    isLoading.value = false
  }
}

function navigateToCategory(category: string) {
  router.push(`/wiki/category/${category}`)
}

function navigateToPage(id: string) {
  router.push(`/wiki/page/${id}`)
}

function performSearch() {
  const keyword = searchKeyword.value.trim()
  if (!keyword) return
  router.push({ path: '/wiki/search', query: { q: keyword } })
}

function formatTime(timestamp: string): string {
  try {
    const date = new Date(timestamp)
    return date.toLocaleString('zh-CN')
  } catch {
    return timestamp
  }
}

async function loadGenerateStatus() {
  try {
    generateStatus.value = await ipc.request<WikiGenerateStatusResponse>('wiki.generateStatus', {})
  } catch {
    // 状态只是提示，拿不到就不提示（不扰民）
    generateStatus.value = null
  }
}

async function generatePages() {
  if (isGenerating.value) return
  isGenerating.value = true
  generateError.value = ''
  generateMessage.value = ''
  generateProgress.value = null
  try {
    const result = await ipc.request<WikiGenerateResponse>(
      'wiki.generate',
      { operationId: generateOperationId },
      // 真实数据下生成要扫 127 万条资源行（实测约 70 秒），默认 30 秒超时不够；
      // 期间进度由 progress 事件回传，所以给一个宽松上限即可。
      900000,
    )
    generateMessage.value = result.message
    await loadHomeData()
    await loadGenerateStatus()
  } catch (e: unknown) {
    generateError.value = e instanceof Error ? e.message : String(e)
  } finally {
    isGenerating.value = false
    generateProgress.value = null
  }
}

// 11 个维基分类的固定顺序（与侧栏一致）；后端只回传有内容的分类，
// 缺席的分类照常给入口卡片，但计数显示「本地无来源」而不是假数字。
const ALL_CATEGORIES = Object.keys(WikiPageCategoryLabels) as WikiPageCategory[]

const CATEGORY_ICONS: Record<WikiPageCategory, string> = {
  persona: '🎭',
  enemy: '👹',
  abnormality: '🌀',
  ego: '⚔️',
  ego_gift: '💍',
  announcer: '📢',
  story: '📖',
  stage: '🗺️',
  item: '🎒',
  mechanism: '⚙️',
  keyword: '🔑',
}

function categoryLabel(category: WikiPageCategory): string {
  return WikiPageCategoryLabels[category] ?? category
}

function categoryIcon(category: WikiPageCategory): string {
  return CATEGORY_ICONS[category] ?? '📄'
}

/** 后端计数按分类索引；只信真实回传，不补零 */
const countByCategory = computed(() => {
  const map = new Map<string, HomeCategoryCard>()
  for (const card of homeData.value?.categories ?? []) map.set(card.category, card)
  return map
})

const categoryCards = computed(() =>
  ALL_CATEGORIES.map((category) => ({
    category,
    label: categoryLabel(category),
    icon: categoryIcon(category),
    card: countByCategory.value.get(category) ?? null,
  })),
)

// ── 进度事件订阅（只认自己那次生成）──
let unsubscribeProgress: (() => void) | null = null

// ── 初始化 ──
onMounted(() => {
  unsubscribeProgress = ipc.on('progress', (payload) => {
    const progress = payload as ProgressPayload
    if (progress.operationId !== generateOperationId) return
    generateProgress.value = progress
  })
  loadHomeData()
  loadGenerateStatus()
})

onUnmounted(() => {
  unsubscribeProgress?.()
})
</script>

<template>
  <WikiShell>
    <div class="wiki-home">
      <div class="wiki-home-container">
        <!-- ── Hero 区域（对齐灰机首页：暗底横幅 + 金色大标题） ── -->
        <section class="hero-section">
          <div class="hero-content">
            <h1 class="hero-title">Limbus Company 中文维基</h1>
            <p class="hero-subtitle">
              边狱公司模组编辑知识库 · 人格、E.G.O、敌方单位与剧情百科 · 数据来自本地游戏文件
            </p>
          </div>
          <div class="hero-search">
            <input
              v-model="searchKeyword"
              class="search-input"
              type="text"
              placeholder="搜索维基页面、人格、异想体、关键词…"
              @keyup.enter="performSearch"
            />
            <button class="search-btn" :disabled="!searchKeyword.trim()" @click="performSearch">
              搜索
            </button>
          </div>
          <!-- 生成入口：维基页是派生内容，按需生成（说明见脚本区注释） -->
          <div class="hero-generate">
            <button class="generate-btn" :disabled="isGenerating" @click="generatePages">
              {{ isGenerating ? '正在生成…' : '生成页面' }}
            </button>
            <span v-if="generateStatus" class="generate-hint">
              {{
                generateStatus.ready
                  ? `已生成 ${generateStatus.pages} 个页面（${generateStatus.entries} 条）`
                  : '还没有生成过页面'
              }}
            </span>
          </div>
        </section>

        <!-- ── 生成进度 ── -->
        <div v-if="isGenerating" class="generate-progress">
          <div class="progress-text">
            {{ generateProgress?.message ?? '正在准备…' }}
            <span v-if="generateProgress?.total" class="progress-count">
              {{ generateProgress.current }}/{{ generateProgress.total }}
            </span>
          </div>
          <div class="progress-track">
            <div
              class="progress-bar"
              :style="{
                width: generateProgress?.total
                  ? `${Math.round((generateProgress.current / generateProgress.total) * 100)}%`
                  : '0%',
              }"
            />
          </div>
        </div>

        <!-- ── 生成结果 / 失败 ── -->
        <div v-if="generateMessage" class="generate-result">✅ {{ generateMessage }}</div>
        <div v-if="generateError" class="error-banner">⚠️ {{ generateError }}</div>

        <!-- ── 错误提示 ── -->
        <div v-if="errorMessage" class="error-banner">
          ⚠️ {{ errorMessage }}
        </div>

        <!-- ── 加载中 ── -->
        <div v-if="isLoading" class="loading-state">
          <span>加载中…</span>
        </div>

        <!-- ── 统计概览 ── -->
        <section v-if="homeData" class="stats-section">
          <div class="stats-grid">
            <div class="stat-card">
              <span class="stat-value">{{ homeData.stats.totalPages.toLocaleString('zh-CN') }}</span>
              <span class="stat-label">总页面数</span>
            </div>
            <div class="stat-card">
              <span class="stat-value">{{ homeData.stats.totalEntries.toLocaleString('zh-CN') }}</span>
              <span class="stat-label">总条目数</span>
            </div>
            <div class="stat-card">
              <span class="stat-value">{{ homeData.stats.totalBindings.toLocaleString('zh-CN') }}</span>
              <span class="stat-label">资源绑定</span>
            </div>
            <div class="stat-card">
              <span class="stat-value">{{ homeData.stats.userEdits.toLocaleString('zh-CN') }}</span>
              <span class="stat-label">用户编辑</span>
            </div>
          </div>
        </section>

        <!-- ── 分类卡片网格 ── -->
        <section class="categories-section">
          <h2 class="section-title">分类浏览</h2>
          <div class="category-grid">
            <div
              v-for="entry in categoryCards"
              :key="entry.category"
              class="category-card"
              :class="{ 'no-source': !entry.card || entry.card.pageCount === 0 }"
              @click="navigateToCategory(entry.category)"
            >
              <div class="card-header">
                <span class="card-icon">{{ entry.card?.icon || entry.icon }}</span>
                <span class="card-label">{{ entry.card?.label || entry.label }}</span>
              </div>
              <div v-if="entry.card && entry.card.pageCount > 0" class="card-count">
                {{ entry.card.pageCount.toLocaleString('zh-CN') }} 页
              </div>
              <div v-else class="card-count card-count-empty">本地无来源</div>
              <div class="card-featured" v-if="entry.card?.featuredPages?.length">
                <span
                  v-for="fp in entry.card.featuredPages.slice(0, 3)"
                  :key="fp.id"
                  class="featured-item"
                  :title="fp.title"
                  @click.stop="navigateToPage(fp.id)"
                >
                  {{ fp.title }}
                </span>
              </div>
            </div>
          </div>
        </section>

        <!-- ── 最近编辑 ── -->
        <section class="recent-section">
          <h2 class="section-title">最近编辑</h2>
          <div v-if="homeData?.recentPages?.length" class="recent-list">
            <div
              v-for="page in homeData.recentPages"
              :key="page.id"
              class="recent-item"
              @click="navigateToPage(page.id)"
            >
              <span class="recent-title">{{ page.title }}</span>
              <span class="recent-category">{{ categoryLabel(page.category) }}</span>
              <span class="recent-time">{{ formatTime(page.lastModified) }}</span>
            </div>
          </div>
          <div v-else class="empty-state">
            <p>暂无最近编辑</p>
          </div>
        </section>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.wiki-home {
  height: 100%;
  overflow-y: auto;
  background: var(--lme-bg-base);
}

.wiki-home-container {
  max-width: 1100px;
  margin: 0 auto;
  padding: var(--lme-gap-xl);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

/* ── Hero 区域（对齐灰机首页横幅观感） ── */
.hero-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
  padding: var(--lme-gap-xl) var(--lme-gap-xl) var(--lme-gap-lg);
  background: var(--wiki-hero-bg);
  border: 1px solid var(--wiki-hero-border);
  border-radius: var(--lme-radius-lg);
  position: relative;
  overflow: hidden;
}

.hero-section::before {
  content: '';
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 3px;
  background: linear-gradient(90deg, transparent, var(--wiki-hero-accent-line), transparent);
}

.hero-content {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  text-align: center;
  padding: var(--lme-gap-md) 0 var(--lme-gap-xs);
}

.hero-title {
  margin: 0;
  font-size: 34px;
  font-weight: 700;
  letter-spacing: 2px;
  color: var(--wiki-hero-title);
}

.hero-subtitle {
  margin: 0;
  font-size: var(--lme-font-size-md);
  color: var(--wiki-hero-subtitle);
}

.hero-search {
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
  border-color: var(--lme-accent);
}

.search-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--lme-accent);
  border: 1px solid var(--lme-accent);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-md);
  cursor: pointer;
  transition: background 0.15s;
  white-space: nowrap;
}

.search-btn:hover:not(:disabled) {
  background: var(--lme-accent-hover);
}

.search-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

/* ── 生成入口 ── */
.hero-generate {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-sm);
}

.generate-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: transparent;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition:
    border-color 0.15s,
    color 0.15s;
  white-space: nowrap;
}

.generate-btn:hover:not(:disabled) {
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.generate-btn:disabled {
  opacity: 0.5;
  cursor: progress;
}

.generate-hint {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

.generate-progress {
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.progress-text {
  display: flex;
  justify-content: space-between;
  gap: var(--lme-gap-md);
  margin-bottom: var(--lme-gap-sm);
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
}

.progress-count {
  color: var(--lme-text-muted);
  font-variant-numeric: tabular-nums;
}

.progress-track {
  height: 4px;
  background: var(--lme-bg-input);
  border-radius: 2px;
  overflow: hidden;
}

.progress-bar {
  height: 100%;
  background: var(--lme-accent);
  transition: width 0.2s;
}

.generate-result {
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
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

/* ── 加载状态 ── */
.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-xl);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-md);
}

/* ── 统计概览 ── */
.stats-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--lme-gap-md);
}

.stat-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.stat-value {
  font-size: var(--lme-font-size-xl);
  font-weight: 700;
  color: var(--wiki-stat-value);
  font-variant-numeric: tabular-nums;
}

.stat-label {
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-stat-label);
}

/* ── 分区标题 ── */
.section-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: 700;
  color: var(--wiki-section-title);
  padding-bottom: var(--lme-gap-sm);
  border-bottom: 1px solid var(--wiki-section-head-border);
}

/* ── 分类卡片网格 ── */
.categories-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.category-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--lme-gap-md);
}

.category-card {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  background: var(--wiki-card-bg);
  border: 1px solid var(--wiki-card-border);
  border-radius: var(--lme-radius-md);
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s, transform 0.15s;
}

.category-card:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
  transform: translateY(-1px);
}

/* 无本地来源的分类：保留入口但视觉弱化，不显示假数字 */
.category-card.no-source {
  opacity: 0.55;
}

.card-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.card-icon {
  font-size: 24px;
}

.card-label {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-primary);
}

.card-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  font-variant-numeric: tabular-nums;
}

.card-count-empty {
  color: var(--wiki-cat-empty-text);
}

.card-featured {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin-top: auto;
}

.featured-item {
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-link);
  padding: 2px 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.featured-item:hover {
  color: var(--wiki-accent-strong);
  text-decoration: underline;
}

/* ── 最近编辑 ── */
.recent-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.recent-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.recent-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--wiki-card-bg);
  border: 1px solid var(--wiki-card-border);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}

.recent-item:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
}

.recent-title {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.recent-category {
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-chip-text);
  padding: 1px 8px;
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  border-radius: var(--wiki-chip-radius);
  flex-shrink: 0;
}

.recent-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
  min-width: 140px;
}

/* ── 空状态 ── */
.empty-state {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  text-align: center;
  padding: var(--lme-gap-lg);
}

/* ── 响应式 ── */
@media (max-width: 900px) {
  .category-grid {
    grid-template-columns: repeat(3, 1fr);
  }
  .stats-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (max-width: 640px) {
  .category-grid {
    grid-template-columns: repeat(2, 1fr);
  }
  .hero-search {
    flex-direction: column;
  }
}
</style>
