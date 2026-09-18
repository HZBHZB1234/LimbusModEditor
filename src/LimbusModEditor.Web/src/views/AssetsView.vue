<script setup lang="ts">
// 资源工作台竖切片（W1 前端一半）—— v2 样板重做（ui-redesign r1）
// 对应 WPF 旧界面 AssetsWorkbenchPage
// 能力清单与差异说明见文件底部注释；本重做只动模板与样式，数据链路（catalog/preview/编辑 IPC）一行未改

import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { usePreviewStore } from '@/stores/preview'
import { useUiStateStore } from '@/stores/uiState'
import { ipc } from '@/ipc'
import type { AssetSearchQuery, RelationSubject } from '@/ipc'
import SearchFilters from '@/components/SearchFilters.vue'
import VirtualList from '@/components/VirtualList.vue'
import PreviewPane from '@/components/PreviewPane.vue'
import PageBar from '@/components/PageBar.vue'
import ContainerTree from '@/components/ContainerTree.vue'

const catalog = useCatalogStore()
const previewStore = usePreviewStore()
const uiState = useUiStateStore()
const route = useRoute()
const router = useRouter()

// 性能测量状态
const perfMeasurements = ref<
  { action: string; timeMs: number; memoryMB?: number; timestamp: number }[]
>([])

function recordPerf(action: string, timeMs: number) {
  const mem = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory
  perfMeasurements.value.push({
    action,
    timeMs,
    memoryMB: mem ? Math.round(mem.usedJSHeapSize / 1024 / 1024) : undefined,
    timestamp: Date.now(),
  })
  // 只保留最近 20 条
  if (perfMeasurements.value.length > 20) {
    perfMeasurements.value.shift()
  }
}

// 视图模式
const viewMode = ref<'list' | 'tree'>('list')
const showPerfPanel = ref(false)

// 搜索处理
function onSearch(query: AssetSearchQuery) {
  const t0 = performance.now()
  catalog.search(query).then(() => {
    recordPerf('搜索+取页', performance.now() - t0)
  })
}

// 分页跳转
function onGoToPage(pageIndex: number) {
  const t0 = performance.now()
  catalog.goToPage(pageIndex).then(() => {
    recordPerf(`翻页→第${pageIndex + 1}页`, performance.now() - t0)
  })
}

// 选中资产
function onSelectAsset(assetId: string) {
  catalog.selectAsset(assetId)
}

// 列表行渲染
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function typeColor(type: string): string {
  const map: Record<string, string> = {
    Texture: 'var(--lme-type-texture)',
    Sprite: 'var(--lme-type-sprite)',
    Audio: 'var(--lme-type-audio)',
    Text: 'var(--lme-text-text)',
    Json: 'var(--lme-type-json)',
    MonoBehaviour: 'var(--lme-type-mono)',
    Mesh: 'var(--lme-type-mesh)',
    Animation: 'var(--lme-type-animation)',
    Font: 'var(--lme-type-font)',
    Material: 'var(--lme-type-material)',
    Shader: 'var(--lme-type-shader)',
    Video: 'var(--lme-type-video)',
    Component: 'var(--lme-type-component)',
  }
  return map[type] ?? 'var(--lme-type-unknown)'
}

function stateLabel(state: string): string {
  const map: Record<string, string> = {
    Unchanged: '未修改',
    Modified: '已修改',
    Added: '已添加',
    Deleted: '已删除',
    Conflict: '冲突',
    Invalid: '无效',
  }
  return map[state] ?? state
}

// ── 空态 / 初次加载（v2 新增展示态，不发请求） ──
const isEmpty = computed(
  () => !catalog.loading && !catalog.error && catalog.items.length === 0,
)
const isInitialLoading = computed(
  () => catalog.loading && catalog.items.length === 0,
)

// ═══════════ 按容器路径定位（维基「去编辑」深链）═══════════

/** /assets?container=<游戏内资源路径> */
const containerFromQuery = computed(() => {
  const raw = route.query.container
  return typeof raw === 'string' ? raw.trim() : ''
})

/**
 * 用现有能力定位：后端没有「按容器路径取一条」的 IPC（catalog.locate 只收
 * assetId），所以把路径填进搜索框查一次（文本匹配面含容器路径），再在当页
 * 结果里按 metadata.containerEntry 精确选中同一条。
 * 命中 → 行高亮 + 预览面板出图；未命中 → 只留搜索结果，路径在搜索框里可见。
 */
async function focusContainer(path: string) {
  await catalog.search({ text: path })
  const lower = path.toLowerCase()
  const hit = catalog.items.find(
    (a) =>
      (a.metadata?.containerEntry ?? '').toLowerCase() === lower ||
      a.logicalPath.toLowerCase() === lower,
  )
  if (hit) catalog.selectAsset(hit.assetId)
}

/** 定位完就摘掉 container 参数：刷新（或后退重进）不会再重复触发一次定位 */
function clearContainerParam() {
  if (route.query.container === undefined) return
  const next = { ...route.query }
  delete next.container
  void router.replace({ path: route.path, query: next })
}

// 初始加载
onMounted(async () => {
  const t0 = performance.now()
  const target = containerFromQuery.value
  if (target) await focusContainer(target)
  else await catalog.search()
  recordPerf(target ? '按容器路径定位' : '初始加载', performance.now() - t0)
  if (target) clearContainerParam()
})

// 已挂载时再从维基点一次「去编辑」：query 变了就重新定位，定位完清掉参数
watch(containerFromQuery, async (path) => {
  if (!path) return
  await focusContainer(path)
  clearContainerParam()
})

// ═══════════ 关联对象（只读列表，relation.describe）═══════════

/** 选中资源命中的对象行；失败/没命中就留空数组 */
const relatedSubjects = ref<RelationSubject[]>([])
/** 后端给的中文说明（也解释「为什么没有行」） */
const relatedInfo = ref('')

watch(
  () => catalog.selectedAssetId,
  async (assetId) => {
    relatedSubjects.value = []
    relatedInfo.value = ''
    if (!assetId) return
    try {
      const res = await ipc.request<{ subjects?: RelationSubject[]; info?: string }>(
        'relation.describe',
        { assetId },
      )
      relatedSubjects.value = res?.subjects ?? []
      relatedInfo.value = res?.info ?? ''
    } catch {
      // 调用失败就什么都不显示（不抛、不重试）
      relatedSubjects.value = []
      relatedInfo.value = ''
    }
  },
)

// ═══════════ 替换资源（asset.edit.replacePayload）════════════

const replaceBusy = ref(false)
/** 空 = 没做过；否则是一行中文结果（成功记替换文件，失败记后端 error.message） */
const replaceMessage = ref('')
const replaceFailed = ref(false)

/**
 * 选一个文件 → 登记为当前资源的替换 → 立刻刷新（本页重拉 + 预览重拉）。
 *
 * assetId 传 logicalPath：后端（be6ef81）两种口径都收，而本页列表行的稳定键就是它
 * （AssetId 每次回灌都可能变，用它做选中键会丢选中）。
 */
async function onReplaceAsset() {
  const asset = catalog.selectedAsset
  if (!asset || replaceBusy.value) return
  const logicalPath = asset.logicalPath

  replaceBusy.value = true
  replaceMessage.value = ''
  replaceFailed.value = false
  try {
    // 宿主原生对话框：filters 是 Win32 过滤串（见 docs/WEB-IPC-CONTRACT.md）
    const picked = await ipc.request<{ path?: string }>('dialog.openFile', {
      title: `替换资源：${logicalPath.split('/').pop() || logicalPath}`,
      filters: '所有文件 (*.*)|*.*',
    })
    if (!picked?.path) return // 用户取消，不算失败

    await ipc.request('asset.edit.replacePayload', {
      assetId: logicalPath,
      replacementPath: picked.path,
    })

    // 刷新：本页重拉（列表行的 editState 由后端按项目态给出「已修改」）+ 预览重拉
    await catalog.fetchPage(catalog.offset)
    const refreshed = catalog.items.find((a) => a.logicalPath === logicalPath)
    if (refreshed) {
      catalog.selectAsset(refreshed.assetId)
      await previewStore.loadPreview(refreshed.assetId)
    }
    replaceMessage.value = `已替换：${picked.path}`
  } catch (e: unknown) {
    replaceFailed.value = true
    replaceMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    replaceBusy.value = false
  }
}

// ═══════════ 批量替换（asset.edit.batchReplace）════════════

const batchBusy = ref(false)
/** 后端逐条给的跳过原因（warnings：没有同名资源 / 已有替换标记未覆盖 / 文件名不匹配…） */
const batchWarnings = ref<string[]>([])

/**
 * 选一个<b>源目录</b> → 按文件名匹配项目里的资源批量登记替换。
 * 载荷按后端 `AssetEditBatchReplaceRequest`：`{ sourceDirectory, namePattern?, onlyUnreplaced? }`。
 */
async function onBatchReplace() {
  if (batchBusy.value) return

  batchBusy.value = true
  replaceMessage.value = ''
  replaceFailed.value = false
  batchWarnings.value = []
  try {
    // 宿主原生目录对话框（见 docs/WEB-IPC-CONTRACT.md）
    const picked = await ipc.request<{ path?: string }>('dialog.folderPick', {
      title: '选择替换资源所在的目录（按文件名匹配项目里的资源）',
    })
    if (!picked?.path) return // 用户取消，不算失败

    const res = await ipc.request<{
      replaced?: number
      skipped?: number
      warnings?: string[]
      info?: string
    }>('asset.edit.batchReplace', {
      sourceDirectory: picked.path,
      onlyUnreplaced: true,
    })

    replaceMessage.value =
      `替换 ${res?.replaced ?? 0} 条 / 跳过 ${res?.skipped ?? 0} 条` +
      (res?.info ? ` · ${res.info}` : '')
    batchWarnings.value = res?.warnings ?? []

    // 刷新：本页重拉（列表行「已修改」由后端按项目态给出）+ 预览重拉
    await catalog.fetchPage(catalog.offset)
    const selected = catalog.selectedAssetId
    if (selected) await previewStore.loadPreview(selected)
  } catch (e: unknown) {
    replaceFailed.value = true
    replaceMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    batchBusy.value = false
  }
}

// ═══════════ 撤销编辑（asset.edit.clearEdits）════════════

const clearBusy = ref(false)

/**
 * 撤销当前选中资源的全部编辑（替换 / Unity 字段 / Sprite），并同步清掉项目编辑清单里的记录。
 * 载荷按后端 `AssetEditClearEditsRequest`：`{ assetId }`（传 logicalPath，与单条替换同口径）。
 */
async function onClearEdits() {
  const asset = catalog.selectedAsset
  if (!asset || clearBusy.value) return
  const logicalPath = asset.logicalPath

  clearBusy.value = true
  replaceMessage.value = ''
  replaceFailed.value = false
  batchWarnings.value = []
  try {
    const res = await ipc.request<{
      clearedCount?: number
      remainingEdits?: number
      info?: string
    }>('asset.edit.clearEdits', { assetId: logicalPath })

    replaceMessage.value =
      res?.info ?? `已清掉 ${res?.clearedCount ?? 0} 条，剩余 ${res?.remainingEdits ?? 0} 条`

    // 刷新：本页重拉（编辑标记回到「未修改」）+ 预览重拉（回到原版）
    await catalog.fetchPage(catalog.offset)
    const refreshed = catalog.items.find((a) => a.logicalPath === logicalPath)
    if (refreshed) {
      catalog.selectAsset(refreshed.assetId)
      await previewStore.loadPreview(refreshed.assetId)
    }
  } catch (e: unknown) {
    replaceFailed.value = true
    replaceMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    clearBusy.value = false
  }
}

// 虚拟列表高度自适应
const listHeight = ref(600)
</script>

<template>
  <div class="assets-view">
    <!-- 中栏：搜索 + 筛选 + 浏览 -->
    <div class="browser-column">
      <!-- 加载进度条（顶部细条，替代整屏遮罩的常规路径） -->
      <div v-if="catalog.loading" class="loading-bar" aria-hidden="true" />

      <!-- 搜索筛选栏 -->
      <SearchFilters
        :model-value="catalog.query"
        @search="onSearch"
      />

      <!-- 视图切换 + 计数 + 性能 -->
      <div class="view-toggle-row">
        <div class="segmented" role="tablist">
          <button
            class="segmented-btn"
            :class="{ active: viewMode === 'list' }"
            @click="viewMode = 'list'"
          >
            ☰ 列表
          </button>
          <button
            class="segmented-btn"
            :class="{ active: viewMode === 'tree' }"
            @click="viewMode = 'tree'"
          >
            🗂 目录树
          </button>
        </div>

        <span class="result-count" v-if="!catalog.loading">
          共 <strong>{{ catalog.totalCount.toLocaleString('zh-CN') }}</strong> 条命中
        </span>

        <button
          class="perf-toggle"
          @click="showPerfPanel = !showPerfPanel"
          :class="{ active: showPerfPanel }"
        >
          📊 性能
        </button>
      </div>

      <!-- 列表视图 -->
      <div v-if="viewMode === 'list'" class="list-container" :class="{ dimmed: catalog.loading }">
        <div class="list-header">
          <span class="col-index">#</span>
          <span class="col-type">类型</span>
          <span class="col-name">名称</span>
          <span class="col-size">大小</span>
          <span class="col-state">状态</span>
        </div>

        <VirtualList
          :items="catalog.items"
          :item-height="30"
          :height="listHeight"
          :overscan="8"
        >
          <template #default="{ item, index }">
            <div
              class="asset-row"
              :class="{ selected: item.assetId === catalog.selectedAssetId }"
              @click="onSelectAsset(item.assetId)"
              @dblclick="onSelectAsset(item.assetId)"
            >
              <span class="row-index lme-mono">{{ index + 1 + catalog.offset }}</span>
              <span class="row-type">
                <span class="type-dot" :style="{ background: typeColor(item.type) }" />
                <span class="type-name" :style="{ color: typeColor(item.type) }">{{ item.type }}</span>
              </span>
              <span class="row-name lme-ellipsis" :title="item.logicalPath">
                {{ item.logicalPath.split('/').pop() || item.logicalPath }}
              </span>
              <span class="row-size lme-mono">{{ formatSize(item.size) }}</span>
              <span
                class="row-state"
                :class="'state-' + item.editState"
              >
                {{ stateLabel(item.editState) }}
              </span>
            </div>
          </template>
        </VirtualList>

        <!-- 页码条 -->
        <PageBar
          :current-page="catalog.currentPage"
          :page-count="catalog.pageCount"
          :total-count="catalog.totalCount"
          :query-ms="catalog.lastQueryMs"
          :loading="catalog.loading"
          @go-to="onGoToPage"
        />
      </div>

      <!-- 目录树视图 -->
      <ContainerTree
        v-else
        :visible="viewMode === 'tree'"
        @select="onSelectAsset"
      />

      <!-- 初次加载态 -->
      <div v-if="isInitialLoading" class="state-block">
        <span class="state-icon spinner">⏳</span>
        <span class="state-text">正在查询资源目录…</span>
      </div>

      <!-- 空态 -->
      <div v-else-if="isEmpty && viewMode === 'list'" class="state-block">
        <span class="state-icon">🗄</span>
        <span class="state-text">没有命中的资源</span>
        <span class="state-hint">换个关键词，或在上方「清除筛选」恢复默认视图</span>
      </div>

      <!-- 错误态 -->
      <div v-if="catalog.error" class="error-banner">
        <span class="error-banner-icon">⚠️</span>
        <span>{{ catalog.error }}</span>
      </div>
    </div>

    <!-- 分隔条 -->
    <div class="column-splitter" />

    <!-- 预览列 -->
    <div class="preview-column" :style="{ width: uiState.previewColumnWidth + 'px' }">
      <!-- 编辑操作组：选中一条资源后可用 -->
      <div class="edit-actions">
        <button
          class="action-btn primary"
          :disabled="!catalog.selectedAsset || replaceBusy"
          :title="catalog.selectedAsset ? catalog.selectedAsset.logicalPath : '先选中一条资源'"
          @click="onReplaceAsset"
        >
          {{ replaceBusy ? '替换中…' : '替换…' }}
        </button>
        <button
          class="action-btn"
          :disabled="batchBusy || replaceBusy"
          title="选一个目录，按文件名批量登记替换（已有替换标记的资源默认不覆盖）"
          @click="onBatchReplace"
        >
          {{ batchBusy ? '批量替换中…' : '批量替换…' }}
        </button>
        <button
          class="action-btn"
          :disabled="!catalog.selectedAsset || clearBusy || replaceBusy"
          :title="catalog.selectedAsset ? `撤销「${catalog.selectedAsset.logicalPath}」的编辑` : '先选中一条资源'"
          @click="onClearEdits"
        >
          {{ clearBusy ? '撤销中…' : '撤销编辑' }}
        </button>
      </div>

      <!-- 操作结果（成功中性 / 失败红） -->
      <div
        v-if="replaceMessage"
        class="action-message"
        :class="{ failed: replaceFailed }"
      >
        {{ replaceMessage }}
      </div>

      <!-- 批量替换的跳过原因（后端逐条给，照原样列出来） -->
      <ul v-if="batchWarnings.length > 0" class="batch-warnings">
        <li v-for="(w, i) in batchWarnings" :key="i">{{ w }}</li>
      </ul>

      <PreviewPane :asset="catalog.selectedAsset" />

      <!-- 关联对象：只读列表，数据来自 relation.describe -->
      <div v-if="catalog.selectedAsset" class="related-card">
        <div class="related-title">关联对象</div>
        <ul class="related-list">
          <li v-for="s in relatedSubjects" :key="s.subjectId" class="related-item">
            <div class="related-item-main">
              <span class="related-item-name">{{ s.displayName }}</span>
              <span class="related-item-cat">{{ s.categoryLabel }}</span>
              <span class="related-item-kind">{{ s.kindLabel }}</span>
            </div>
            <div class="related-item-display lme-mono">{{ s.display }}</div>
            <RouterLink v-if="s.pageId" class="related-item-link" :to="`/wiki/page/${s.pageId}`">
              打开维基页 →
            </RouterLink>
          </li>
          <li v-if="relatedSubjects.length === 0" class="related-empty">未找到关联对象</li>
        </ul>
        <div v-if="relatedInfo" class="related-info">{{ relatedInfo }}</div>
      </div>
    </div>

    <!-- 性能面板 -->
    <div v-if="showPerfPanel" class="perf-panel">
      <div class="perf-panel-header">
        <span>性能实测</span>
        <button @click="perfMeasurements = []">清除</button>
      </div>
      <table class="perf-table">
        <thead>
          <tr>
            <th>操作</th>
            <th>耗时 (ms)</th>
            <th>内存 (MB)</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(m, i) in perfMeasurements" :key="i">
            <td>{{ m.action }}</td>
            <td>{{ m.timeMs.toFixed(1) }}</td>
            <td>{{ m.memoryMB ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      <div class="perf-summary" v-if="perfMeasurements.length > 0">
        <div>最近操作数: {{ perfMeasurements.length }}</div>
        <div v-if="catalog.items.length > 0">
          当前页: {{ catalog.items.length }} 条 / {{ catalog.pageSize }} 条每页
        </div>
        <div>数据规模: 1,275,623 条资源 / 1,459 bundle</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.assets-view {
  display: flex;
  height: 100%;
  overflow: hidden;
  position: relative;
  background: var(--lme-bg-base);
}

.browser-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
  position: relative;
}

/* ── 加载进度条（顶部细条） ── */
.loading-bar {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 2px;
  overflow: hidden;
  z-index: var(--lme-z-raised);
  background: var(--lme-progressbar-bg);
}

.loading-bar::after {
  content: '';
  position: absolute;
  inset: 0;
  width: 40%;
  border-radius: var(--lme-radius-full);
  background: var(--lme-progressbar-fill);
  animation: loading-slide 1s var(--lme-ease-standard) infinite;
}

@keyframes loading-slide {
  from {
    transform: translateX(-100%);
  }
  to {
    transform: translateX(350%);
  }
}

/* ── 视图切换行 ── */
.view-toggle-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.segmented {
  display: inline-flex;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  overflow: hidden;
}

.segmented-btn {
  padding: 4px 14px;
  background: var(--lme-bg-panel);
  border: none;
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.segmented-btn + .segmented-btn {
  border-left: 1px solid var(--lme-border);
}

.segmented-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.segmented-btn.active {
  background: var(--lme-accent-muted);
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-medium);
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  margin-right: auto;
}

.result-count strong {
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-semibold);
}

.perf-toggle {
  padding: 3px 10px;
  background: none;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-family);
  transition: color var(--lme-dur-fast) var(--lme-ease-standard),
    border-color var(--lme-dur-fast) var(--lme-ease-standard);
}

.perf-toggle.active {
  border-color: var(--lme-warning);
  color: var(--lme-warning);
}

/* ── 列表 ── */
.list-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
  position: relative;
  transition: opacity var(--lme-dur-base) var(--lme-ease-standard);
}

.list-container.dimmed {
  opacity: 0.55;
}

.list-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 0 var(--lme-gap-md);
  height: 28px;
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-panel);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  user-select: none;
}

.col-index { width: 48px; text-align: right; flex-shrink: 0; }
.col-type { width: 110px; flex-shrink: 0; }
.col-name { flex: 1; min-width: 0; }
.col-size { width: 72px; text-align: right; flex-shrink: 0; }
.col-state { width: 64px; text-align: center; flex-shrink: 0; }

/* ── 资产行 ── */
.asset-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.asset-row:hover {
  background: var(--lme-bg-hover);
}

.asset-row.selected {
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

.row-type {
  width: 110px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.type-dot {
  width: 8px;
  height: 8px;
  border-radius: var(--lme-radius-full);
  flex-shrink: 0;
}

.type-name {
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-medium);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-name {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.row-size {
  width: 72px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.row-state {
  width: 64px;
  font-size: var(--lme-font-size-xs);
  text-align: center;
  padding: 1px 6px;
  border-radius: var(--lme-radius-full);
  flex-shrink: 0;
}

.state-Unchanged {
  color: var(--lme-text-muted);
}
.state-Modified {
  color: var(--lme-state-modified);
  background: var(--lme-state-modified-bg);
}
.state-Added {
  color: var(--lme-state-added);
  background: var(--lme-state-added-bg);
}
.state-Deleted {
  color: var(--lme-state-deleted);
  background: var(--lme-state-deleted-bg);
}

/* ── 空态 / 初次加载 ── */
.state-block {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  color: var(--lme-text-muted);
  pointer-events: none;
}

.state-icon {
  font-size: 36px;
  opacity: 0.7;
}

.state-icon.spinner {
  animation: state-spin 1.4s linear infinite;
  display: inline-block;
}

@keyframes state-spin {
  to {
    transform: rotate(360deg);
  }
}

.state-text {
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-secondary);
}

.state-hint {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

/* ── 错误态 ── */
.error-banner {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  border-radius: var(--lme-radius-md);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
}

/* ── 分隔条 ── */
.column-splitter {
  width: 1px;
  flex-shrink: 0;
  background: var(--lme-border);
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

/* ── 编辑操作组 ── */
.edit-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.action-btn {
  padding: 5px 14px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  flex-shrink: 0;
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.action-btn:hover:not(:disabled) {
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.action-btn.primary {
  background: var(--lme-accent-muted);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-medium);
}

.action-btn.primary:hover:not(:disabled) {
  background: var(--lme-accent);
}

.action-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.action-message {
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-success);
  border-bottom: 1px solid var(--lme-border);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.action-message.failed {
  color: var(--lme-error);
}

/* ── 批量替换的跳过原因 ── */
.batch-warnings {
  margin: 0;
  padding: var(--lme-gap-xs) var(--lme-gap-md) var(--lme-gap-sm) 28px;
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  line-height: var(--lme-line-height-relaxed);
  max-height: 120px;
  overflow-y: auto;
}

/* ── 关联对象卡 ── */
.related-card {
  margin: var(--lme-gap-md);
  padding: var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-lg);
  overflow-y: auto;
}

.related-title {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  margin-bottom: var(--lme-gap-sm);
  padding-bottom: var(--lme-gap-xs);
  border-bottom: 1px solid var(--lme-border);
}

.related-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.related-item {
  padding: var(--lme-gap-sm);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.related-item-main {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.related-item-name {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
}

.related-item-cat,
.related-item-kind {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: 0 6px;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-full);
}

.related-item-display {
  margin-top: var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  word-break: break-all;
}

.related-item-link {
  display: inline-block;
  margin-top: var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-accent-hover);
  text-decoration: none;
}

.related-item-link:hover {
  text-decoration: underline;
}

.related-empty {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.related-info {
  margin-top: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  line-height: var(--lme-line-height-normal);
}

/* ── 性能面板 ── */
.perf-panel {
  position: absolute;
  bottom: 40px;
  left: var(--lme-gap-md);
  width: 320px;
  max-height: 280px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border-strong);
  border-radius: var(--lme-radius-lg);
  box-shadow: var(--lme-shadow-xl);
  z-index: var(--lme-z-raised);
  overflow: auto;
}

.perf-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
}

.perf-panel-header button {
  background: none;
  border: none;
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
}

.perf-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--lme-font-size-xs);
}

.perf-table th,
.perf-table td {
  padding: 3px var(--lme-gap-md);
  text-align: left;
  border-bottom: 1px solid var(--lme-border);
}

.perf-table th {
  color: var(--lme-text-muted);
  font-weight: var(--lme-font-weight-medium);
}

.perf-table td {
  color: var(--lme-text-secondary);
  font-family: var(--lme-font-mono);
}

.perf-summary {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  display: flex;
  flex-direction: column;
  gap: 2px;
}
</style>

<!--
  ── 与 WPF 旧界面 AssetsWorkbenchPage 的能力差异说明 ──

  等价能力（已保真；v2 重做只动模板/样式，数据链路未改）：
  - 搜索框 + 250ms 防抖 + 按名称/路径/中文搜索（含 v2 的内嵌清空钮）
  - 类型筛选下拉（与旧界面同选项）
  - 排序下拉（名称/大小/类型/修改在前）
  - 「仅容器内资源」复选框（默认勾选，hasContainerEntry=true）
  - 「显示静态数据表」复选框（默认不显示）
  - 列表/目录树双视图切换（v2 分段控件样式）
  - 服务端分页（200 条/页）+ 页码条（首页/上一页/下一页/末页/跳转）
  - 选中资产 → 预览面板
  - 预览面板：图像/文本/元数据/未知类型（不空白）
  - 虚拟滚动（可视窗口 + 预取 overscan=8，行高 28→30，仅视觉调整）
  - 替换…/批量替换…/撤销编辑（dialog.openFile + replacePayload /
    dialog.folderPick + batchReplace / clearEdits，均带结果反馈与刷新）
  - 关联对象面板（relation.describe，只读 + 维基深链）
  - 按容器路径定位（/assets?container=…，维基「去编辑」深链）
  - 性能实测面板（W1 验证用）

  v2 新增展示态（不发请求、不改数据链路）：
  - 顶部细进度条替代整屏遮罩（首次加载保留居中指示 + 空态插画）
  - 列表空态（含操作建议）
  - 表头（# / 类型 / 名称 / 大小 / 状态）与类型色点、状态胶囊
  - 操作结果按成功/失败分色（原先是同一行灰字）
-->
