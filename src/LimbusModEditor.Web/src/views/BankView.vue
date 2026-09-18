<script setup lang="ts">
// 音频工作台（Bank 浏览 / 预览 / 导出）
// 对应 WPF 旧界面的 AudioWorkbenchPage
// 布局：页头工具栏 + 三栏面板（银行 | 采样 | 试听与详情），与 StaticView 同一套设计语言
//
// ui-redesign r6（本次改造）：页头换成 PageHeader（图标 / 说明 / 可关闭指引），
// 手写的加载/空/错误态换成 StateBlock，顶部细进度条改用 .lme-loadingbar 工具类，
// 页面里的 emoji 全部换成 AppIcon，导出接入 status.track（进度与结果进底部状态栏）。
// IPC 方法名、载荷字段与调用时序零改动；?bank=&sample= 深链保持可用。

import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc, IpcClientError } from '@/ipc'
import {
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NInput,
  NSelect,
  NTag,
  NTooltip,
  useMessage,
} from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import StateBlock from '@/components/StateBlock.vue'
import VirtualList from '@/components/VirtualList.vue'
import PageBar from '@/components/PageBar.vue'
import { useUiStateStore } from '@/stores/uiState'
import { useStatusStore } from '@/stores/status'

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()

/** 全局状态：耗时操作登记为「活动」，失败/完成同时记一条全局通知 */
const status = useStatusStore()

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
const route = useRoute()
const router = useRouter()

/**
 * 维基页「去编辑」带过来的深链目标：bank = 银行路径（bank.list 的 bankId 口径）、
 * sample = 采样名。取到就在列表加载/采样加载后定位一次，用完即清；
 * 定位不到（例如该银行不在当前页）就什么都不做——不猜、不伪造选中项。
 */
const deepLinkBankId = ref<string | null>(null)
const deepLinkSampleName = ref<string | null>(null)

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

    // 有深链目标就先定位它（bankId 直接可用，不依赖它是否落在当前分页里）
    if (deepLinkBankId.value) {
      const target = deepLinkBankId.value
      deepLinkBankId.value = null
      await selectBank(target)
    } else if (bankList.value.length > 0 && !selectedBankId.value) {
      // 自动选中第一条
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

    // 深链指定了采样名：命中就选中它（顺带触发一次预览），否则按原逻辑选第一条
    var deepLinked: SampleInfo | undefined
    if (deepLinkSampleName.value) {
      const name = deepLinkSampleName.value
      deepLinkSampleName.value = null
      deepLinked = sampleList.value.find((s) => s.name === name)
    }
    if (deepLinked) await selectSample(deepLinked)
    // 自动选中第一条采样
    else if (sampleList.value.length > 0) {
      selectSample(sampleList.value[0])
    }
  } catch (e: unknown) {
    if (gen !== sampleGeneration.value) return
    sampleError.value = e instanceof Error ? e.message : String(e)
    status.notify('error', `读取采样列表失败：${sampleError.value}`)
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
    // 契约方法 bank.preview：载荷 { bankId, sampleName } 返回 { audioUrl }（可直接播放的 WAV 地址）
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
  const bank = selectedBank.value
  if (!bank) return

  try {
    // BankExportRebankRequest 需要 targetDirectory：走宿主原生目录对话框（不用 Web 文件输入）
    const picked = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: '选择 .rebank 导出目录',
    })
    if (!picked.path) return

    // 耗时导出登记进全局状态：底部状态栏可见进度，成功/失败都发一条通知
    await status.track(
      'bank-export',
      '正在导出音频',
      () =>
        ipc.request('bank.exportRebank', {
          bankId: bank.bankId,
          targetDirectory: picked.path,
        }),
      { successText: `已导出「${bank.name}」为 .rebank`, errorPrefix: '导出音频' },
    )
  } catch {
    // 导出 IPC 未实现
    showNotImplemented()
  }
}

/** 显示"暂未实现"提示（走 message 通道，不污染 bankError 错误态） */
function showNotImplemented() {
  message.warning('暂未实现：该功能对应的 IPC 方法尚未在后端实现')
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
  // 维基页「去编辑」的深链参数（/bank?bank=…&sample=…）：取到就定位，用完立刻清掉
  const bank = typeof route.query.bank === 'string' && route.query.bank ? route.query.bank : null
  const sample =
    typeof route.query.sample === 'string' && route.query.sample ? route.query.sample : null
  if (bank || sample) {
    deepLinkBankId.value = bank
    deepLinkSampleName.value = sample
    router.replace({ path: route.path, query: {} })
  }
  performSearch()
})
</script>

<template>
  <div class="bank-view">
    <!-- 页头：标题 + 一句话说明 + 可关闭的操作指引 -->
    <PageHeader
      icon="audio"
      title="音频工作台"
      description="试听游戏音频库里的音效，并把选中的条目替换成自己的音频文件"
      hint="先在左侧选一个音频库，再在列表里点一条试听"
      hint-key="bank"
    >
      <template #meta>
        <span class="toolbar-sub" v-if="selectedBank">
          <span class="toolbar-bank lme-ellipsis">{{ selectedBank.name }}</span>
          <span class="toolbar-dot">·</span>
          {{ selectedBank.sampleCount.toLocaleString('zh-CN') }} 个采样
          <span class="toolbar-dot">·</span>
          {{ formatSize(selectedBank.totalSize) }}
        </span>
        <NTag size="small" :bordered="false" v-if="!bankLoading">
          共 <strong>{{ bankTotalCount.toLocaleString('zh-CN') }}</strong> 个银行
        </NTag>
      </template>

      <template #actions>
        <NTooltip placement="bottom" :show-arrow="false">
          <template #trigger>
            <NButton size="small" :disabled="!selectedBank" @click="exportRebank">
              <template #icon>
                <AppIcon name="export" :size="14" />
              </template>
              导出 .rebank
            </NButton>
          </template>
          导出为 .rebank 格式（需先选中一个银行）
        </NTooltip>
      </template>
    </PageHeader>

    <!-- 顶部细进度条：银行列表或采样列表加载时显示（替代整屏遮罩） -->
    <div v-if="bankLoading || sampleLoading" class="lme-loadingbar" aria-hidden="true" />

    <!-- 三栏：银行 | 采样 | 试听与详情 -->
    <div class="workbench-body">
      <!-- ── 左：银行列表 ── -->
      <NCard size="small" class="panel panel-banks">
        <template #header>
          <div class="panel-head">
            <NInput
              v-model:value="query.text"
              class="search-input"
              size="small"
              clearable
              placeholder="搜索银行路径或采样名称（支持中文）"
              @input="onSearchInput"
              @clear="onSearchInput"
            >
              <template #prefix>
                <span class="panel-search-icon">
                  <AppIcon name="search" :size="13" />
                </span>
              </template>
            </NInput>

            <div class="filter-row">
              <span class="filter-label">类型</span>
              <NTooltip placement="bottom" :show-arrow="false">
                <template #trigger>
                  <NSelect
                    v-model:value="query.bankType"
                    class="filter-select"
                    size="small"
                    :options="bankTypeOptions"
                    @update:value="onFilterChange"
                  />
                </template>
                按银行类型筛选（后端暂不提供类型时统一显示「未知」）
              </NTooltip>

              <span class="filter-label">排序</span>
              <NTooltip placement="bottom" :show-arrow="false">
                <template #trigger>
                  <NSelect
                    v-model:value="query.sort"
                    class="filter-select"
                    size="small"
                    :options="sortOptions"
                    @update:value="onFilterChange"
                  />
                </template>
                按名称 / 采样数 / 大小 / 类型排序
              </NTooltip>

              <NTooltip placement="bottom" :show-arrow="false">
                <template #trigger>
                  <NButton size="small" tertiary @click="clearFilters">清除筛选</NButton>
                </template>
                清空关键词、类型与排序，回到默认视图
              </NTooltip>
            </div>
          </div>
        </template>

        <div class="panel-body">
          <VirtualList
            v-if="hasBankData"
            :items="bankList"
            :item-height="30"
            :overscan="8"
            grow
          >
            <template #default="{ item, index }">
              <div
                class="bank-row"
                :class="{ selected: item.bankId === selectedBankId }"
                :title="item.path"
                @click="selectBank(item.bankId)"
              >
                <span class="row-index lme-mono">{{ index + 1 + bankOffset }}</span>
                <NTag
                  size="small"
                  :bordered="false"
                  class="row-type-badge"
                  :style="{ color: bankTypeColor(item.bankType) }"
                >
                  {{ bankTypeLabel(item.bankType) }}
                </NTag>
                <span class="row-name lme-ellipsis">{{ item.name }}</span>
                <span class="row-count lme-mono">{{ item.sampleCount }} 采样</span>
                <NTag
                  v-if="item.fsbInfo"
                  size="small"
                  :bordered="false"
                  class="row-fsb lme-mono"
                  :title="item.fsbInfo"
                >
                  {{ item.fsbInfo }}
                </NTag>
              </div>
            </template>
          </VirtualList>

          <!-- 错误态：银行列表没读到，整块换成可重试的提示 -->
          <StateBlock
            v-else-if="bankError"
            class="fill"
            state="error"
            icon="database"
            :title="bankError"
            description="检查游戏目录设置与资源索引，再点「重试」重新读取"
          >
            <template #actions>
              <NButton size="small" @click="performSearch">重试</NButton>
            </template>
          </StateBlock>

          <!-- 初次加载态 -->
          <StateBlock
            v-else-if="bankLoading"
            class="fill"
            state="loading"
            title="正在读取音频银行…"
          />

          <!-- 空态 -->
          <StateBlock
            v-else
            class="fill"
            state="empty"
            icon="audio"
            title="暂无音频银行数据"
            description="先运行「启动扫描」建立资源索引，或确认项目目录里存在 FSB 音频文件"
          />
        </div>

        <template #footer>
          <PageBar
            :current-page="currentPage"
            :page-count="pageCount"
            :total-count="bankTotalCount"
            :query-ms="lastBankQueryMs"
            :loading="bankLoading"
            @go-to="onGoToPage"
          />
        </template>
      </NCard>

      <!-- ── 中：采样列表 ── -->
      <NCard size="small" class="panel panel-samples">
        <template #header>
          <div class="panel-head compact">
            <div class="panel-title">
              <span class="panel-title-text">采样列表</span>
              <NTag size="small" :bordered="false" class="panel-badge lme-mono">
                {{ sampleList.length.toLocaleString('zh-CN') }}
              </NTag>
            </div>

            <span class="panel-bank lme-ellipsis" v-if="selectedBank" :title="selectedBank.path">
              {{ selectedBank.name }}
            </span>
          </div>
        </template>

        <div class="panel-body">
          <!-- 采样加载错误 -->
          <StateBlock
            v-if="sampleError"
            class="fill"
            state="error"
            icon="database"
            :title="sampleError"
            description="在左侧换一个银行重试；若始终失败，请先重建资源索引"
          />

          <!-- 采样列表表头 -->
          <div class="sample-table-header" v-if="sampleList.length > 0">
            <span class="col col-name">采样名称</span>
            <span class="col col-codec">编码</span>
            <span class="col col-duration">时长</span>
            <span class="col col-size">大小</span>
            <span class="col col-channels">声道</span>
            <span class="col col-rate">采样率</span>
          </div>

          <VirtualList
            v-if="sampleList.length > 0"
            :items="sampleList"
            :item-height="30"
            :overscan="6"
            grow
          >
            <template #default="{ item }">
              <div
                class="sample-row"
                :class="{ selected: item.sampleId === selectedSampleId }"
                :title="item.name"
                @click="selectSample(item)"
              >
                <span class="col col-name lme-ellipsis">{{ item.name }}</span>
                <span class="col col-codec">
                  <NTag size="small" :bordered="false">{{ item.codec }}</NTag>
                </span>
                <span class="col col-duration">
                  <NTag size="small" :bordered="false">
                    {{ formatDuration(item.duration) }}
                  </NTag>
                </span>
                <span class="col col-size">
                  <NTag size="small" :bordered="false">{{ formatSize(item.size) }}</NTag>
                </span>
                <span class="col col-channels">
                  <NTag size="small" :bordered="false">{{ item.channels }}ch</NTag>
                </span>
                <span class="col col-rate">
                  <NTag size="small" :bordered="false">{{ item.sampleRate }} Hz</NTag>
                </span>
              </div>
            </template>
          </VirtualList>

          <!-- 采样加载态 -->
          <StateBlock
            v-else-if="sampleLoading"
            class="fill"
            state="loading"
            title="正在读取采样列表…"
          />

          <!-- 采样空态（错误已单独占位，避免两个态同时出现） -->
          <StateBlock
            v-else-if="!sampleError"
            class="fill"
            state="empty"
            icon="mute"
            :title="selectedBank ? '该银行暂无采样数据' : '未选择银行'"
            :description="
              selectedBank
                ? '换一个银行看看，或确认该 FSB 内确实含有采样'
                : '先在左侧选一个音频银行，再在列表里点一条试听'
            "
          />
        </div>
      </NCard>

      <!-- ── 分隔条（拖拽调整试听列宽，写回 uiState.previewColumnWidth） ── -->
      <div
        class="column-splitter"
        :class="{ dragging: splitterDragging }"
        title="拖拽调整试听列宽"
        @mousedown="onSplitterMouseDown"
      />

      <!-- ── 右：试听区 + 银行详情 ── -->
      <NCard
        size="small"
        class="panel panel-player"
        :style="{ width: uiState.previewColumnWidth + 'px' }"
      >
        <template #header>
          <div class="panel-head compact">
            <div class="panel-title">
              <span class="panel-title-text">试听</span>
              <NTag size="small" :bordered="false" class="panel-badge lme-mono">
                {{ selectedSample ? selectedSample.codec : '—' }}
              </NTag>
            </div>

            <NTag
              v-if="selectedBank"
              size="small"
              :bordered="false"
              class="panel-type"
              :style="{ color: bankTypeColor(selectedBank.bankType) }"
            >
              {{ bankTypeLabel(selectedBank.bankType) }}
            </NTag>
          </div>
        </template>

        <div class="panel-body">
          <!-- 播放器卡：当前样本名 / 轨道底纹 / 原生播放器 / 元信息 -->
          <div class="player-card">
            <div class="player-head">
              <div class="player-titles">
                <span class="player-name lme-ellipsis" :title="selectedSample ? selectedSample.name : ''">
                  {{ selectedSample ? selectedSample.name : '未选择采样' }}
                </span>
                <span class="player-sub lme-ellipsis" v-if="selectedBank" :title="selectedBank.path">
                  {{ selectedBank.name }}
                </span>
              </div>
            </div>

            <!-- 轨道底纹（纯装饰，不表示真实波形/进度） -->
            <div class="player-track" aria-hidden="true" />

            <div class="player-controls">
              <!-- 有预览 URL：原生播放器（controls/preload/src 三属性与 data 处理链路未改） -->
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

              <!-- 未实现提示（后端明确回 unsupported 才显示） -->
              <StateBlock
                v-else-if="selectedSample && !sampleLoading && audioPreviewUnsupported"
                class="state-inline"
                state="empty"
                icon="mute"
                title="音频预览暂未实现"
                description="后端 bank.preview 返回 unsupported：该样本暂无法解码（需 FMOD 链路）"
              />

              <!-- 未选中采样 -->
              <StateBlock
                v-else
                class="state-inline"
                state="empty"
                icon="audio"
                title="请选择一个采样以预览"
                description="点击中间列表里的任意一行"
              />
            </div>

            <!-- 当前样本元信息（胶囊） -->
            <div class="player-meta" v-if="selectedSample">
              <NTag size="small" :bordered="false">
                <span class="meta-label">编码</span>
                <span class="meta-value lme-mono">{{ selectedSample.codec }}</span>
              </NTag>
              <NTag size="small" :bordered="false">
                <span class="meta-label">时长</span>
                <span class="meta-value lme-mono">
                  {{ formatDuration(selectedSample.duration) }}
                </span>
              </NTag>
              <NTag size="small" :bordered="false">
                <span class="meta-label">声道</span>
                <span class="meta-value lme-mono">{{ selectedSample.channels }}ch</span>
              </NTag>
              <NTag size="small" :bordered="false">
                <span class="meta-label">采样率</span>
                <span class="meta-value lme-mono">{{ selectedSample.sampleRate }} Hz</span>
              </NTag>
              <NTag size="small" :bordered="false">
                <span class="meta-label">大小</span>
                <span class="meta-value lme-mono">{{ formatSize(selectedSample.size) }}</span>
              </NTag>
              <NTag size="small" :bordered="false" :title="selectedSample.bankId">
                <span class="meta-label">所属银行</span>
                <span class="meta-value lme-mono lme-ellipsis">{{ selectedSample.bankId }}</span>
              </NTag>
            </div>
          </div>

          <!-- 银行详情 -->
          <NDescriptions
            v-if="selectedBank"
            class="bank-detail"
            title="银行详情"
            size="small"
            :column="1"
            label-placement="left"
          >
            <NDescriptionsItem label="类型">
              <span :style="{ color: bankTypeColor(selectedBank.bankType) }">
                {{ bankTypeLabel(selectedBank.bankType) }}
              </span>
            </NDescriptionsItem>
            <NDescriptionsItem label="采样数">
              <span class="detail-value lme-mono">
                {{ selectedBank.sampleCount.toLocaleString('zh-CN') }}
              </span>
            </NDescriptionsItem>
            <NDescriptionsItem label="大小">
              <span class="detail-value lme-mono">{{ formatSize(selectedBank.totalSize) }}</span>
            </NDescriptionsItem>
            <NDescriptionsItem label="路径">
              <span class="detail-value path lme-mono" :title="selectedBank.path">
                {{ selectedBank.path }}
              </span>
            </NDescriptionsItem>
            <NDescriptionsItem v-if="selectedBank.fsbInfo" label="FSB">
              <span class="detail-value path lme-mono" :title="selectedBank.fsbInfo">
                {{ selectedBank.fsbInfo }}
              </span>
            </NDescriptionsItem>
          </NDescriptions>

          <StateBlock
            v-else
            class="fill"
            state="empty"
            icon="audio"
            title="未选择银行"
            description="在左侧选中一个银行后，这里显示试听与银行详情"
          />
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.bank-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  position: relative;
  background: var(--lme-bg-base);
}

/* 顶部细进度条改用工具类 .lme-loadingbar（见 tokens.css），此处不再重复实现 */

/* ── 页头右侧的选中银行摘要（计数与操作按钮由 PageHeader 插槽承载） ── */
.toolbar-sub {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.toolbar-bank {
  max-width: 320px;
  color: var(--lme-text-secondary);
}

.toolbar-dot {
  color: var(--lme-text-disabled);
}

/* ── 三栏栅格 ── */
.workbench-body {
  flex: 1;
  display: flex;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-md) var(--lme-gap-lg) var(--lme-gap-lg);
  min-height: 0;
  overflow: hidden;
}

/* 面板容器走库组件 NCard：底色/边框/圆角由 naiveTheme.ts 从 tokens 派生 */
.panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

/* 卡头：自带 padding 改由 tokens 控制，并补一条分隔线 */
.panel :deep(.n-card-header) {
  flex-shrink: 0;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
}

/* 卡体：撑满剩余高度，供 VirtualList(grow) 取到有界高度 */
.panel :deep(.n-card-content) {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  padding: 0;
  overflow: hidden;
}

.panel :deep(.n-card__footer) {
  flex-shrink: 0;
  padding: 0;
  background: var(--lme-bg-elevated);
  border-top: 1px solid var(--lme-border);
}

.panel-banks {
  flex: 0 0 320px;
}

.panel-samples {
  flex: 1;
}

.panel-player {
  flex-shrink: 0;
}

@media (max-width: 1180px) {
  .panel-banks {
    flex-basis: 260px;
  }
}

.panel-head {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.panel-head.compact {
  flex-direction: row;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-sm);
  min-height: 22px;
}

.panel-title {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.panel-title-text {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-secondary);
  white-space: nowrap;
}

.panel-badge {
  padding: 1px 8px;
  background: var(--lme-bg-base);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-full);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
  max-width: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-bank {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  max-width: 45%;
}

.panel-type {
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-medium);
  flex-shrink: 0;
}

.panel-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  position: relative;
}

/* ── 搜索框 / 筛选（库组件 NInput / NSelect） ── */
.search-input {
  width: 100%;
}

.panel-search-icon {
  display: inline-flex;
  align-items: center;
  color: var(--lme-text-muted);
}

.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.filter-label {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}

.filter-select {
  flex: 1;
  min-width: 96px;
}

/* ── 银行行 ── */
.bank-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  height: 30px;
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.bank-row:hover {
  background: var(--lme-bg-hover);
}

.bank-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

.bank-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.row-index {
  width: 36px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

/* 类型胶囊（NTag）：固定宽度，文字居中 */
.row-type-badge {
  width: 54px;
  flex-shrink: 0;
  justify-content: center;
}

.row-name {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.bank-row.selected .row-name {
  font-weight: var(--lme-font-weight-medium);
}

.row-count {
  width: 56px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.row-fsb {
  max-width: 96px;
  flex-shrink: 0;
  overflow: hidden;
}

/* 银行行内的胶囊统一压到行高内 */
.bank-row :deep(.n-tag) {
  max-height: 18px;
  padding: 0 var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
}

/* ── 采样列表 ── */
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
  font-weight: var(--lme-font-weight-semibold);
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

.sample-row {
  display: flex;
  align-items: center;
  height: 30px;
  padding: 0 var(--lme-gap-md);
  gap: var(--lme-gap-sm);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.sample-row:hover {
  background: var(--lme-bg-hover);
}

.sample-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

/* 已载入播放器的那一行：用音频播放底色区分（选中 = 已载入待播） */
.sample-row.selected {
  background: var(--wiki-audio-playing-bg);
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

/* 采样行的信息胶囊（NTag）：压到行高内，数值列按列对齐 */
.sample-row :deep(.n-tag) {
  width: 100%;
  max-height: 18px;
  padding: 0 var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
}

.sample-row .col-codec :deep(.n-tag),
.sample-row .col-channels :deep(.n-tag) {
  justify-content: center;
}

.sample-row .col-duration :deep(.n-tag),
.sample-row .col-size :deep(.n-tag),
.sample-row .col-rate :deep(.n-tag) {
  justify-content: flex-end;
}

/* ── 分隔条 ── */
.column-splitter {
  flex: 0 0 4px;
  border-radius: var(--lme-radius-full);
  background: var(--lme-border);
  cursor: col-resize;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.column-splitter:hover,
.column-splitter.dragging {
  background: var(--lme-accent);
}

/* ── 试听区 ── */
.player-card {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  margin: var(--lme-gap-md);
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-lg);
}

.player-head {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.player-titles {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.player-name {
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  line-height: var(--lme-line-height-tight);
}

.player-sub {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* 轨道底纹：中性装饰条，不表示真实波形或播放进度 */
.player-track {
  height: 6px;
  border-radius: var(--lme-radius-full);
  background: repeating-linear-gradient(
    to right,
    var(--lme-border-strong) 0,
    var(--lme-border-strong) 2px,
    transparent 2px,
    transparent 6px
  );
  opacity: 0.6;
}

.player-controls {
  display: flex;
  flex-direction: column;
}

.audio-player-container {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-xs);
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-md);
  border: 1px solid var(--lme-border);
}

.audio-player {
  width: 100%;
  height: 34px;
}

.audio-player::-webkit-media-controls-panel {
  background: var(--lme-bg-input);
}

/* ── 样本元信息（胶囊） ── */
.player-meta {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lme-gap-xs);
  margin: 0;
  padding-top: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border);
}

.player-meta :deep(.n-tag) {
  max-width: 100%;
}

.meta-label {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-right: var(--lme-gap-xs);
}

.meta-value {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  min-width: 0;
}

/* ── 银行详情（库组件 NDescriptions） ── */
.bank-detail {
  flex-shrink: 0;
  margin: 0 var(--lme-gap-md) var(--lme-gap-md);
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-lg);
}

.detail-value {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  min-width: 0;
}

.detail-value.path {
  display: block;
  max-width: 100%;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: var(--lme-gap-2xs) var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 三态块（StateBlock 自带排版；这里只调面板内的撑满与内嵌内边距） ── */
.panel-body > .state-block {
  min-height: 0;
}

/* 试听卡里的内嵌态：不撑满，收一点内边距 */
.state-inline {
  padding: var(--lme-gap-lg) var(--lme-gap-md);
}
</style>
