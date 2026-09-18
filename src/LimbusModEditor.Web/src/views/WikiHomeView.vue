<script setup lang="ts">
// 维基首页（WikiHomeView）
// Hero 区域 + 分类卡片网格 + 最近编辑 + 统计概览
// 数据来源：ipc.request('wiki.home', {})
// 布局：WikiShell 包裹；呈现层使用 Naive UI（ui-redesign r6：图标走 AppIcon，配色统一到全局令牌）

import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { NAlert, NButton, NCard, NInput, NProgress, NTag, NTooltip } from 'naive-ui'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import PageHeader from '@/components/PageHeader.vue'
import StateBlock from '@/components/StateBlock.vue'
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'
import { useStatusStore } from '@/stores/status'
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
const status = useStatusStore()

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
    status.notify('error', `维基首页数据加载失败：${errorMessage.value}`)
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
  // 全局状态登记：与 progress 事件的 operationId 同 id，
  // 事件到达时 store 会把进度补进同一条活动（原有订阅逻辑不变）。
  status.beginActivity(generateOperationId, '正在生成维基页面')
  try {
    const result = await ipc.request<WikiGenerateResponse>(
      'wiki.generate',
      { operationId: generateOperationId },
      // 真实数据下生成要扫 127 万条资源行（实测约 70 秒），默认 30 秒超时不够；
      // 期间进度由 progress 事件回传，所以给一个宽松上限即可。
      900000,
    )
    generateMessage.value = result.message
    status.notify('success', result.message || '维基页面生成完成')
    await loadHomeData()
    await loadGenerateStatus()
  } catch (e: unknown) {
    generateError.value = e instanceof Error ? e.message : String(e)
    status.notify('error', `生成维基页面失败：${generateError.value}`)
  } finally {
    isGenerating.value = false
    generateProgress.value = null
    status.endActivity(generateOperationId)
  }
}

// 11 个维基分类的固定顺序（与侧栏一致）；后端只回传有内容的分类，
// 缺席的分类照常给入口卡片，但计数显示「本地无来源」而不是假数字。
const ALL_CATEGORIES = Object.keys(WikiPageCategoryLabels) as WikiPageCategory[]

const CATEGORY_ICONS: Record<WikiPageCategory, IconName> = {
  persona: 'wikiPersona',
  enemy: 'wikiEnemy',
  abnormality: 'wikiAbnormality',
  ego: 'wikiEgo',
  ego_gift: 'wikiEgoGift',
  announcer: 'wikiAnnouncer',
  story: 'wikiStory',
  stage: 'wikiStage',
  item: 'wikiItem',
  mechanism: 'wikiMechanism',
  keyword: 'wikiKeyword',
}

function categoryLabel(category: WikiPageCategory): string {
  return WikiPageCategoryLabels[category] ?? category
}

function categoryIcon(category: WikiPageCategory): IconName {
  return CATEGORY_ICONS[category] ?? 'file'
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

/** 生成进度百分比；未知总量时返回 0 并交给 processing 态做循环动画 */
const progressPercentage = computed(() => {
  const progress = generateProgress.value
  if (!progress?.total) return 0
  return Math.round((progress.current / progress.total) * 100)
})

// ── 初始化 ──
onMounted(() => {
  unsubscribeProgress = ipc.on('progress', (payload) => {
    const progress = payload as ProgressPayload
    if (progress.operationId !== generateOperationId) return
    generateProgress.value = progress
    // 额外登记到全局状态（本地进度逻辑保持不变）
    status.updateActivity(generateOperationId, {
      detail: progress.message || '',
      current: progress.total ? progress.current : null,
      total: progress.total ? progress.total : null,
    })
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
      <!-- 顶部细进度条（统一工具类 .lme-loadingbar） -->
      <div v-if="isLoading" class="lme-loadingbar" aria-hidden="true" />

      <PageHeader
        class="wiki-home-header"
        icon="wiki"
        title="维基首页"
        description="由本地游戏数据生成的维基：按分类浏览人格、E.G.O、敌方单位与剧情。"
        hint="首次使用先点「生成页面」——要扫本地全部资源，耗时较长；进度会显示在底部状态栏。"
        hint-key="wiki-home"
      />

      <div class="wiki-home-container">
        <!-- ── Hero 区域（对齐灰机首页：暗底横幅 + 主文字色大标题） ── -->
        <section class="hero-section wiki-section">
          <div class="hero-content">
            <h1 class="hero-title page-title">Limbus Company 中文维基</h1>
            <p class="hero-subtitle">
              边狱公司模组编辑知识库 · 人格、E.G.O、敌方单位与剧情百科 · 数据来自本地游戏文件
            </p>
          </div>
          <div class="hero-search">
            <NInput
              v-model:value="searchKeyword"
              class="search-input"
              size="small"
              placeholder="搜索维基页面、人格、异想体、关键词…"
              clearable
              @keyup.enter="performSearch"
            />
            <NTooltip placement="bottom" :show-arrow="false">
              <template #trigger>
                <NButton
                  size="small"
                  type="primary"
                  :disabled="!searchKeyword.trim()"
                  @click="performSearch"
                >
                  搜索
                </NButton>
              </template>
              搜索维基页面
            </NTooltip>
          </div>
          <!-- 生成入口：维基页是派生内容，按需生成（说明见脚本区注释） -->
          <div class="hero-generate">
            <NTooltip placement="bottom" :show-arrow="false">
              <template #trigger>
                <NButton
                  size="small"
                  type="primary"
                  ghost
                  :disabled="isGenerating"
                  @click="generatePages"
                >
                  {{ isGenerating ? '正在生成…' : '生成页面' }}
                </NButton>
              </template>
              {{ isGenerating ? '正在扫描本地资源生成维基页面' : '生成或刷新维基页面（首次耗时较长）' }}
            </NTooltip>
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
          <NProgress
            type="line"
            :percentage="progressPercentage"
            :processing="!generateProgress?.total"
            :show-indicator="false"
          />
        </div>

        <!-- ── 生成结果 / 失败 ── -->
        <NAlert v-if="generateMessage" type="success" :bordered="false" class="generate-result">
          {{ generateMessage }}
        </NAlert>
        <NAlert v-if="generateError" type="error" class="page-error">{{ generateError }}</NAlert>

        <!-- ── 错误提示 ── -->
        <NAlert v-if="errorMessage" type="error" class="page-error" :title="errorMessage">
          <NButton size="small" @click="loadHomeData">重试</NButton>
        </NAlert>

        <!-- ── 加载中 ── -->
        <StateBlock
          v-if="isLoading"
          class="loading-state"
          state="loading"
          title="正在加载维基首页…"
        />

        <!-- ── 统计概览 ── -->
        <section v-if="homeData" class="stats-section wiki-section">
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
        <section class="categories-section wiki-section">
          <h2 class="section-title">分类浏览</h2>
          <div class="category-grid">
            <NCard
              v-for="entry in categoryCards"
              :key="entry.category"
              size="small"
              hoverable
              class="category-card"
              :class="{ 'no-source': !entry.card || entry.card.pageCount === 0 }"
              @click="navigateToCategory(entry.category)"
            >
              <div class="card-header">
                <span class="card-icon"><AppIcon :name="entry.icon" :size="22" /></span>
                <span class="card-label">{{ entry.card?.label || entry.label }}</span>
              </div>
              <div v-if="entry.card && entry.card.pageCount > 0" class="card-count">
                {{ entry.card.pageCount.toLocaleString('zh-CN') }} 页
              </div>
              <NTag v-else size="small" :bordered="false" class="card-count-empty">
                本地无来源
              </NTag>
              <div class="card-featured" v-if="entry.card?.featuredPages?.length">
                <span
                  v-for="fp in entry.card.featuredPages.slice(0, 3)"
                  :key="fp.id"
                  class="featured-item entry-item"
                  :title="fp.title"
                  @click.stop="navigateToPage(fp.id)"
                >
                  {{ fp.title }}
                </span>
              </div>
            </NCard>
          </div>
        </section>

        <!-- ── 最近编辑 ── -->
        <section class="recent-section wiki-section">
          <h2 class="section-title">最近编辑</h2>
          <div v-if="homeData?.recentPages?.length" class="recent-list">
            <div
              v-for="page in homeData.recentPages"
              :key="page.id"
              class="recent-item"
              @click="navigateToPage(page.id)"
            >
              <span class="recent-title">{{ page.title }}</span>
              <NTag size="small" :bordered="false" class="recent-category wiki-chip">
                {{ categoryLabel(page.category) }}
              </NTag>
              <span class="recent-time">{{ formatTime(page.lastModified) }}</span>
            </div>
          </div>
          <StateBlock
            v-else
            class="empty-state"
            state="empty"
            icon="clock"
            title="暂无最近编辑"
            description="点上方「生成页面」后，这里会列出最近变更的维基页面"
          />
        </section>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.wiki-home {
  position: relative;
  height: 100%;
  overflow-y: auto;
  background: var(--lme-bg-base);
}

/* ── 顶部细进度条：统一用 tokens.css 的 .lme-loadingbar 工具类，此处不再重复定义 ── */

/* ── 内容容器：宽屏居中，让页面「呼吸」 ── */
.wiki-home-container {
  max-width: var(--wiki-content-max-width);
  margin: 0 auto;
  padding: var(--lme-gap-xl) var(--lme-gap-lg) var(--lme-gap-2xl);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

/* ── Hero 区域（对齐灰机首页横幅观感） ── */
.hero-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
  padding: var(--lme-gap-2xl) var(--lme-gap-xl) var(--lme-gap-xl);
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
  height: var(--wiki-hero-accent-line-height);
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
  font-size: var(--wiki-title-size);
  line-height: var(--wiki-title-line);
  font-weight: var(--lme-font-weight-bold);
  letter-spacing: var(--lme-tracking-caps);
  color: var(--wiki-hero-title);
}

.hero-subtitle {
  margin: 0;
  font-size: var(--lme-font-size-md);
  line-height: var(--lme-line-height-relaxed);
  color: var(--wiki-hero-subtitle);
}

/* ── 搜索 ── */
.hero-search {
  display: flex;
  gap: var(--lme-gap-sm);
}

.search-input {
  flex: 1;
}

/* ── 生成入口 ── */
.hero-generate {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-xs);
}

.generate-hint {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

/* ── 生成进度 ── */
.generate-progress {
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--wiki-card-border);
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

.generate-result {
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
}

/* ── 加载 / 空状态：三态由 StateBlock 统一呈现，这里只留页内间距 ── */
.loading-state {
  padding: var(--lme-gap-xl) var(--lme-gap-md);
}

/* ── 统计概览 ── */
.stats-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(var(--wiki-stat-col-min), 1fr));
  gap: var(--lme-gap-md);
}

.stat-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-lg);
  background: var(--wiki-card-bg);
  border: 1px solid var(--wiki-card-border);
  border-radius: var(--lme-radius-md);
  transition:
    border-color var(--lme-dur-fast) var(--lme-ease-standard),
    transform var(--lme-dur-fast) var(--lme-ease-standard),
    box-shadow var(--lme-dur-fast) var(--lme-ease-standard);
}

.stat-card:hover {
  border-color: var(--wiki-card-hover-border);
  transform: translateY(var(--wiki-card-lift));
  box-shadow: var(--lme-shadow-md);
}

.stat-value {
  font-size: var(--lme-font-size-2xl);
  font-weight: var(--lme-font-weight-bold);
  color: var(--wiki-stat-value);
  font-variant-numeric: tabular-nums;
  line-height: var(--lme-line-height-tight);
}

.stat-label {
  font-size: var(--lme-font-size-xs);
  color: var(--wiki-stat-label);
}

/* ── 分区标题 ── */
.section-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
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
  grid-template-columns: repeat(auto-fill, minmax(var(--wiki-category-col-min), 1fr));
  gap: var(--lme-gap-md);
}

.category-card {
  cursor: pointer;
  transition:
    border-color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard),
    transform var(--lme-dur-fast) var(--lme-ease-standard),
    box-shadow var(--lme-dur-fast) var(--lme-ease-standard);
}

.category-card.n-card:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
  transform: translateY(var(--wiki-card-lift));
  box-shadow: var(--lme-shadow-md);
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
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-inset);
  color: var(--lme-text-secondary);
}

.card-label {
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-semibold);
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
  gap: var(--lme-gap-xs);
  margin-top: auto;
}

.featured-item {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-accent);
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition: color var(--lme-dur-fast) var(--lme-ease-standard);
}

.featured-item:hover {
  color: var(--lme-accent-hover);
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
  transition:
    border-color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.recent-item:hover {
  border-color: var(--wiki-card-hover-border);
  background: var(--wiki-card-hover-bg);
}

.recent-title {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 分类胶囊：NTag 套 wiki-chip 皮肤（跟随全局强调色） */
.recent-category.wiki-chip {
  background: var(--wiki-chip-bg);
  color: var(--wiki-chip-text);
  border-radius: var(--wiki-chip-radius);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.recent-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
  min-width: var(--wiki-recent-time-min);
  font-variant-numeric: tabular-nums;
}

/* ── 空状态 ── */
.empty-state {
  color: var(--lme-text-muted);
}

/* ── 可达性：键盘焦点 ── */
.stat-card:focus-visible,
.category-card.n-card:focus-visible,
.recent-item:focus-visible,
.featured-item:focus-visible {
  outline: none;
  box-shadow: var(--wiki-focus-ring);
}

/* ── 响应式 ── */
@media (max-width: 900px) {
  .stats-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (max-width: 640px) {
  .hero-search {
    flex-direction: column;
  }
  .hero-generate {
    flex-direction: column;
    align-items: stretch;
  }
  .recent-item {
    flex-wrap: wrap;
  }
  .recent-time {
    min-width: 0;
  }
}
</style>
