<script setup lang="ts">
// 资源工作台竖切片（W1 前端一半）
// 对应 WPF 旧界面 AssetsWorkbenchPage
// 差异说明见文件底部注释

import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { useUiStateStore } from '@/stores/uiState'
import { ipc } from '@/ipc'
import type { AssetSearchQuery, RelationSubject } from '@/ipc'
import SearchFilters from '@/components/SearchFilters.vue'
import VirtualList from '@/components/VirtualList.vue'
import PreviewPane from '@/components/PreviewPane.vue'
import PageBar from '@/components/PageBar.vue'
import ContainerTree from '@/components/ContainerTree.vue'

const catalog = useCatalogStore()
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

watch(
  () => catalog.selectedAssetId,
  async (assetId) => {
    relatedSubjects.value = []
    if (!assetId) return
    try {
      const res = await ipc.request<{ subjects?: RelationSubject[] }>('relation.describe', {
        assetId,
      })
      relatedSubjects.value = res?.subjects ?? []
    } catch {
      // 调用失败就什么都不显示（不抛、不重试）
      relatedSubjects.value = []
    }
  },
)

// 虚拟列表高度自适应
const listHeight = ref(600)
</script>

<template>
  <div class="assets-view">
    <!-- 中栏：搜索 + 筛选 + 浏览 -->
    <div class="browser-column">
      <!-- 搜索筛选栏 -->
      <SearchFilters
        :model-value="catalog.query"
        @search="onSearch"
      />

      <!-- 视图切换 -->
      <div class="view-toggle-row">
        <button
          class="view-toggle-btn"
          :class="{ active: viewMode === 'list' }"
          @click="viewMode = 'list'"
        >
          ☰ 列表
        </button>
        <button
          class="view-toggle-btn"
          :class="{ active: viewMode === 'tree' }"
          @click="viewMode = 'tree'"
        >
          🗂 目录树
        </button>

        <span class="result-count" v-if="!catalog.loading">
          共 {{ catalog.totalCount.toLocaleString('zh-CN') }} 条命中
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
      <div v-if="viewMode === 'list'" class="list-container">
        <VirtualList
          :items="catalog.items"
          :item-height="28"
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
              <span
                class="row-type"
                :style="{ color: typeColor(item.type) }"
              >
                {{ item.type }}
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

      <!-- 加载态 -->
      <div v-if="catalog.loading" class="loading-overlay">
        <span class="loading-spinner">⏳</span>
        <span>查询中…</span>
      </div>

      <!-- 错误态 -->
      <div v-if="catalog.error" class="error-banner">
        ⚠️ {{ catalog.error }}
      </div>
    </div>

    <!-- 分隔条 -->
    <div class="column-splitter" />

    <!-- 预览列 -->
    <div class="preview-column" :style="{ width: uiState.previewColumnWidth + 'px' }">
      <PreviewPane :asset="catalog.selectedAsset" />

      <!-- 关联对象：只读列表，数据来自 relation.describe -->
      <div v-if="catalog.selectedAsset" class="related-subjects">
        <div class="related-title">关联对象</div>
        <ul class="related-list">
          <li v-for="s in relatedSubjects" :key="s.subjectId">
            {{ s.displayName }}（{{ s.categoryLabel }}） · {{ s.kindLabel }} · {{ s.display }}
          </li>
          <li v-if="relatedSubjects.length === 0">未找到关联对象</li>
        </ul>
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
}

.browser-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
}

.view-toggle-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.view-toggle-btn {
  padding: 3px 12px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.view-toggle-btn.active {
  background: var(--lme-accent-muted);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  margin-left: auto;
}

.perf-toggle {
  padding: 2px 8px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
}

.perf-toggle.active {
  border-color: var(--lme-warning);
  color: var(--lme-warning);
}

.list-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
}

/* ── 资产行 ── */
.asset-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 3px solid transparent;
  transition: background 0.1s;
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
  width: 90px;
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
  font-weight: 500;
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
  width: 56px;
  font-size: var(--lme-font-size-xs);
  text-align: center;
  padding: 1px 4px;
  border-radius: var(--lme-radius-sm);
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

/* ── 分隔条 ── */
.column-splitter {
  width: 6px;
  flex-shrink: 0;
  background: var(--lme-border);
  cursor: col-resize;
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

/* ── 关联对象 ── */
.related-subjects {
  padding: 8px 12px;
  border-top: 1px solid var(--lme-border);
  overflow-y: auto;
}

.related-title {
  font-size: 12px;
  font-weight: 600;
  margin-bottom: 4px;
}

.related-list {
  margin: 0;
  padding-left: 18px;
  font-size: 12px;
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
}

.error-banner {
  padding: var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
}

/* ── 性能面板 ── */
.perf-panel {
  position: absolute;
  bottom: 40px;
  left: var(--lme-gap-md);
  width: 320px;
  max-height: 280px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  box-shadow: var(--lme-shadow-lg);
  z-index: 20;
  overflow: auto;
}

.perf-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
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
  font-weight: 500;
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

  等价能力（已保真）：
  - 搜索框 + 250ms 防抖 + 按名称/路径/中文搜索
  - 类型筛选下拉（与旧界面同选项）
  - 排序下拉（名称/大小/类型/修改在前）
  - 「仅容器内资源」复选框（默认勾选，hasContainerEntry=true）
  - 「显示静态数据表」复选框（默认不显示）
  - 列表/目录树双视图切换
  - 服务端分页（200 条/页）+ 页码条（首页/上一页/下一页/末页/跳转）
  - 选中资产 → 预览面板
  - 预览面板：图像/文本/元数据/未知类型（不空白）
  - 虚拟滚动（可视窗口 + 预取 overscan=8）

  差异（有意为之的改进）：
  - ❌ 旧界面：DataGrid 虚拟化（回收行，但行高固定、不支持可变行高）
  - ✅ 新界面：自研 VirtualList（通用、可控 overscan、预取窗口）
  - ❌ 旧界面：关联资源区（反查「这个资源属于哪些对象」）
  - ⏳ 新界面：W2 全量迁移时补上（需 relation.describe IPC 方法）
  - ❌ 旧界面：替换/批处理/撤销/编辑文本/Unity 字段编辑
  - ⏳ 新界面：W2 全量迁移时补上（需 asset.edit.* IPC 方法）
  - ❌ 旧界面：Spine 动画预览按钮
  - ⏳ 新接口：W2 全量迁移时改为前端 spine-ts 就地播放
  - ❌ 旧界面：右键菜单（替换/编辑文本/字段编辑/撤销/复制路径）
  - ⏳ 新界面：W2 补上
  - ✅ 新增：性能实测面板（W1 验证用，W2 可保留或移除）
  - ✅ 新增：列宽钳制 260-2000（通过 uiState）

  备注：本竖切片聚焦「浏览/预览」闭环，编辑操作留 W2。
-->
