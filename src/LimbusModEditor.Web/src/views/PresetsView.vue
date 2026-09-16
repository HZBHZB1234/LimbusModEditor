<script setup lang="ts">
// 预设卡片流页面：左栏卡片网格（虚拟滚动）+ 右栏关联资源详情
// 对应 WPF 旧界面 PresetCardFlowPage
// 数据走 ipc 客户端，预设专用方法未实现时显示「暂未实现」

import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import { ipc } from '@/ipc'
import { useUiStateStore } from '@/stores/uiState'

// ── 类型定义 ──────────────────────────────────────────────
interface PresetSubject {
  id: string
  category: string
  label: string
  objectId: string
  chineseName: string
  roleToken: string
  relationCount: number
  portraitUrl: string | null
}

interface PresetRelationLink {
  assetId: string
  previewText: string
  previewKind: string
  mediaKind: string
  duration?: number
  deepLink: string
  isSpine?: boolean
}

interface PresetDetail {
  subject: PresetSubject
  links: PresetRelationLink[]
}

// ── 常量 ──────────────────────────────────────────────────
const CATEGORIES = [
  { key: 'personality', label: '人格' },
  { key: 'enemy', label: '敌人' },
  { key: 'abnormality', label: '异想体' },
  { key: 'announcer', label: '播报员' },
  { key: 'ego_weapon', label: 'E.G.O装备' },
  { key: 'ego_accessory', label: 'E.G.O饰品' },
] as const

const DETAIL_GROUPS = [
  { key: 'static', label: '静态数据' },
  { key: 'text', label: '文本' },
  { key: 'audio', label: '音频' },
  { key: 'image', label: '图像' },
  { key: 'video', label: '视频' },
  { key: 'spine', label: 'Spine' },
  { key: 'animation', label: '动画' },
  { key: 'prefab', label: 'Prefab' },
  { key: 'mesh', label: '网格' },
  { key: 'other', label: '其它' },
] as const

const MAX_ROWS_PER_GROUP = 200
const MAX_CONCURRENT_DECODES = 4
const CARD_WIDTH = 200
const CARD_HEIGHT = 280
const CARD_GAP = 12

// ── 状态 ──────────────────────────────────────────────────
const uiState = useUiStateStore()

const currentCategory = ref<string>('personality')
const searchText = ref('')
const presets = ref<PresetSubject[]>([])
const selectedPresetId = ref<string | null>(null)
const detail = ref<PresetDetail | null>(null)
const loading = ref(false)
const detailLoading = ref(false)
const notImplemented = ref(false)
const indexReady = ref(true)
const reanalyzing = ref(false)

// 搜索防抖
let searchTimer: ReturnType<typeof setTimeout> | null = null

// ── 图片加载（并发闸门 + 代际守卫） ──────────────────────
let activeDecodes = 0
const decodeQueue: Array<() => void> = []
let imageGeneration = 0
const imageUrls = ref<Map<string, string>>(new Map())
const imageErrors = ref<Map<string, boolean>>(new Map())

function acquireSlot(): Promise<void> {
  return new Promise((resolve) => {
    if (activeDecodes < MAX_CONCURRENT_DECODES) {
      activeDecodes++
      resolve()
    } else {
      decodeQueue.push(() => {
        activeDecodes++
        resolve()
      })
    }
  })
}

function releaseSlot() {
  activeDecodes--
  const next = decodeQueue.shift()
  if (next) next()
}

async function loadImage(url: string, objectId: string): Promise<void> {
  const gen = ++imageGeneration

  await acquireSlot()

  // 代际守卫：获取槽位后检查是否已过期
  if (gen !== imageGeneration) {
    releaseSlot()
    return
  }

  try {
    const response = await fetch(url)
    if (!response.ok) throw new Error('加载失败')
    const blob = await response.blob()
    // 再次检查代际（fetch 可能耗时较长）
    if (gen !== imageGeneration) {
      URL.revokeObjectURL(URL.createObjectURL(blob))
      return
    }
    const objectUrl = URL.createObjectURL(blob)
    // 释放旧 URL
    const old = imageUrls.value.get(objectId)
    if (old && old.startsWith('blob:')) URL.revokeObjectURL(old)
    imageUrls.value.set(objectId, objectUrl)
    imageErrors.value.delete(objectId)
  } catch {
    if (gen === imageGeneration) {
      imageErrors.value.set(objectId, true)
    }
  } finally {
    releaseSlot()
  }
}

function getPortraitUrl(preset: PresetSubject): string {
  if (preset.portraitUrl) return preset.portraitUrl
  return `https://lme.data/portraits/${preset.objectId}.png`
}

function triggerImageLoad(preset: PresetSubject) {
  const url = getPortraitUrl(preset)
  const objectId = preset.objectId
  if (imageUrls.value.has(objectId) || imageErrors.value.has(objectId)) return
  loadImage(url, objectId)
}

// ── 虚拟卡片网格 ──────────────────────────────────────────
const containerRef = ref<HTMLElement | null>(null)
const containerWidth = ref(0)
const scrollTop = ref(0)
const containerHeight = ref(600)

let resizeObserver: ResizeObserver | null = null

const cardsPerRow = computed(() =>
  Math.max(1, Math.floor((containerWidth.value + CARD_GAP) / (CARD_WIDTH + CARD_GAP))),
)

const totalRows = computed(() =>
  Math.ceil(filteredPresets.value.length / cardsPerRow.value),
)

const totalHeight = computed(() => totalRows.value * (CARD_HEIGHT + CARD_GAP))

const visibleRows = computed(() => {
  const startRow = Math.max(0, Math.floor(scrollTop.value / (CARD_HEIGHT + CARD_GAP)) - 1)
  const endRow = Math.min(
    totalRows.value,
    Math.ceil((scrollTop.value + containerHeight.value) / (CARD_HEIGHT + CARD_GAP)) + 1,
  )
  const result: Array<{ row: number; items: Array<{ item: PresetSubject; index: number }> }> = []
  for (let row = startRow; row < endRow; row++) {
    const startIdx = row * cardsPerRow.value
    const endIdx = Math.min(startIdx + cardsPerRow.value, filteredPresets.value.length)
    result.push({
      row,
      items: filteredPresets.value.slice(startIdx, endIdx).map((item, i) => ({
        item,
        index: startIdx + i,
      })),
    })
  }
  return result
})

function onScroll(e: Event) {
  const target = e.target as HTMLElement
  scrollTop.value = target.scrollTop
}

// ── 搜索过滤 ──────────────────────────────────────────────
const filteredPresets = computed(() => {
  const q = searchText.value.trim().toLowerCase()
  if (!q) return presets.value
  return presets.value.filter(
    (p) =>
      p.objectId.toLowerCase().includes(q) ||
      p.chineseName.toLowerCase().includes(q) ||
      p.roleToken.toLowerCase().includes(q),
  )
})

function onSearchInput() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => {
    // 搜索仅做前端过滤（数据已在内存中）
  }, 250)
}

// ── 数据加载 ──────────────────────────────────────────────
async function fetchPresets() {
  loading.value = true
  notImplemented.value = false
  try {
    const result = await ipc.request<PresetSubject[]>('preset.list', {
      category: currentCategory.value,
    })
    presets.value = result
    // 重置图片缓存
    imageGeneration++
    imageUrls.value.clear()
    imageErrors.value.clear()
    // 触发可见卡片图片加载
    nextTick(() => {
      for (const p of filteredPresets.value.slice(0, 20)) {
        triggerImageLoad(p)
      }
    })
  } catch {
    notImplemented.value = true
    presets.value = []
  } finally {
    loading.value = false
  }
}

async function fetchDetail(presetId: string) {
  detailLoading.value = true
  notImplemented.value = false
  try {
    const result = await ipc.request<PresetDetail>('preset.detail', { id: presetId })
    detail.value = result
  } catch {
    notImplemented.value = true
    detail.value = null
  } finally {
    detailLoading.value = false
  }
}

async function reanalyze() {
  reanalyzing.value = true
  notImplemented.value = false
  try {
    await ipc.request('preset.reanalyze', { category: currentCategory.value })
    await fetchPresets()
  } catch {
    notImplemented.value = true
  } finally {
    reanalyzing.value = false
  }
}

function selectPreset(preset: PresetSubject) {
  selectedPresetId.value = preset.id
  fetchDetail(preset.id)
}

function revealRelation(link: PresetRelationLink) {
  ipc
    .request('relation.reveal', {
      payload: link.deepLink,
      fallbackKeyword: link.previewText,
    })
    .catch(() => {
      // 静默失败
    })
}

function playSpine(link: PresetRelationLink) {
  ipc
    .request('spine.play', { assetId: link.assetId, deepLink: link.deepLink })
    .catch(() => {
      // 暂未实现
    })
}

function exportSpine(link: PresetRelationLink) {
  ipc
    .request('spine.export', { assetId: link.assetId, deepLink: link.deepLink })
    .catch(() => {
      // 暂未实现
    })
}

// ── 详情分组 ──────────────────────────────────────────────
function getDetailGroup(mediaKind: string, previewKind: string): string {
  const kind = mediaKind || previewKind
  if (['JsonFields', 'Hex', 'Json', 'ScriptableObject', 'MonoBehaviour'].includes(kind))
    return 'static'
  if (kind === 'Text') return 'text'
  if (kind === 'Audio') return 'audio'
  if (['Image', 'SpriteComposite', 'Atlas', 'Texture', 'Sprite'].includes(kind)) return 'image'
  if (kind === 'Video') return 'video'
  if (kind === 'Spine') return 'spine'
  if (kind === 'Animation') return 'animation'
  if (['Prefab', 'GameObject'].includes(kind)) return 'prefab'
  if (kind === 'Mesh') return 'mesh'
  return 'other'
}

const groupedLinks = computed(() => {
  if (!detail.value) return []
  const groups: Array<{ key: string; label: string; links: PresetRelationLink[] }> = []
  for (const g of DETAIL_GROUPS) {
    const links = detail.value.links.filter(
      (l) => getDetailGroup(l.mediaKind, l.previewKind) === g.key,
    )
    if (links.length > 0) {
      groups.push({ key: g.key, label: g.label, links: links.slice(0, MAX_ROWS_PER_GROUP) })
    }
  }
  return groups
})

function previewKindLabel(kind: string): string {
  const labels: Record<string, string> = {
    Image: '图像',
    SpriteComposite: 'Sprite 合成',
    Audio: '音频',
    Text: '文本',
    JsonFields: 'JSON 字段',
    Hex: '十六进制',
    Material: '材质',
    Shader: '着色器',
    Video: '视频',
    Atlas: '图集',
    Summary: '摘要',
    None: '无法预览',
  }
  return labels[kind] ?? kind
}

function formatDuration(ms?: number): string {
  if (ms == null) return '—'
  if (ms < 1000) return `${ms}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

// ── 生命周期 ──────────────────────────────────────────────
onMounted(() => {
  fetchPresets()
  if (containerRef.value) {
    resizeObserver = new ResizeObserver((entries) => {
      for (const entry of entries) {
        containerWidth.value = entry.contentRect.width
      }
    })
    resizeObserver.observe(containerRef.value)
  }
})

onUnmounted(() => {
  resizeObserver?.disconnect()
  // 释放所有 blob URL
  for (const url of imageUrls.value.values()) {
    if (url.startsWith('blob:')) URL.revokeObjectURL(url)
  }
})

watch(currentCategory, () => {
  selectedPresetId.value = null
  detail.value = null
  fetchPresets()
})
</script>

<template>
  <div class="presets-view">
    <!-- 左栏：卡片流 -->
    <div class="browser-column">
      <!-- 分类切换 -->
      <div class="category-bar">
        <button
          v-for="cat in CATEGORIES"
          :key="cat.key"
          class="category-tab"
          :class="{ active: currentCategory === cat.key }"
          @click="currentCategory = cat.key"
        >
          {{ cat.label }}
        </button>
      </div>

      <!-- 搜索 + 操作栏 -->
      <div class="toolbar-row">
        <input
          v-model="searchText"
          class="search-input"
          type="text"
          placeholder="搜索对象 ID / 中文名 / 角色标记"
          @input="onSearchInput"
        />
        <button class="reanalyze-btn" :disabled="reanalyzing" @click="reanalyze">
          {{ reanalyzing ? '分析中…' : '重新分析' }}
        </button>
      </div>

      <!-- 结果计数 -->
      <div class="result-info" v-if="!loading && !notImplemented">
        <span class="result-count">共 {{ filteredPresets.length }} 个预设</span>
      </div>

      <!-- 卡片网格（虚拟滚动） -->
      <div
        ref="containerRef"
        class="card-grid-container"
        :style="{ height: containerHeight + 'px' }"
        @scroll="onScroll"
      >
        <div class="card-grid-spacer" :style="{ height: totalHeight + 'px' }">
          <div
            v-for="row in visibleRows"
            :key="row.row"
            class="card-grid-row"
            :style="{ transform: `translateY(${row.row * (CARD_HEIGHT + CARD_GAP)}px)` }"
          >
            <div
              v-for="entry in row.items"
              :key="entry.item.id"
              class="preset-card"
              :class="{ selected: entry.item.id === selectedPresetId }"
              @click="selectPreset(entry.item)"
            >
              <!-- 头像封面 -->
              <div class="card-portrait">
                <img
                  v-if="imageUrls.get(entry.item.objectId)"
                  :src="imageUrls.get(entry.item.objectId)"
                  class="portrait-img"
                />
                <div
                  v-else-if="imageErrors.get(entry.item.objectId)"
                  class="portrait-placeholder"
                >
                  无图
                </div>
                <div v-else class="portrait-loading">加载中…</div>
              </div>
              <!-- 卡片信息 -->
              <div class="card-info">
                <div class="card-name" :title="entry.item.chineseName">
                  {{ entry.item.chineseName || entry.item.label }}
                </div>
                <div class="card-meta">
                  <span class="card-category">{{ entry.item.category }}</span>
                  <span class="card-relation-count">{{ entry.item.relationCount }} 关联</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 空状态：索引未就绪 -->
      <div v-if="!indexReady && !loading" class="empty-state">
        <div class="empty-icon">📭</div>
        <div class="empty-text">关联索引尚未就绪，请先执行分析</div>
      </div>

      <!-- 空状态：暂未实现 -->
      <div v-if="notImplemented && !loading" class="empty-state">
        <div class="empty-icon">🚧</div>
        <div class="empty-text">暂未实现</div>
        <div class="empty-hint">预设数据加载方法尚未在后端实现</div>
      </div>

      <!-- 加载态 -->
      <div v-if="loading" class="loading-overlay">
        <span class="loading-spinner">⏳</span>
        <span>加载中…</span>
      </div>
    </div>

    <!-- 分隔条 -->
    <div class="column-splitter" />

    <!-- 右栏：详情面板 -->
    <div class="detail-column" :style="{ width: uiState.previewColumnWidth + 'px' }">
      <!-- 详情头部 -->
      <div class="detail-header" v-if="detail">
        <div class="detail-title">{{ detail.subject.chineseName || detail.subject.label }}</div>
        <div class="detail-subtitle lme-mono">{{ detail.subject.objectId }}</div>
      </div>

      <!-- 详情内容 -->
      <div class="detail-body">
        <!-- 未选中 -->
        <div v-if="!detail && !detailLoading" class="detail-empty">
          <div class="empty-icon">🃏</div>
          <div class="empty-text">从左侧选择一个预设查看详情</div>
        </div>

        <!-- 加载中 -->
        <div v-if="detailLoading" class="detail-loading">
          <span class="loading-spinner">⏳</span>
          <span>加载详情…</span>
        </div>

        <!-- 暂未实现 -->
        <div v-if="notImplemented && !detailLoading" class="detail-empty">
          <div class="empty-icon">🚧</div>
          <div class="empty-text">暂未实现</div>
        </div>

        <!-- 分组列表 -->
        <template v-if="detail && !notImplemented">
          <div v-for="group in groupedLinks" :key="group.key" class="detail-group">
            <div class="group-header">
              <span class="group-label">{{ group.label }}</span>
              <span class="group-count">{{ group.links.length }}</span>
            </div>
            <div class="group-rows">
              <div v-for="link in group.links" :key="link.assetId" class="relation-row">
                <!-- 预览文本 -->
                <div class="row-preview" :title="link.previewText">
                  {{ link.previewText }}
                </div>
                <!-- 元信息 -->
                <div class="row-meta">
                  <span class="meta-tag kind-tag">{{ previewKindLabel(link.previewKind) }}</span>
                  <span class="meta-tag media-tag">{{ link.mediaKind }}</span>
                  <span class="meta-tag duration-tag">{{ formatDuration(link.duration) }}</span>
                </div>
                <!-- 深链接 -->
                <div class="row-deeplink lme-mono" :title="link.deepLink">
                  {{ link.deepLink }}
                </div>
                <!-- 操作按钮 -->
                <div class="row-actions">
                  <button class="action-btn open-btn" @click="revealRelation(link)">打开</button>
                  <template v-if="group.key === 'spine'">
                    <button class="action-btn play-btn" @click="playSpine(link)">▶ 播放</button>
                    <button class="action-btn export-btn" @click="exportSpine(link)">导出</button>
                  </template>
                </div>
              </div>
            </div>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* ── 布局 ── */
.presets-view {
  display: flex;
  height: 100%;
  overflow: hidden;
  position: relative;
}

.browser-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
}

.column-splitter {
  width: 6px;
  flex-shrink: 0;
  background: var(--lme-border);
  cursor: col-resize;
}

.detail-column {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--lme-bg-panel);
  min-width: 260px;
}

/* ── 分类栏 ── */
.category-bar {
  display: flex;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  flex-wrap: wrap;
}

.category-tab {
  padding: 4px 12px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.category-tab:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.category-tab.active {
  background: var(--lme-accent-muted);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

/* ── 工具栏 ── */
.toolbar-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.search-input {
  flex: 1;
  min-width: 200px;
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

.reanalyze-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  white-space: nowrap;
}

.reanalyze-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.reanalyze-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ── 结果信息 ── */
.result-info {
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

/* ── 卡片网格 ── */
.card-grid-container {
  overflow-y: auto;
  overflow-x: hidden;
  position: relative;
  flex: 1;
}

.card-grid-spacer {
  position: relative;
  width: 100%;
}

.card-grid-row {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  display: flex;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
}

.preset-card {
  width: 200px;
  flex-shrink: 0;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  overflow: hidden;
  cursor: pointer;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.preset-card:hover {
  border-color: var(--lme-accent);
  box-shadow: var(--lme-shadow-sm);
}

.preset-card.selected {
  border-color: var(--lme-accent);
  box-shadow: 0 0 0 1px var(--lme-accent);
}

/* 头像封面 */
.card-portrait {
  width: 100%;
  height: 200px;
  background: var(--lme-bg-input);
  display: flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
}

.portrait-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.portrait-placeholder {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-md);
}

.portrait-loading {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

/* 卡片信息 */
.card-info {
  padding: var(--lme-gap-sm);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.card-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-meta {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
}

.card-category {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: 1px 6px;
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
}

.card-relation-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-left: auto;
}

/* ── 详情面板 ── */
.detail-header {
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.detail-title {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.detail-subtitle {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-top: var(--lme-gap-xs);
}

.detail-body {
  flex: 1;
  overflow: auto;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.detail-empty,
.detail-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  flex: 1;
  color: var(--lme-text-muted);
}

/* ── 分组 ── */
.detail-group {
  border-bottom: 1px solid var(--lme-border);
}

.group-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  position: sticky;
  top: 0;
  z-index: 1;
}

.group-label {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.group-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: 1px 6px;
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
}

.group-rows {
  display: flex;
  flex-direction: column;
}

/* ── 关联行 ── */
.relation-row {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.relation-row:last-child {
  border-bottom: none;
}

.row-preview {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-meta {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.meta-tag {
  padding: 1px 6px;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
}

.kind-tag {
  color: var(--lme-accent);
}

.media-tag {
  color: var(--lme-text-secondary);
}

.duration-tag {
  color: var(--lme-text-muted);
}

.row-deeplink {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-actions {
  display: flex;
  gap: var(--lme-gap-xs);
  margin-top: var(--lme-gap-xs);
}

.action-btn {
  padding: 2px 10px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
  transition: all 0.15s;
}

.action-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
  border-color: var(--lme-accent);
}

.open-btn {
  color: var(--lme-accent);
}

.play-btn {
  color: var(--lme-success);
}

.export-btn {
  color: var(--lme-info);
}

/* ── 空状态 ── */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  flex: 1;
  color: var(--lme-text-muted);
  padding: var(--lme-gap-xl);
}

.empty-icon {
  font-size: 48px;
}

.empty-text {
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-secondary);
}

.empty-hint {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

/* ── 加载态 ── */
.loading-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  background: var(--lme-overlay-bg);
  color: var(--lme-text-secondary);
  z-index: 10;
}

.loading-spinner {
  font-size: var(--lme-font-size-lg);
}
</style>
