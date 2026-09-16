<script setup lang="ts">
// 静态数据工作台
// 对应 WPF 旧界面 StaticWorkbenchPage
// 三栏布局：浏览 | 分隔条 | 编辑

import { ref, computed, onMounted, defineComponent, h } from 'vue'
import { ipc } from '@/ipc'
import VirtualList from '@/components/VirtualList.vue'
import PageBar from '@/components/PageBar.vue'

// ── 类型定义 ──────────────────────────────────────────────

/** 静态数据表元信息 */
interface StaticTableInfo {
  tableName: string
  dataClass: string
  recordCount: number
  bundleSource: string
}

/** 静态数据记录 */
interface StaticRecord {
  key: string
  dataClass: string
  tableName: string
  jsonContent: string
  editState: 'Unchanged' | 'Modified' | 'Added' | 'Deleted'
}

/** 静态数据分页结果 */
interface StaticPageResult {
  records: StaticRecord[]
  totalCount: number
  offset: number
  take: number
}

type StaticSortKind = 'Name' | 'RecordCount' | 'DataClass' | 'ModifiedFirst'

// ── 树视图节点 ────────────────────────────────────────────

interface JsonTreeNodeData {
  /** 属性键名（叶子节点显示用） */
  displayKey: string
  /** 原始值 */
  value: unknown
  /** JSON 类型 */
  type: 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null'
  /** 子节点 */
  children: JsonTreeNodeData[]
  /** 当前是否展开 */
  expanded: boolean
  /** 路径（用于 key） */
  path: string
}

function buildTree(obj: unknown, key: string, path: string): JsonTreeNodeData {
  if (obj === null) {
    return { displayKey: key, value: null, type: 'null', children: [], expanded: false, path }
  }
  if (Array.isArray(obj)) {
    return {
      displayKey: key || '(root)',
      value: obj,
      type: 'array',
      children: obj.map((item, i) => buildTree(item, `[${i}]`, `${path}[${i}]`)),
      expanded: false,
      path,
    }
  }
  if (typeof obj === 'object') {
    const entries = Object.entries(obj as Record<string, unknown>)
    return {
      displayKey: key || '(root)',
      value: obj,
      type: 'object',
      children: entries.map(([k, v]) => buildTree(v, k, `${path}.${k}`)),
      expanded: false,
      path,
    }
  }
  const typeMap: Record<string, JsonTreeNodeData['type']> = {
    string: 'string',
    number: 'number',
    boolean: 'boolean',
  }
  return {
    displayKey: key,
    value: obj,
    type: typeMap[typeof obj] ?? 'null',
    children: [],
    expanded: false,
    path,
  }
}

/** 切换节点展开状态（响应式） */
function toggleNode(node: JsonTreeNodeData) {
  node.expanded = !node.expanded
}

function formatPrimitive(value: unknown): string {
  if (typeof value === 'string') return `"${value}"`
  return String(value)
}

function nodeTypeColor(type: JsonTreeNodeData['type']): string {
  const map: Record<string, string> = {
    object: 'var(--lme-type-json)',
    array: 'var(--lme-type-json)',
    string: 'var(--lme-text-text)',
    number: 'var(--lme-type-mono)',
    boolean: 'var(--lme-accent)',
    null: 'var(--lme-text-muted)',
  }
  return map[type] ?? 'var(--lme-text-secondary)'
}

// ── 递归 JSON 树组件（inline defineComponent，自引用） ─────

const JsonTreeNode = defineComponent({
  name: 'JsonTreeNode',
  props: {
    node: { type: Object as () => JsonTreeNodeData, required: true },
    depth: { type: Number, default: 0 },
  },
  setup(props) {
    return () => {
      const { node, depth } = props
      const indent = depth * 18 + 8

      if (node.type === 'object' || node.type === 'array') {
        const label = node.type === 'object' ? '{ }' : '[ ]'
        const childCount = node.children.length
        return h('div', { class: 'json-tree-node' }, [
          h(
            'div',
            {
              class: 'json-tree-row',
              style: { paddingLeft: indent + 'px' },
              onClick: () => toggleNode(node),
            },
            [
              h('span', { class: 'json-tree-arrow' }, node.expanded ? '▼' : '▶'),
              h('span', { class: 'json-tree-key' }, node.displayKey),
              h('span', { class: 'json-tree-label' }, `${label} ${childCount} 项`),
            ],
          ),
          ...(node.expanded
            ? node.children.map((child) =>
                h(JsonTreeNode, { node: child, depth: depth + 1, key: child.path }),
              )
            : []),
        ])
      }

      // 叶子节点
      return h('div', { class: 'json-tree-node' }, [
        h(
          'div',
          { class: 'json-tree-row leaf', style: { paddingLeft: indent + 'px' } },
          [
            h('span', { class: 'json-tree-arrow' }, '•'),
            h('span', { class: 'json-tree-key' }, node.displayKey),
            h(
              'span',
              { class: 'json-tree-value', style: { color: nodeTypeColor(node.type) } },
              formatPrimitive(node.value),
            ),
          ],
        ),
      ])
    }
  },
})

// ── 状态 ──────────────────────────────────────────────────

const searchText = ref('')
const dataClassFilter = ref<string>('')
const sortKind = ref<StaticSortKind>('Name')
const loading = ref(false)
const error = ref<string | null>(null)
const lastQueryMs = ref(0)

// 表列表
const allTables = ref<StaticTableInfo[]>([])
const expandedClasses = ref<Set<string>>(new Set())

// 选中表
const selectedTable = ref<StaticTableInfo | null>(null)

// 记录分页
const records = ref<StaticRecord[]>([])
const totalRecordCount = ref(0)
const recordOffset = ref(0)
const pageSize = ref(200)
const generation = ref(0)

// JSON 编辑器
const editorMode = ref<'tree' | 'raw'>('tree')
const rawJsonText = ref('')
const parseError = ref<string | null>(null)
const rootNode = ref<JsonTreeNodeData | null>(null)

// Diff 视图
const showDiff = ref(false)
const diffContent = ref('')

// 防抖计时器
let debounceTimer: ReturnType<typeof setTimeout> | null = null

// ── 计算属性 ──────────────────────────────────────────────

const dataClasses = computed<string[]>(() => {
  const set = new Set<string>()
  for (const t of allTables.value) {
    set.add(t.dataClass)
  }
  return Array.from(set).sort()
})

const filteredTables = computed<StaticTableInfo[]>(() => {
  let list = allTables.value

  // 按 dataClass 筛选
  if (dataClassFilter.value) {
    list = list.filter((t) => t.dataClass === dataClassFilter.value)
  }

  // 按搜索文本筛选（表名或数据类名）
  if (searchText.value.trim()) {
    const q = searchText.value.trim().toLowerCase()
    list = list.filter(
      (t) =>
        t.tableName.toLowerCase().includes(q) ||
        t.dataClass.toLowerCase().includes(q),
    )
  }

  // 排序
  switch (sortKind.value) {
    case 'Name':
      list = [...list].sort((a, b) => a.tableName.localeCompare(b.tableName))
      break
    case 'RecordCount':
      list = [...list].sort((a, b) => b.recordCount - a.recordCount)
      break
    case 'DataClass':
      list = [...list].sort((a, b) => a.dataClass.localeCompare(b.dataClass))
      break
    case 'ModifiedFirst':
      // 暂未实现修改状态排序
      break
  }

  return list
})

const groupedTables = computed<{ dataClass: string; tables: StaticTableInfo[] }[]>(() => {
  const map = new Map<string, StaticTableInfo[]>()
  for (const t of filteredTables.value) {
    const arr = map.get(t.dataClass) ?? []
    arr.push(t)
    map.set(t.dataClass, arr)
  }
  return Array.from(map.entries())
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([dataClass, tables]) => ({ dataClass, tables }))
})

const pageCount = computed(() =>
  Math.max(1, Math.ceil(totalRecordCount.value / pageSize.value)),
)
const currentPage = computed(() =>
  Math.floor(recordOffset.value / pageSize.value) + 1,
)

// ── 搜索处理 ──────────────────────────────────────────────

function onSearchInput() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => {
    performSearch()
  }, 250)
}

function onFilterChange() {
  performSearch()
}

async function performSearch() {
  loading.value = true
  error.value = null
  const t0 = performance.now()

  try {
    const result = await ipc.request<StaticTableInfo[]>('static.listTables', {
      text: searchText.value || undefined,
      dataClass: dataClassFilter.value || undefined,
      sort: sortKind.value,
    })
    allTables.value = result
  } catch {
    // 后端暂未实现：显示空列表（保留搜索体验）
    allTables.value = []
    error.value = '暂未实现：静态数据表列表接口尚未就绪'
  } finally {
    lastQueryMs.value = performance.now() - t0
    loading.value = false
  }
}

// ── 表选择 ────────────────────────────────────────────────

async function onSelectTable(table: StaticTableInfo) {
  selectedTable.value = table
  recordOffset.value = 0
  selectedRecord.value = null
  rootNode.value = null
  rawJsonText.value = ''
  parseError.value = null
  showDiff.value = false
  await fetchRecords(table, 0)
}

async function fetchRecords(table: StaticTableInfo, offset: number) {
  const gen = ++generation.value
  loading.value = true
  error.value = null
  const t0 = performance.now()

  try {
    const result = await ipc.request<StaticPageResult>('static.queryRecords', {
      tableName: table.tableName,
      offset,
      take: pageSize.value,
    })

    if (gen !== generation.value) return

    records.value = result.records
    totalRecordCount.value = result.totalCount
    recordOffset.value = result.offset
    lastQueryMs.value = performance.now() - t0
  } catch {
    if (gen !== generation.value) return
    records.value = []
    totalRecordCount.value = 0
    error.value = '暂未实现：静态数据接口尚未就绪'
  } finally {
    if (gen === generation.value) {
      loading.value = false
    }
  }
}

// ── 分页 ──────────────────────────────────────────────────

function onGoToPage(pageIndex: number) {
  if (!selectedTable.value) return
  const clamped = Math.max(0, Math.min(pageIndex, pageCount.value - 1))
  recordOffset.value = clamped * pageSize.value
  fetchRecords(selectedTable.value, recordOffset.value)
}

// ── 记录选择 ──────────────────────────────────────────────

const selectedRecord = ref<StaticRecord | null>(null)

function onSelectRecord(record: StaticRecord) {
  selectedRecord.value = record
  rawJsonText.value = record.jsonContent
  parseError.value = null
  rootNode.value = null
  showDiff.value = false

  try {
    const parsed = JSON.parse(record.jsonContent)
    rootNode.value = buildTree(parsed, '(root)', '$')
  } catch (e) {
    parseError.value = e instanceof Error ? e.message : String(e)
  }
}

// ── JSON 编辑器 ───────────────────────────────────────────

function onRawJsonInput() {
  parseError.value = null
  try {
    const parsed = JSON.parse(rawJsonText.value)
    rootNode.value = buildTree(parsed, '(root)', '$')
  } catch (e) {
    parseError.value = e instanceof Error ? e.message : String(e)
    rootNode.value = null
  }
}

// ── Diff 视图 ─────────────────────────────────────────────

function onToggleDiff() {
  if (!selectedRecord.value) return
  showDiff.value = !showDiff.value
  if (showDiff.value) {
    diffContent.value = '暂未实现：差异对比功能开发中'
  }
}

// ── 导出 ──────────────────────────────────────────────────

function onExport() {
  if (!selectedTable.value) return
  ipc
    .request<{ success: boolean; path: string }>('static.export', {
      tableName: selectedTable.value.tableName,
      format: 'staticmod',
    })
    .then((result) => {
      alert(`已导出至: ${result.path}`)
    })
    .catch(() => {
      alert('暂未实现：导出功能开发中')
    })
}

// ── 分组展开 ──────────────────────────────────────────────

function toggleClassGroup(dataClass: string) {
  if (expandedClasses.value.has(dataClass)) {
    expandedClasses.value.delete(dataClass)
  } else {
    expandedClasses.value.add(dataClass)
  }
}

// ── 初始加载 ──────────────────────────────────────────────

onMounted(() => {
  performSearch()
})

// ── 排序选项 ──────────────────────────────────────────────

const sortOptions: { value: StaticSortKind; label: string }[] = [
  { value: 'Name', label: '按表名' },
  { value: 'RecordCount', label: '按记录数' },
  { value: 'DataClass', label: '按数据类' },
  { value: 'ModifiedFirst', label: '修改在前' },
]
</script>

<template>
  <div class="static-view">
    <!-- 左栏：浏览 -->
    <div class="browser-column">
      <!-- 搜索框 -->
      <div class="search-section">
        <input
          v-model="searchText"
          class="search-input"
          type="text"
          placeholder="搜索表名或数据类（支持中文）"
          @input="onSearchInput"
        />
      </div>

      <!-- 筛选行 -->
      <div class="filter-row">
        <label class="filter-label">数据类</label>
        <select v-model="dataClassFilter" class="filter-select" @change="onFilterChange">
          <option value="">全部</option>
          <option v-for="cls in dataClasses" :key="cls" :value="cls">
            {{ cls }}
          </option>
        </select>

        <label class="filter-label">排序</label>
        <select v-model="sortKind" class="filter-select" @change="onFilterChange">
          <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
            {{ opt.label }}
          </option>
        </select>
      </div>

      <!-- 表列表（按 dataClass 分组，懒展开） -->
      <div class="table-list-container">
        <VirtualList
          :items="groupedTables"
          :item-height="32"
          :overscan="6"
        >
          <template #default="{ item }">
            <!-- 分组头 -->
            <div
              class="class-group-header"
              @click="toggleClassGroup(item.dataClass)"
            >
              <span class="group-arrow">
                {{ expandedClasses.has(item.dataClass) ? '▼' : '▶' }}
              </span>
              <span class="group-name">{{ item.dataClass }}</span>
              <span class="group-count">{{ item.tables.length }} 表</span>
            </div>

            <!-- 展开的表列表 -->
            <div v-if="expandedClasses.has(item.dataClass)" class="class-group-tables">
              <div
                v-for="table in item.tables"
                :key="table.tableName"
                class="table-row"
                :class="{ selected: selectedTable?.tableName === table.tableName }"
                @click="onSelectTable(table)"
              >
                <span class="table-name lme-ellipsis" :title="table.tableName">
                  {{ table.tableName }}
                </span>
                <span class="table-record-count lme-mono">
                  {{ table.recordCount.toLocaleString('zh-CN') }}
                </span>
                <span class="table-bundle lme-ellipsis" :title="table.bundleSource">
                  {{ table.bundleSource }}
                </span>
              </div>
            </div>
          </template>
        </VirtualList>
      </div>

      <!-- 加载态 -->
      <div v-if="loading" class="loading-overlay">
        <span class="loading-spinner">⏳</span>
        <span>查询中…</span>
      </div>

      <!-- 错误态 -->
      <div v-if="error" class="error-banner">
        ⚠️ {{ error }}
      </div>
    </div>

    <!-- 分隔条 -->
    <div class="column-splitter" />

    <!-- 右栏：编辑 -->
    <div class="edit-column">
      <!-- 已选中表头 -->
      <div v-if="selectedTable" class="edit-header">
        <div class="header-info">
          <span class="header-table-name">{{ selectedTable.tableName }}</span>
          <span class="header-meta lme-mono">
            {{ selectedTable.recordCount.toLocaleString('zh-CN') }} 条记录
          </span>
          <span class="header-meta lme-mono">
            来源: {{ selectedTable.bundleSource }}
          </span>
        </div>
        <button class="export-btn" @click="onExport">📦 导出 .staticmod</button>
      </div>

      <!-- 记录浏览器 + 编辑器 -->
      <div v-if="selectedTable" class="edit-body">
        <!-- 记录列表 -->
        <div class="record-browser">
          <div class="record-browser-header">
            <span>记录列表</span>
            <span class="record-count-badge lme-mono">
              {{ totalRecordCount.toLocaleString('zh-CN') }}
            </span>
          </div>
          <VirtualList
            :items="records"
            :item-height="28"
            :overscan="8"
          >
            <template #default="{ item }">
              <div
                class="record-row"
                :class="{ selected: selectedRecord?.key === item.key }"
                @click="onSelectRecord(item)"
              >
                <span class="record-key lme-ellipsis" :title="item.key">
                  {{ item.key }}
                </span>
                <span
                  class="record-state"
                  :class="'state-' + item.editState"
                >
                  {{ item.editState === 'Unchanged' ? '未修改' : item.editState === 'Modified' ? '已修改' : item.editState === 'Added' ? '已添加' : '已删除' }}
                </span>
              </div>
            </template>
          </VirtualList>
          <PageBar
            :current-page="currentPage"
            :page-count="pageCount"
            :total-count="totalRecordCount"
            :query-ms="lastQueryMs"
            :loading="loading"
            @go-to="onGoToPage"
          />
        </div>

        <!-- JSON 编辑器 -->
        <div class="json-editor-panel">
          <div class="editor-tabs">
            <button
              class="tab-btn"
              :class="{ active: editorMode === 'tree' && !showDiff }"
              @click="editorMode = 'tree'; showDiff = false"
            >
              🌳 树视图
            </button>
            <button
              class="tab-btn"
              :class="{ active: editorMode === 'raw' && !showDiff }"
              @click="editorMode = 'raw'; showDiff = false"
            >
              {} 原始文本
            </button>
            <button
              class="tab-btn diff-btn"
              :class="{ active: showDiff }"
              @click="onToggleDiff"
            >
              ⚖️ 差异对比
            </button>
          </div>

          <!-- 树视图 -->
          <div v-if="editorMode === 'tree' && !showDiff" class="tree-view">
            <div v-if="parseError" class="parse-error">
              ⚠️ JSON 解析错误: {{ parseError }}
            </div>
            <div v-else-if="rootNode" class="tree-content">
              <JsonTreeNode :node="rootNode" :depth="0" />
            </div>
            <div v-else class="empty-editor">选择记录以查看 JSON 内容</div>
          </div>

          <!-- 原始文本 -->
          <div v-else-if="editorMode === 'raw' && !showDiff" class="raw-view">
            <textarea
              v-model="rawJsonText"
              class="raw-textarea"
              spellcheck="false"
              @input="onRawJsonInput"
            />
            <div v-if="parseError" class="parse-error">
              ⚠️ {{ parseError }}
            </div>
          </div>

          <!-- Diff 视图 -->
          <div v-else-if="showDiff" class="diff-view">
            <pre class="diff-content">{{ diffContent }}</pre>
          </div>
        </div>
      </div>

      <!-- 空状态 -->
      <div v-else class="empty-state">
        <div class="empty-icon">📊</div>
        <div class="empty-title">静态数据工作台</div>
        <div class="empty-desc">
          从左侧选择一个静态数据表以开始编辑
        </div>
        <div class="empty-hint">
          支持浏览、搜索、编辑和导出静态数据表
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.static-view {
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

.search-section {
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.search-input {
  width: 100%;
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

.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  flex-wrap: wrap;
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

.table-list-container {
  flex: 1;
  overflow: hidden;
  min-height: 0;
}

/* ── 分组头 ── */
.class-group-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: 4px var(--lme-gap-md);
  cursor: pointer;
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-primary);
  user-select: none;
}

.class-group-header:hover {
  background: var(--lme-bg-hover);
}

.group-arrow {
  width: 14px;
  text-align: center;
  font-size: 10px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.group-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.group-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  background: var(--lme-bg-panel);
  padding: 1px 6px;
  border-radius: 8px;
  font-weight: 400;
}

/* ── 表行 ── */
.class-group-tables {
  display: flex;
  flex-direction: column;
}

.table-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 4px var(--lme-gap-md) 4px 28px;
  cursor: pointer;
  border-left: 3px solid transparent;
  transition: background 0.1s;
}

.table-row:hover {
  background: var(--lme-bg-hover);
}

.table-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.table-name {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.table-record-count {
  width: 64px;
  text-align: right;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.table-bundle {
  width: 100px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  text-align: right;
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
}

/* ── 编辑列 ── */
.edit-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--lme-bg-panel);
  min-width: 320px;
}

.edit-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  gap: var(--lme-gap-md);
  flex-shrink: 0;
}

.header-info {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  min-width: 0;
  flex: 1;
}

.header-table-name {
  font-size: var(--lme-font-size-lg);
  font-weight: 600;
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.header-meta {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}

.export-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-accent);
  border: none;
  border-radius: var(--lme-radius-sm);
  color: #fff;
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  font-weight: 500;
  white-space: nowrap;
  flex-shrink: 0;
  transition: background 0.15s;
}

.export-btn:hover {
  background: var(--lme-accent-hover);
}

/* ── 编辑主体 ── */
.edit-body {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
}

/* ── 记录浏览器 ── */
.record-browser {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid var(--lme-border);
  max-height: 240px;
  min-height: 120px;
}

.record-browser-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
  border-bottom: 1px solid var(--lme-border);
  flex-shrink: 0;
}

.record-count-badge {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  background: var(--lme-bg-elevated);
  padding: 1px 6px;
  border-radius: 8px;
}

.record-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 3px solid transparent;
  transition: background 0.1s;
}

.record-row:hover {
  background: var(--lme-bg-hover);
}

.record-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.record-key {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.record-state {
  font-size: var(--lme-font-size-xs);
  padding: 1px 6px;
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

/* ── JSON 编辑器面板 ── */
.json-editor-panel {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-height: 0;
}

.editor-tabs {
  display: flex;
  align-items: center;
  gap: 0;
  border-bottom: 1px solid var(--lme-border);
  flex-shrink: 0;
}

.tab-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: transparent;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: color 0.15s, border-color 0.15s;
}

.tab-btn:hover {
  color: var(--lme-text-secondary);
}

.tab-btn.active {
  color: var(--lme-text-primary);
  border-bottom-color: var(--lme-accent);
}

.diff-btn {
  margin-left: auto;
}

/* ── 树视图 ── */
.tree-view {
  flex: 1;
  overflow: auto;
  padding: var(--lme-gap-sm) 0;
}

.tree-content {
  padding: 0 var(--lme-gap-sm);
}

/* ── JSON 树节点 ── */
.json-tree-node {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
}

.json-tree-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: 2px var(--lme-gap-sm);
  cursor: pointer;
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  user-select: none;
}

.json-tree-row:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.json-tree-row.leaf {
  cursor: default;
  color: var(--lme-text-muted);
}

.json-tree-row.leaf:hover {
  background: transparent;
  color: var(--lme-text-muted);
}

.json-tree-arrow {
  width: 14px;
  text-align: center;
  font-size: 10px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.json-tree-key {
  color: var(--lme-text-primary);
  flex-shrink: 0;
}

.json-tree-label {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}

.json-tree-value {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--lme-font-size-sm);
}

/* ── 原始文本 ── */
.raw-view {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: var(--lme-gap-sm);
}

.raw-textarea {
  flex: 1;
  width: 100%;
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  line-height: 1.5;
  resize: none;
  padding: var(--lme-gap-md);
  tab-size: 2;
}

.raw-textarea:focus {
  outline: none;
  border-color: var(--lme-accent);
}

/* ── Diff 视图 ── */
.diff-view {
  flex: 1;
  overflow: auto;
  padding: var(--lme-gap-md);
}

.diff-content {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  white-space: pre-wrap;
  margin: 0;
}

/* ── 解析错误 ── */
.parse-error {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-mono);
  flex-shrink: 0;
}

/* ── 空编辑器 ── */
.empty-editor {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

/* ── 空状态 ── */
.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-md);
  color: var(--lme-text-muted);
}

.empty-icon {
  font-size: 48px;
  opacity: 0.5;
}

.empty-title {
  font-size: var(--lme-font-size-xl);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.empty-desc {
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-muted);
}

.empty-hint {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-disabled);
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
  font-size: 18px;
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
