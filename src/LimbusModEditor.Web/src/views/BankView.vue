<script setup lang="ts">
// 音频工作台（Bank 浏览 / 预览 / 导出）
// 对应 WPF 旧界面的 AudioWorkbenchPage
// 布局：三列（浏览 | 分隔条 | 预览），与 AssetsView 一致

import { ref, reactive, computed, onMounted, watch } from 'vue'
import { ipc, IpcClientError } from '@/ipc'
import VirtualList from '@/components/VirtualList.vue'
import PageBar from '@/components/PageBar.vue'
import { useUiStateStore } from '@/stores/uiState'

// ── 类型定义 ──────────────────────────────────────────────────

/** 银行类型 */
type BankType = 'All' | 'EventBank' | 'AudioBank'

/** 排序方式 */
type BankSortKind = 'Name' | 'SampleCount' | 'Size' | 'Type'

/** 银行记录（字段对齐 bank.list 的 BankListItem：bankId/name/sampleCount/sizeBytes） */
interface BankInfo {
  /** 银行唯一 ID（bank.list 给的是容器路径） */
  bankId: string
  /** 显示名称 */
  name: string
  /** 资源路径 */
  path: string
  /** 银行类型（bank.list 不提供，缺失时显示"未知"） */
  bankType?: 'EventBank' | 'AudioBank'
  /** 采样数 */
  sampleCount: number
  /** FSB 信息（bank.list 不提供，缺失时不显示） */
  fsbInfo?: string
  /** 总字节数 */
  totalSize: number
}

/** 采样信息 */
interface SampleInfo {
  /** 采样唯一 ID */
  sampleId: string
  /** 采样名称 */
  name: string
  /** 编码格式 */
  codec: string
  /** 时长（秒） */
  duration: number
  /** 字节数（bank.samples 不提供，缺失时显示"—"） */
  size?: number
  /** 声道数 */
  channels: number
  /** 采样率（Hz） */
  sampleRate: number
  /** 所属银行 ID */
  bankId: string
}

// ── UI 状态 store ─────────────────────────────────────────────

const uiState = useUiStateStore()

// ── 搜索判据 ──────────────────────────────────────────────────

/** 银行搜索判据 */
interface BankSearchQuery {
  text: string
  bankType: BankType
  sort: BankSortKind
}

const query = reactive<BankSearchQuery>({
  text: '',
  bankType: 'All',
  sort: 'Name',
})

// ── 数据状态 ──────────────────────────────────────────────────

const bankList = ref<BankInfo[]>([])
const bankTotalCount = ref(0)
const bankOffset = ref(0)
const bankPageSize = ref(200)
const bankLoading = ref(false)
const bankError = ref<string | null>(null)
const bankGeneration = ref(0)
const lastBankQueryMs = ref(0)

const selectedBankId = ref<string | null>(null)

/** 选中银行的采样列表 */
const sampleList = ref<SampleInfo[]>([])
const sampleLoading = ref(false)
const sampleError = ref<string | null>(null)
const sampleGeneration = ref(0)

/** 当前选中采样的音频预览 URL（bank.preview 给的 WAV 地址，可直接播） */
const audioPreviewUrl = ref<string | null>(null)
/** 后端明确回 unsupported 才置真：此时才显示「未实现」提示 */
const audioPreviewUnsupported = ref(false)

// ── 计算属性 ──────────────────────────────────────────────────

const pageCount = computed(() => Math.max(1, Math.ceil(bankTotalCount.value / bankPageSize.value)))
const currentPage = computed(() => Math.floor(bankOffset.value / bankPageSize.value) + 1)

const selectedBank = computed<BankInfo | null>(() =>
  bankList.value.find((b) => b.bankId === selectedBankId.value) ?? null,
)

const hasBankData = computed(() => bankList.value.length > 0)

const selectedSampleId = ref<string | null>(null)

const selectedSample = computed<SampleInfo | null>(() =>
  sampleList.value.find((s) => s.sampleId === selectedSampleId.value) ?? null,
)

// ── 工具函数 ──────────────────────────────────────────────────

/** 格式化文件大小 */
function formatSize(bytes?: number): string {
  if (bytes == null) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** 格式化时长 */
function formatDuration(seconds: number): string {
  const m = Math.floor(seconds / 60)
  const s = Math.floor(seconds % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

/** 银行类型颜色 */
function bankTypeColor(type?: string): string {
  return type === 'EventBank' ? 'var(--lme-type-audio)' : 'var(--lme-type-shader)'
}

/** 银行类型中文标签（bank.list 不提供类型，缺失时显示"未知"，不猜） */
function bankTypeLabel(type?: string): string {
  if (type === 'EventBank') return '事件银行'
  if (type === 'AudioBank') return '音频银行'
  return '未知'
}

// ── 选项数据 ──────────────────────────────────────────────────

const bankTypeOptions: { value: BankType; label: string }[] = [
  { value: 'All', label: '全部类型' },
  { value: 'EventBank', label: '事件银行' },
  { value: 'AudioBank', label: '音频银行' },
]

const sortOptions: { value: BankSortKind; label: string }[] = [
  { value: 'Name', label: '按名称' },
  { value: 'SampleCount', label: '按采样数' },
  { value: 'Size', label: '按大小' },
  { value: 'Type', label: '按类型' },
]

// ── 搜索防抖 ──────────────────────────────────────────────────

let debounceTimer: ReturnType<typeof setTimeout> | null = null

function onSearchInput() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => {
    performSearch()
  }, 250)
}

function onFilterChange() {
  performSearch()
}

function clearFilters() {
  query.text = ''
  query.bankType = 'All'
  query.sort = 'Name'
  performSearch()
}

// ── IPC 请求 ──────────────────────────────────────────────────

/** 执行银行搜索 */
async function performSearch() {
  bankOffset.value = 0
  await fetchBankPage(0)
}

/** 获取银行分页数据 */
async function fetchBankPage(offset: number) {
  const gen = ++bankGeneration.value
  bankLoading.value = true
  bankError.value = null
  const t0 = performance.now()

  try {
    // 契约方法 bank.list：载荷 { offset, take }（后端无过滤参数），响应 BankListResponse { items, totalCount }
    const result = await ipc.request<{
      items: { bankId: string; name: string; sampleCount: number; sizeBytes: number }[]
      totalCount: number
    }>('bank.list', {
      offset,
      take: bankPageSize.value,
    })

    if (gen !== bankGeneration.value) return

    // bankType / fsbInfo 后端不提供，留空（不编造）
    bankList.value = result.items.map((i) => ({
      bankId: i.bankId,
      name: i.name,
      path: i.bankId,
      sampleCount: i.sampleCount,
      totalSize: i.sizeBytes,
    }))
    bankTotalCount.value = result.totalCount
    bankOffset.value = offset
    lastBankQueryMs.value = performance.now() - t0

    // 自动选中第一条
    if (bankList.value.length > 0 && !selectedBankId.value) {
      selectBank(bankList.value[0].bankId)
    }
  } catch (e: unknown) {
    if (gen !== bankGeneration.value) return
    bankError.value = e instanceof Error ? e.message : String(e)
    bankList.value = []
    bankTotalCount.value = 0
  } finally {
    if (gen === bankGeneration.value) {
      bankLoading.value = false
    }
  }
}

/** 翻页 */
async function onGoToPage(pageIndex: number) {
  const clamped = Math.max(0, Math.min(pageIndex, pageCount.value - 1))
  bankOffset.value = clamped * bankPageSize.value
  await fetchBankPage(bankOffset.value)
}

/** 选中银行并加载采样列表 */
async function selectBank(bankId: string) {
  selectedBankId.value = bankId
  selectedSampleId.value = null
  audioPreviewUrl.value = null
  audioPreviewUnsupported.value = false
  await loadSamples(bankId)
}

/** 加载采样列表 */
async function loadSamples(bankId: string) {
  const gen = ++sampleGeneration.value
  sampleLoading.value = true
  sampleError.value = null

  try {
    // 契约方法 bank.samples：载荷 { bankId }，响应 BankSamplesResponse { bankId, samples }
    const result = await ipc.request<{
      bankId: string
      samples: {
        name: string
        durationSec: number
        codecName: string
        channels: number
        sampleRate: number
      }[]
    }>('bank.samples', { bankId })

    if (gen !== sampleGeneration.value) return

    // SampleListItem 无字节数字段，size 留空（不编造）；sampleId 用采样名（后端无独立 id）
    sampleList.value = result.samples.map((s) => ({
      sampleId: s.name,
      name: s.name,
      codec: s.codecName,
      duration: s.durationSec,
      channels: s.channels,
      sampleRate: s.sampleRate,
      bankId: result.bankId,
    }))

    // 自动选中第一条采样
    if (sampleList.value.length > 0) {
      selectSample(sampleList.value[0])
    }
  } catch (e: unknown) {
    if (gen !== sampleGeneration.value) return
    sampleError.value = e instanceof Error ? e.message : String(e)
    sampleList.value = []
  } finally {
    if (gen === sampleGeneration.value) {
      sampleLoading.value = false
    }
  }
}

/** 选中采样并加载音频预览 */
async function selectSample(sample: SampleInfo) {
  selectedSampleId.value = sample.sampleId
  audioPreviewUrl.value = null
  audioPreviewUnsupported.value = false

  try {
    // 契约方法 bank.preview：载荷 { bankId, sampleName } → { audioUrl }（可直接播放的 WAV 地址）
    const result = await ipc.request<{ audioUrl: string }>('bank.preview', {
      bankId: sample.bankId,
      sampleName: sample.name,
    })
    audioPreviewUrl.value = result?.audioUrl ?? null
    audioPreviewUnsupported.value = !audioPreviewUrl.value
  } catch (e: unknown) {
    // 只有后端明确回 unsupported 时才显示「未实现」提示；其它失败不阻塞 UI，也不谎报未实现
    audioPreviewUrl.value = null
    audioPreviewUnsupported.value = e instanceof IpcClientError && e.code === 'unsupported'
  }
}

// ── 导出 ──────────────────────────────────────────────────────

/** 导出为 .rebank 格式 */
async function exportRebank() {
  if (!selectedBank.value) return

  try {
    // BankExportRebankRequest 需要 targetDirectory：走宿主原生目录对话框（不用 Web 文件输入）
    const picked = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: '选择 .rebank 导出目录',
    })
    if (!picked.path) return

    await ipc.request('bank.exportRebank', {
      bankId: selectedBank.value.bankId,
      targetDirectory: picked.path,
    })
  } catch {
    // 导出 IPC 未实现
    showNotImplemented()
  }
}

/** 显示"暂未实现"提示 */
function showNotImplemented() {
  // 轻量提示：复用 error 通道显示临时消息
  const prevError = bankError.value
  bankError.value = '暂未实现：该功能对应的 IPC 方法尚未在后端实现'
  setTimeout(() => {
    if (bankError.value === '暂未实现：该功能对应的 IPC 方法尚未在后端实现') {
      bankError.value = prevError
    }
  }, 3000)
}

// ── 分隔条拖拽 ────────────────────────────────────────────────

const splitterDragging = ref(false)

function onSplitterMouseDown() {
  splitterDragging.value = true
  document.addEventListener('mousemove', onSplitterMouseMove)
  document.addEventListener('mouseup', onSplitterMouseUp)
}

function onSplitterMouseMove(e: MouseEvent) {
  if (!splitterDragging.value) return
  const viewportWidth = window.innerWidth
  const previewWidth = viewportWidth - e.clientX - 3
  uiState.setPreviewColumnWidth(previewWidth)
}

function onSplitterMouseUp() {
  splitterDragging.value = false
  document.removeEventListener('mousemove', onSplitterMouseMove)
  document.removeEventListener('mouseup', onSplitterMouseUp)
}

// ── 生命周期 ──────────────────────────────────────────────────

onMounted(() => {
  performSearch()
})

// ── 虚拟列表高度 ──────────────────────────────────────────────

const bankListHeight = ref(600)
const sampleListHeight = ref(400)
</script>

<template>
  <div class="bank-view">
    <!-- ═══════════════════════════════════════════════════════════ -->
    <!-- 中栏：搜索 + 筛选 + 浏览                                 -->
    <!-- ═══════════════════════════════════════════════════════════ -->
    <div class="browser-column">
      <!-- 搜索筛选栏 -->
      <div class="search-filters">
        <!-- 搜索框 -->
        <div class="filter-row">
          <input
            v-model="query.text"
            class="search-input"
            type="text"
            placeholder="搜索银行路径或采样名称（支持中文）"
            @input="onSearchInput"
          />
        </div>

        <!-- 筛选下拉 -->
        <div class="filter-row">
          <label class="filter-label">类型</label>
          <select v-model="query.bankType" class="filter-select" @change="onFilterChange">
            <option v-for="opt in bankTypeOptions" :key="opt.value" :value="opt.value">
              {{ opt.label }}
            </option>
          </select>

          <label class="filter-label">排序</label>
          <select v-model="query.sort" class="filter-select" @change="onFilterChange">
            <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
              {{ opt.label }}
            </option>
          </select>

          <button class="filter-clear-btn" @click="clearFilters">清除筛选</button>

          <span class="result-count" v-if="!bankLoading">
            共 {{ bankTotalCount.toLocaleString('zh-CN') }} 个银行
          </span>
        </div>
      </div>

      <!-- 银行列表容器 -->
      <div class="bank-list-container">
        <!-- 空状态：无数据 -->
        <div v-if="!hasBankData && !bankLoading && !bankError" class="empty-state">
          <div class="empty-icon">🎵</div>
          <div class="empty-title">暂无音频银行数据</div>
          <div class="empty-desc">
            尚未建立音频银行索引。请先运行「启动扫描」建立资源索引，
            或检查项目目录中是否存在 FSB 音频文件。
          </div>
        </div>

        <!-- 银行虚拟列表 -->
        <VirtualList
          v-else
          :items="bankList"
          :item-height="40"
          :height="bankListHeight"
          :overscan="8"
        >
          <template #default="{ item, index }">
            <div
              class="bank-row"
              :class="{ selected: item.bankId === selectedBankId }"
              @click="selectBank(item.bankId)"
            >
              <span class="row-index lme-mono">{{ index + 1 + bankOffset }}</span>
              <span class="row-type-badge" :style="{ color: bankTypeColor(item.bankType) }">
                {{ bankTypeLabel(item.bankType) }}
              </span>
              <span class="row-name lme-ellipsis" :title="item.path">
                {{ item.name }}
              </span>
              <span class="row-count lme-mono">{{ item.sampleCount }} 采样</span>
              <span class="row-fsb lme-mono" :title="item.fsbInfo">{{ item.fsbInfo }}</span>
            </div>
          </template>
        </VirtualList>

        <!-- 页码条 -->
        <PageBar
          :current-page="currentPage"
          :page-count="pageCount"
          :total-count="bankTotalCount"
          :query-ms="lastBankQueryMs"
          :loading="bankLoading"
          @go-to="onGoToPage"
        />
      </div>

      <!-- 加载态 -->
      <div v-if="bankLoading" class="loading-overlay">
        <span class="loading-spinner">⏳</span>
        <span>加载音频银行…</span>
      </div>

      <!-- 错误态 -->
      <div v-if="bankError" class="error-banner">
        ⚠️ {{ bankError }}
      </div>
    </div>

    <!-- ═══════════════════════════════════════════════════════════ -->
    <!-- 分隔条                                                   -->
    <!-- ═══════════════════════════════════════════════════════════ -->
    <div
      class="column-splitter"
      :class="{ dragging: splitterDragging }"
      @mousedown="onSplitterMouseDown"
    />

    <!-- ═══════════════════════════════════════════════════════════ -->
    <!-- 预览 / 编辑列                                            -->
    <!-- ═══════════════════════════════════════════════════════════ -->
    <div class="preview-column" :style="{ width: uiState.previewColumnWidth + 'px' }">
      <!-- 银行详情头部 -->
      <div class="bank-detail-header" v-if="selectedBank">
        <div class="detail-title-row">
          <span class="detail-name lme-ellipsis" :title="selectedBank.path">
            {{ selectedBank.name }}
          </span>
          <button class="export-btn" @click="exportRebank" title="导出为 .rebank 格式">
            📤 导出
          </button>
        </div>
        <div class="detail-meta">
          <span class="meta-tag type-tag" :style="{ color: bankTypeColor(selectedBank.bankType) }">
            {{ bankTypeLabel(selectedBank.bankType) }}
          </span>
          <span class="meta-tag count-tag">
            {{ selectedBank.sampleCount }} 个采样
          </span>
          <span class="meta-tag size-tag">
            {{ formatSize(selectedBank.totalSize) }}
          </span>
          <span class="meta-tag path-tag lme-mono" :title="selectedBank.path">
            {{ selectedBank.path }}
          </span>
        </div>
        <div class="detail-fsb lme-mono" :title="selectedBank.fsbInfo">
          FSB: {{ selectedBank.fsbInfo }}
        </div>
      </div>

      <!-- 采样列表 -->
      <div class="sample-section">
        <div class="sample-section-header">
          <span class="section-title">采样列表</span>
          <span class="section-count lme-mono" v-if="sampleList.length > 0">
            {{ sampleList.length }} 项
          </span>
        </div>

        <!-- 采样列表表头 -->
        <div class="sample-table-header" v-if="sampleList.length > 0">
          <span class="col col-name">采样名称</span>
          <span class="col col-codec">编码</span>
          <span class="col col-duration">时长</span>
          <span class="col col-size">大小</span>
          <span class="col col-channels">声道</span>
          <span class="col col-rate">采样率</span>
        </div>

        <!-- 采样列表空状态 -->
        <div v-if="!sampleLoading && sampleList.length === 0 && selectedBank" class="sample-empty">
          <span class="empty-icon">🔇</span>
          <span>该银行暂无采样数据</span>
        </div>

        <!-- 采样加载错误 -->
        <div v-if="sampleError" class="sample-error">
          ⚠️ {{ sampleError }}
        </div>

        <!-- 采样虚拟列表 -->
        <VirtualList
          v-if="sampleList.length > 0"
          :items="sampleList"
          :item-height="28"
          :height="sampleListHeight"
          :overscan="6"
        >
          <template #default="{ item }">
            <div
              class="sample-row"
              :class="{ selected: item.sampleId === selectedSampleId }"
              @click="selectSample(item)"
            >
              <span class="col col-name lme-ellipsis" :title="item.name">
                {{ item.name }}
              </span>
              <span class="col col-codec lme-mono">{{ item.codec }}</span>
              <span class="col col-duration lme-mono">{{ formatDuration(item.duration) }}</span>
              <span class="col col-size lme-mono">{{ formatSize(item.size) }}</span>
              <span class="col col-channels lme-mono">{{ item.channels }}ch</span>
              <span class="col col-rate lme-mono">{{ item.sampleRate }} Hz</span>
            </div>
          </template>
        </VirtualList>

        <!-- 采样加载态 -->
        <div v-if="sampleLoading" class="sample-loading">
          <span class="loading-spinner">⏳</span>
          <span>加载采样列表…</span>
        </div>
      </div>

      <!-- 音频预览播放器 -->
      <div class="audio-preview-section">
        <div class="preview-section-header">
          <span class="section-title">音频预览</span>
          <span class="section-sample-name lme-ellipsis" v-if="selectedSample" :title="selectedSample.name">
            {{ selectedSample.name }}
          </span>
        </div>

        <!-- 有预览 URL -->
        <div v-if="audioPreviewUrl" class="audio-player-container">
          <audio
            class="audio-player"
            controls
            preload="auto"
            :src="audioPreviewUrl"
          >
            浏览器不支持音频播放
          </audio>
        </div>

        <!-- 未实现提示 -->
        <div
          v-else-if="selectedSample && !sampleLoading && audioPreviewUnsupported"
          class="audio-unimplemented"
        >
          <span class="empty-icon">🔇</span>
          <span>音频预览暂未实现</span>
          <span class="unimplemented-hint">
            后端 bank.preview 返回 unsupported：该样本暂无法解码（需 FMOD 链路）
          </span>
        </div>

        <!-- 未选中采样 -->
        <div v-else class="audio-empty">
          <span class="empty-icon">🎧</span>
          <span>请选择一个采样以预览</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* ── 根容器 ── */
.bank-view {
  display: flex;
  height: 100%;
  overflow: hidden;
  position: relative;
}

/* ── 浏览列 ── */
.browser-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
}

/* ── 搜索筛选栏 ── */
.search-filters {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
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

.filter-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.filter-select {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
}

.filter-select:focus {
  outline: none;
  border-color: var(--lme-accent);
}

.filter-clear-btn {
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: background 0.15s, color 0.15s;
}

.filter-clear-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  margin-left: auto;
}

/* ── 银行列表容器 ── */
.bank-list-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
}

/* ── 银行行 ── */
.bank-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 3px solid transparent;
  transition: background 0.1s;
}

.bank-row:hover {
  background: var(--lme-bg-hover);
}

.bank-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.row-index {
  width: 48px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.row-type-badge {
  width: 72px;
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
  font-weight: 600;
  text-align: center;
  padding: 2px 4px;
  border-radius: var(--lme-radius-sm);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
}

.row-name {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.row-count {
  width: 72px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.row-fsb {
  width: 120px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 分隔条 ── */
.column-splitter {
  width: 6px;
  flex-shrink: 0;
  background: var(--lme-border);
  cursor: col-resize;
  transition: background 0.15s;
}

.column-splitter:hover,
.column-splitter.dragging {
  background: var(--lme-accent);
}

/* ── 预览列 ── */
.preview-column {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--lme-bg-panel);
  min-width: 260px;
}

/* ── 银行详情头部 ── */
.bank-detail-header {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.detail-title-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.detail-name {
  flex: 1;
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-primary);
  min-width: 0;
}

.export-btn {
  padding: 4px 12px;
  background: var(--lme-accent-muted);
  border: 1px solid var(--lme-accent);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  white-space: nowrap;
  transition: background 0.15s;
}

.export-btn:hover {
  background: var(--lme-accent);
}

.detail-meta {
  display: flex;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.meta-tag {
  padding: 2px 6px;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
}

.count-tag {
  color: var(--lme-text-secondary);
}

.size-tag {
  color: var(--lme-text-muted);
}

.path-tag {
  color: var(--lme-text-muted);
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.detail-fsb {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 采样区域 ── */
.sample-section {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
  border-bottom: 1px solid var(--lme-border);
}

.sample-section-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.section-title {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.section-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-left: auto;
}

/* ── 采样表头 ── */
.sample-table-header {
  display: flex;
  align-items: center;
  padding: 0 var(--lme-gap-md);
  gap: var(--lme-gap-sm);
  height: 28px;
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
  flex-shrink: 0;
}

.sample-table-header .col {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  font-weight: 600;
}

.col-name {
  flex: 1;
  min-width: 0;
}
.col-codec {
  width: 64px;
  flex-shrink: 0;
  text-align: center;
}
.col-duration {
  width: 56px;
  flex-shrink: 0;
  text-align: right;
}
.col-size {
  width: 64px;
  flex-shrink: 0;
  text-align: right;
}
.col-channels {
  width: 48px;
  flex-shrink: 0;
  text-align: center;
}
.col-rate {
  width: 80px;
  flex-shrink: 0;
  text-align: right;
}

/* ── 采样行 ── */
.sample-row {
  display: flex;
  align-items: center;
  padding: 0 var(--lme-gap-md);
  gap: var(--lme-gap-sm);
  cursor: pointer;
  border-left: 3px solid transparent;
  transition: background 0.1s;
}

.sample-row:hover {
  background: var(--lme-bg-hover);
}

.sample-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-type-audio);
}

.sample-row .col {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sample-row .col-codec,
.sample-row .col-duration,
.sample-row .col-size,
.sample-row .col-channels,
.sample-row .col-rate {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

.sample-empty,
.sample-error,
.sample-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  flex: 1;
  color: var(--lme-text-muted);
  padding: var(--lme-gap-xl);
  text-align: center;
}

.sample-error {
  color: var(--lme-error);
}

.sample-loading {
  flex-direction: row;
}

/* ── 音频预览区域 ── */
.audio-preview-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  border-top: 1px solid var(--lme-border);
  flex-shrink: 0;
}

.preview-section-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.section-sample-name {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-left: auto;
  max-width: 200px;
}

.audio-player-container {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
  border: 1px solid var(--lme-border);
}

.audio-player {
  width: 100%;
  height: 36px;
}

.audio-player::-webkit-media-controls-panel {
  background: var(--lme-bg-input);
}

.audio-unimplemented,
.audio-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-lg);
  color: var(--lme-text-muted);
  text-align: center;
}

.unimplemented-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-disabled);
  margin-top: var(--lme-gap-xs);
}

/* ── 空状态 ── */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  flex: 1;
  padding: var(--lme-gap-xl);
  text-align: center;
}

.empty-icon {
  font-size: 32px;
  margin-bottom: var(--lme-gap-sm);
}

.empty-title {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.empty-desc {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  max-width: 320px;
  line-height: 1.6;
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
  font-size: var(--lme-font-size-sm);
}

.loading-spinner {
  font-size: var(--lme-font-size-lg);
}

/* ── 错误态 ── */
.error-banner {
  padding: var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
}
</style>
