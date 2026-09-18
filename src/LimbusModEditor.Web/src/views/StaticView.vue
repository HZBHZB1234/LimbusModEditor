<script setup lang="ts">
// 静态数据工作台
// 对应 WPF 旧界面 StaticWorkbenchPage
// 三栏布局：浏览 | 分隔条 | 编辑
// v3（ui-redesign r3）：手写版式替换为 Naive UI 组件（面板/按钮/输入/下拉/标签/空态/提示），功能不变

import { ref, computed, onMounted, defineComponent, h, type DefineComponent } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NEmpty,
  NInput,
  NSelect,
  NSpin,
  NTab,
  NTabs,
  NTag,
  NTooltip,
  useDialog,
  useMessage,
} from 'naive-ui'

const route = useRoute()
const router = useRouter()
import VirtualList from '@/components/VirtualList.vue'
import PageBar from '@/components/PageBar.vue'

// 呈现层提示（App.vue 已提供 NMessageProvider / NDialogProvider），不参与 IPC 时序
const message = useMessage()
const dialog = useDialog()

// ── 类型定义 ──────────────────────────────────────────────

/** 静态数据表元信息 */
interface StaticTableInfo {
  /** 后端表 id（static.tableList 的 tableId = 容器路径，查记录时用它） */
  tableId: string
  tableName: string
  /** static.tableList 不提供分类，归到"未分类" */
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

/** 记录编辑状态 → NTag 的 type + 中文标签 */
const recordStateTag: Record<
  StaticRecord['editState'],
  { type: 'default' | 'warning' | 'success' | 'error'; label: string }
> = {
  Unchanged: { type: 'default', label: '未修改' },
  Modified: { type: 'warning', label: '已修改' },
  Added: { type: 'success', label: '已添加' },
  Deleted: { type: 'error', label: '已删除' },
}

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
    return (): any => {
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

/** 数据类下拉选项（NSelect）：首项「全部」= 空值 */
const dataClassOptions = computed(() => [
  { label: '全部', value: '' },
  ...dataClasses.value.map((cls) => ({ label: cls, value: cls })),
])

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

/** 编辑器三态 ↔ NTabs 的 value（差异对比是独立开关，故单独映射） */
const editorTab = computed<'tree' | 'raw' | 'diff'>(() =>
  showDiff.value ? 'diff' : editorMode.value,
)

function onEditorTabChange(value: string | number) {
  // 与旧版「差异对比」按钮一致：再点一次收起，回到之前的编辑态
  if (value === 'diff') {
    onToggleDiff()
    return
  }
  showDiff.value = false
  editorMode.value = value === 'raw' ? 'raw' : 'tree'
}

// ── 搜索处理 ──────────────────────────────────────────────

// NInput 的 clearable 会先触发 clear 再触发 update:value；旧版清空只清文本、不重查，这里保持一致
let skipSearchOnce = false

function onSearchClear() {
  skipSearchOnce = true
}

function onSearchInput() {
  if (skipSearchOnce) {
    skipSearchOnce = false
    return
  }
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
    // 契约方法 static.tableList：载荷 { offset, take }（后端无过滤参数，筛选在前端页内做）
    const result = await ipc.request<{
      items: { tableId: string; name: string; recordCount: number }[]
      totalCount: number
    }>('static.tableList', { offset: 0, take: 200 })

    // static.tableList 不提供 dataClass / bundleSource，不编造
    allTables.value = result.items.map((i) => ({
      tableId: i.tableId,
      tableName: i.name,
      dataClass: '未分类',
      recordCount: i.recordCount,
      bundleSource: '',
    }))
    // 首屏默认展开分组（否则只看到一个折叠头、看不到任何表）；仍可手动折叠
    for (const t of allTables.value) expandedClasses.value.add(t.dataClass)
  } catch {
    allTables.value = []
    error.value = '静态数据表列表加载失败'
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
    // 契约方法 static.records：载荷 { tableId, offset, take }，响应 { tableId, items, totalCount, offset, take }
    const result = await ipc.request<{
      items: { recordId: string; summary: string; rawJson?: string }[]
      totalCount: number
      offset: number
      take: number
    }>('static.records', {
      tableId: table.tableId,
      offset,
      take: pageSize.value,
    })

    if (gen !== generation.value) return

    records.value = result.items.map((i) => ({
      key: i.recordId,
      dataClass: table.dataClass,
      tableName: table.tableName,
      jsonContent: i.rawJson ?? '',
      editState: 'Unchanged' as const,
    }))
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

// ── 保存记录（static.editRecord + static.readRecord 读回）──────────

const saving = ref(false)

/**
 * 保存当前记录的改动。
 *
 * 编辑形态选「整条记录的 JSON 文本」而不是逐字段：后端 `static.editRecord` 的语义是
 * **用请求里的 json 整条替换该行**（`ReplaceRow`），按字段改最终也要拼回整行 JSON，
 * 中间多一层「字段 ↔ 行」的转换就多一处失真（类型、嵌套、键顺序）。
 *
 * 保存后立即用 `static.readRecord` 读回覆盖预览：后端读的是「编辑集优先」的当前值，
 * 所以读到的就是改后内容（不是官方原文）。
 */
async function onSaveRecord() {
  const table = selectedTable.value
  const record = selectedRecord.value
  if (!table || !record || saving.value) return

  // 前端先判一次合法性：后端也会判（给中文错误），这里挡掉省一次往返。
  try {
    JSON.parse(rawJsonText.value)
  } catch (e) {
    parseError.value = e instanceof Error ? e.message : String(e)
    return
  }
  parseError.value = null

  saving.value = true
  try {
    await ipc.request('static.editRecord', {
      tableId: table.tableId,
      recordId: record.key,
      json: rawJsonText.value,
    })

    const reread = await ipc.request<{ json?: string }>('static.readRecord', {
      tableId: table.tableId,
      recordId: record.key,
    })
    const fresh = reread?.json ?? ''
    record.jsonContent = fresh
    record.editState = 'Modified'
    rawJsonText.value = fresh
    try {
      rootNode.value = buildTree(JSON.parse(fresh), '(root)', '$')
    } catch {
      rootNode.value = null // 读回的不是合法 JSON：树视图留空，原始文本里照实显示
    }
    message.success('已保存（该改动会随导出写进 .staticmod）')
  } catch (e: unknown) {
    message.error(`保存失败：${e instanceof Error ? e.message : String(e)}`)
  } finally {
    saving.value = false
  }
}

/** 保存会整条替换该行，先确认一次 */
function requestSaveRecord() {
  const record = selectedRecord.value
  if (!record) return
  dialog.warning({
    title: '保存修改',
    content: `将用编辑器里的 JSON 整条替换记录 ${record.key}。`,
    positiveText: '确认保存',
    negativeText: '取消',
    onPositiveClick: () => {
      void onSaveRecord()
    },
  })
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

async function onExport() {
  // 契约方法 static.exportStaticmod：载荷 { targetDirectory }（导出当前静态编辑集，不按单表）
  try {
    const picked = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: '选择 .staticmod 导出目录',
    })
    if (!picked.path) return

    const result = await ipc.request<{ ok: boolean; outputPath: string; written: number }>(
      'static.exportStaticmod',
      { targetDirectory: picked.path },
    )
    message.success(`已导出 ${result.written} 条补丁至: ${result.outputPath}`)
  } catch {
    message.error('导出失败：请确认已打开项目并有静态数据改动')
  }
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
  // 维基「去编辑」深链：/static?table=<表名> → 填进搜索框（页内筛选按表名/数据类名匹配）
  const raw = route.query.table
  if (typeof raw === 'string' && raw.trim() !== '') {
    searchText.value = raw.trim()
    performSearch()
    const next = { ...route.query }
    delete next.table
    void router.replace({ path: route.path, query: next })
    return
  }
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
    <!-- 加载进度条（顶部细条，替代整屏遮罩） -->
    <div v-if="loading" class="loading-bar" aria-hidden="true" />

    <!-- 页头工具栏 -->
    <header class="page-toolbar">
      <div class="toolbar-leading">
        <span class="toolbar-icon">📊</span>
        <div class="toolbar-titles">
          <h1 class="toolbar-name">静态数据</h1>
          <span class="toolbar-sub" v-if="selectedTable">
            <span class="toolbar-table lme-mono">{{ selectedTable.tableName }}</span>
            <span class="toolbar-dot">·</span>
            {{ selectedTable.recordCount.toLocaleString('zh-CN') }} 条记录
          </span>
          <span class="toolbar-sub" v-else>从左侧选择一张表开始浏览与编辑</span>
        </div>
      </div>

      <div class="toolbar-actions">
        <NTag v-if="!loading" class="result-count" size="small" :bordered="false">
          共 <strong>{{ filteredTables.length.toLocaleString('zh-CN') }}</strong> 张表
        </NTag>
        <NTooltip placement="bottom" :show-arrow="false">
          <template #trigger>
            <NButton size="small" tertiary @click="onExport">📦 导出 .staticmod</NButton>
          </template>
          导出当前静态编辑集为 .staticmod（不按单表）
        </NTooltip>
      </div>
    </header>

    <!-- 错误态 -->
    <NAlert v-if="error" class="error-banner" type="error" :closable="false">
      {{ error }}
    </NAlert>

    <!-- 三栏主从：表 / 记录 / 编辑器 -->
    <div class="workbench-body">
      <!-- ── 左：表列表 ── -->
      <NCard class="panel panel-tables" size="small" bordered :content-style="{ padding: '0px' }">
        <template #header>
          <div class="panel-head">
            <div class="panel-search">
              <NInput
                v-model:value="searchText"
                class="search-input"
                size="small"
                clearable
                placeholder="搜索表名或数据类（支持中文）"
                @update:value="onSearchInput"
                @clear="onSearchClear"
              >
                <template #prefix>
                  <span class="panel-search-icon">🔍</span>
                </template>
              </NInput>
            </div>

            <div class="filter-row">
              <span class="filter-label">数据类</span>
              <NSelect
                v-model:value="dataClassFilter"
                class="filter-select"
                size="small"
                placeholder="全部"
                :options="dataClassOptions"
                @update:value="onFilterChange"
              />

              <span class="filter-label">排序</span>
              <NSelect
                v-model:value="sortKind"
                class="filter-select"
                size="small"
                :options="sortOptions"
                @update:value="onFilterChange"
              />
            </div>
          </div>
        </template>

        <div class="panel-body">
          <!-- 表最多取 200 张，直接渲染；分组头 + 表行（虚拟列表按固定行高切，展开后会溢出，故不套 VirtualList） -->
          <div
            v-for="group in groupedTables"
            :key="group.dataClass"
            class="class-group"
          >
            <div class="class-group-header" @click="toggleClassGroup(group.dataClass)">
              <span class="group-arrow">
                {{ expandedClasses.has(group.dataClass) ? '▼' : '▶' }}
              </span>
              <span class="group-name">{{ group.dataClass }}</span>
              <NTag class="group-count" size="small" round :bordered="false">
                {{ group.tables.length }} 表
              </NTag>
            </div>

            <div v-if="expandedClasses.has(group.dataClass)" class="class-group-tables">
              <div
                v-for="table in group.tables"
                :key="table.tableName"
                class="table-row"
                :class="{ selected: selectedTable?.tableName === table.tableName }"
                :title="table.bundleSource ? `${table.tableName}（来源：${table.bundleSource}）` : table.tableName"
                @click="onSelectTable(table)"
              >
                <span class="table-name lme-ellipsis">{{ table.tableName }}</span>
                <span class="table-record-count lme-mono">
                  {{ table.recordCount.toLocaleString('zh-CN') }}
                </span>
                <span v-if="table.bundleSource" class="table-bundle lme-ellipsis">
                  {{ table.bundleSource }}
                </span>
              </div>
            </div>
          </div>

          <!-- 空态 -->
          <div v-if="!loading && groupedTables.length === 0" class="state-block">
            <NEmpty size="small" description="没有命中的静态数据表">
              <template #extra>
                <span class="state-hint">换个关键词，或把「数据类」切回「全部」</span>
              </template>
            </NEmpty>
          </div>

          <!-- 初次加载态 -->
          <div v-if="loading && allTables.length === 0" class="state-block">
            <NSpin size="small" />
            <span class="state-text">正在读取静态数据表列表…</span>
          </div>
        </div>
      </NCard>

      <!-- ── 中：记录列表 ── -->
      <NCard class="panel panel-records" size="small" bordered :content-style="{ padding: '0px' }">
        <template #header>
          <div class="panel-head compact">
            <div class="panel-title">
              <span class="panel-title-text">记录列表</span>
              <NTag class="panel-badge" size="small" round :bordered="false">
                {{ totalRecordCount.toLocaleString('zh-CN') }}
              </NTag>
            </div>
          </div>
        </template>

        <div class="panel-body">
          <VirtualList
            v-if="records.length > 0"
            :items="records"
            :item-height="30"
            :overscan="8"
            grow
          >
            <template #default="{ item }">
              <div
                class="record-row"
                :class="{ selected: selectedRecord?.key === item.key }"
                :title="item.key"
                @click="onSelectRecord(item)"
              >
                <span class="record-key lme-ellipsis">{{ item.key }}</span>
                <NTag
                  class="record-state"
                  size="small"
                  :bordered="false"
                  :type="recordStateTag[item.editState].type"
                >
                  {{ recordStateTag[item.editState].label }}
                </NTag>
              </div>
            </template>
          </VirtualList>

          <div v-else-if="loading" class="state-block">
            <NSpin size="small" />
            <span class="state-text">正在读取记录…</span>
          </div>

          <div v-else class="state-block">
            <NEmpty
              size="small"
              :description="selectedTable ? '本页没有记录' : '未选择表'"
            >
              <template #extra>
                <span v-if="!selectedTable" class="state-hint">先在左侧选一张静态数据表</span>
              </template>
            </NEmpty>
          </div>
        </div>

        <template #footer>
          <div class="panel-foot">
            <PageBar
              :current-page="currentPage"
              :page-count="pageCount"
              :total-count="totalRecordCount"
              :query-ms="lastQueryMs"
              :loading="loading"
              @go-to="onGoToPage"
            />
          </div>
        </template>
      </NCard>

      <!-- ── 右：JSON 编辑器 ── -->
      <NCard class="panel panel-editor" size="small" bordered :content-style="{ padding: '0px' }">
        <template #header>
          <div class="panel-head compact">
            <NTabs
              class="editor-tabs"
              type="bar"
              size="small"
              :value="editorTab"
              @update:value="onEditorTabChange"
            >
              <NTab class="segmented-btn" name="tree">🌳 树视图</NTab>
              <NTab class="segmented-btn" name="raw">{} 原始文本</NTab>
              <NTab class="segmented-btn" name="diff">⚖️ 差异对比</NTab>
            </NTabs>

            <NTag v-if="selectedRecord" class="record-key-badge" size="small" :bordered="false">
              {{ selectedRecord.key }}
            </NTag>
          </div>
        </template>

        <div class="panel-body">
          <!-- 树视图 -->
          <div v-if="editorMode === 'tree' && !showDiff" class="tree-view">
            <NAlert
              v-if="parseError"
              class="parse-error"
              type="error"
              :closable="false"
            >
              JSON 解析错误: {{ parseError }}
            </NAlert>
            <div v-else-if="rootNode" class="tree-content">
              <JsonTreeNode :node="rootNode" :depth="0" />
            </div>
            <div v-else class="state-block">
              <NEmpty size="small" description="选择一条记录以查看 JSON">
                <template #extra>
                  <span class="state-hint">点击中间列表里的任意一行</span>
                </template>
              </NEmpty>
            </div>
          </div>

          <!-- 原始文本 -->
          <div v-else-if="editorMode === 'raw' && !showDiff" class="raw-view">
            <NInput
              v-model:value="rawJsonText"
              class="raw-textarea"
              type="textarea"
              size="small"
              :resizable="false"
              :input-props="{ spellcheck: false }"
              @update:value="onRawJsonInput"
            />
            <NAlert
              v-if="parseError"
              class="parse-error"
              type="error"
              :closable="false"
            >
              {{ parseError }}
            </NAlert>
            <!-- 记录编辑入口：改完直接保存，保存后用 static.readRecord 读回覆盖上面 -->
            <div class="save-bar">
              <NTooltip placement="top" :show-arrow="false">
                <template #trigger>
                  <NButton
                    size="small"
                    type="primary"
                    :disabled="!selectedRecord || saving"
                    @click="requestSaveRecord"
                  >
                    {{ saving ? '保存中…' : '💾 保存修改' }}
                  </NButton>
                </template>
                用编辑器里的 JSON 整条替换该记录（会写进静态编辑集）
              </NTooltip>
            </div>
          </div>

          <!-- Diff 视图 -->
          <div v-else-if="showDiff" class="diff-view">
            <pre class="diff-content">{{ diffContent }}</pre>
          </div>
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.static-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  position: relative;
  background: var(--lme-bg-base);
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

/* ── 页头工具栏 ── */
.page-toolbar {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-lg);
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border-bottom: 1px solid var(--lme-border);
}

.toolbar-leading {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.toolbar-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-md);
  flex-shrink: 0;
}

.toolbar-titles {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.toolbar-name {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  line-height: var(--lme-line-height-tight);
  color: var(--lme-text-primary);
}

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

.toolbar-table {
  color: var(--lme-text-secondary);
}

.toolbar-dot {
  color: var(--lme-text-disabled);
}

.toolbar-actions {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  flex-shrink: 0;
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.result-count :deep(strong) {
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-semibold);
}

/* ── 错误态 ── */
.error-banner {
  flex-shrink: 0;
  margin: var(--lme-gap-md) var(--lme-gap-lg) 0;
  border-radius: var(--lme-radius-md);
  font-size: var(--lme-font-size-sm);
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

.panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: var(--lme-bg-panel);
  border-radius: var(--lme-radius-lg);
  overflow: hidden;
}

/* NCard 内部：头部 / 内容 / 底部都交给 .panel-head / .panel-body / .panel-foot 排版 */
.panel :deep(.n-card-header) {
  display: block;
  padding: 0;
  min-height: 0;
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
}

.panel :deep(.n-card-header__main) {
  padding: 0;
  width: 100%;
  font-size: var(--lme-font-size-sm);
}

.panel :deep(.n-card-content) {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.panel :deep(.n-card__footer) {
  padding: 0;
  background: var(--lme-bg-elevated);
  border-top: 1px solid var(--lme-border);
}

.panel-tables {
  flex: 0 0 300px;
}

.panel-records {
  flex: 0 0 300px;
}

.panel-editor {
  flex: 1;
}

@media (max-width: 1180px) {
  .panel-tables,
  .panel-records {
    flex-basis: 240px;
  }
}

.panel-head {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
}

.panel-head.compact {
  flex-direction: row;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-sm);
  height: 38px;
  padding: 0 var(--lme-gap-sm) 0 var(--lme-gap-md);
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

.panel-badge,
.group-count,
.record-key-badge {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
}

.panel-badge :deep(.n-tag__text),
.group-count :deep(.n-tag__text),
.record-key-badge :deep(.n-tag__text) {
  font-family: var(--lme-font-mono);
}

.panel-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  position: relative;
}

.panel-foot {
  flex-shrink: 0;
}

/* ── 搜索框 / 筛选 ── */
.panel-search {
  display: flex;
  align-items: center;
}

.panel-search-icon {
  font-size: var(--lme-font-size-sm);
  opacity: 0.6;
}

.search-input {
  width: 100%;
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
  min-width: 88px;
}

/* ── 分组头 ── */
.class-group {
  display: flex;
  flex-direction: column;
}

.class-group-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  cursor: pointer;
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  user-select: none;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.class-group-header:hover {
  background: var(--lme-bg-hover);
}

.class-group-header:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

.group-arrow {
  width: 14px;
  text-align: center;
  font-size: 9px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.group-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
  height: 30px;
  padding: 0 var(--lme-gap-md) 0 calc(var(--lme-gap-md) + 14px);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.table-row:hover {
  background: var(--lme-bg-hover);
}

.table-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
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

.table-row.selected .table-name {
  font-weight: var(--lme-font-weight-medium);
}

.table-record-count {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  flex-shrink: 0;
}

.table-bundle {
  max-width: 76px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-disabled);
  flex-shrink: 0;
}

/* ── 记录行 ── */
.record-row {
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

.record-row:hover {
  background: var(--lme-bg-hover);
}

.record-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

.record-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.record-key {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  min-width: 0;
}

.record-row.selected .record-key {
  color: var(--lme-text-primary);
}

/* ── 空态 / 加载态（与资源工作台同一套） ── */
.state-block {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-xl) var(--lme-gap-md);
  color: var(--lme-text-muted);
  text-align: center;
}

.state-text {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.state-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-disabled);
}

/* ── 编辑器分段 Tab（树视图 / 原始文本 / 差异对比） ── */
.editor-tabs {
  flex: 1;
  min-width: 0;
}

.editor-tabs :deep(.n-tabs-nav) {
  height: 30px;
}

.editor-tabs :deep(.n-tabs-wrapper) {
  height: 30px;
}

.editor-tabs :deep(.segmented-btn) {
  padding: 0 var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.editor-tabs :deep(.segmented-btn:hover) {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.editor-tabs :deep(.segmented-btn.n-tabs-tab--active) {
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
}

.record-key-badge {
  max-width: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex-shrink: 0;
}

/* ── 树视图 ── */
.tree-view {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: auto;
}

.tree-content {
  padding: var(--lme-gap-sm);
}

/* JSON 树节点由文件内 JsonTreeNode（defineComponent + h）渲染，用 :deep 穿透 */
:deep(.json-tree-node) {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
}

:deep(.json-tree-row) {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  user-select: none;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

:deep(.json-tree-row:hover) {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

:deep(.json-tree-row:focus-visible) {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

:deep(.json-tree-row.leaf) {
  cursor: default;
}

:deep(.json-tree-arrow) {
  width: 14px;
  text-align: center;
  font-size: 9px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

:deep(.json-tree-key) {
  color: var(--lme-text-primary);
  flex-shrink: 0;
}

:deep(.json-tree-label) {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}

:deep(.json-tree-value) {
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
  min-height: 0;
  overflow: hidden;
  padding: var(--lme-gap-md);
  gap: var(--lme-gap-sm);
}

.raw-textarea {
  flex: 1;
  min-height: 0;
}

.raw-textarea :deep(.n-input-wrapper),
.raw-textarea :deep(.n-input__textarea),
.raw-textarea :deep(.n-scrollbar-container),
.raw-textarea :deep(.n-input__textarea-el) {
  height: 100%;
}

.raw-textarea :deep(.n-input__textarea-el) {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  line-height: var(--lme-line-height-normal);
  padding: var(--lme-gap-md);
  tab-size: 2;
  resize: none;
}

/* ── 保存条 ── */
.save-bar {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-shrink: 0;
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
  border-radius: var(--lme-radius-md);
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-mono);
  flex-shrink: 0;
}

/* ── 库组件焦点环（tokens 统一） ── */
:deep(.n-button:focus-visible),
:deep(.n-input:focus-visible),
:deep(.n-base-selection:focus-visible) {
  box-shadow: var(--lme-shadow-focus);
}
</style>
