<script setup lang="ts">
// 文本/本地化工作台页面（TextView）
// 三栏主从栅格：语言文件 | 键值条目 | 差异预览（v2 设计语言重做，只动模板与样式）
// v3（ui-redesign r3）：手写版式替换为 Naive UI 组件（面板/按钮/输入/下拉/标签/空态/提示），功能不变
// 对应 WPF 旧界面 TextWorkbenchPage
// IPC 方法：text.fileTreeRoots / text.fileTreeChildren / text.fileEntries / text.applyPatch / text.fileSearch
// 未实现的 IPC 方法降级为「暂未实现」提示

import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import VirtualList from '@/components/VirtualList.vue'
import {
  NAlert,
  NButton,
  NCard,
  NEmpty,
  NInput,
  NSelect,
  NSpin,
  NTag,
  NTooltip,
  useMessage,
} from 'naive-ui'

const route = useRoute()
const router = useRouter()

// 呈现层提示（App.vue 已提供 NMessageProvider），不参与 IPC 时序
const message = useMessage()

// ── 本地模型 ──────────────────────────────────────────────

interface FileNode {
  name: string
  path: string
  isLeaf: boolean
  entryCount: number
  language: string
  expanded: boolean
  loading: boolean
  children: FileNode[] | null
  depth: number
}

interface KeyValueEntry {
  id: number
  key: string
  value: string
  originalValue: string
  editState: 'Unchanged' | 'Modified' | 'Added' | 'Deleted'
}

// ── 浏览状态 ──────────────────────────────────────────────

const searchText = ref('')
const languageFilter = ref('')
const fileTypeFilter = ref('')
const fileTreeRoots = ref<FileNode[]>([])
const fileTreeLoading = ref(false)
let fileTreeGeneration = 0

// 下拉选项（语言 / 文件类型）
const languageOptions = [
  { label: '全部语言', value: '' },
  { label: '简体中文 (zh-CN)', value: 'zh-CN' },
  { label: 'English (en-US)', value: 'en-US' },
  { label: '日本語 (ja-JP)', value: 'ja-JP' },
  { label: '한국어 (ko-KR)', value: 'ko-KR' },
]

const fileTypeOptions = [
  { label: '全部类型', value: '' },
  { label: 'JSON', value: 'json' },
  { label: 'YAML', value: 'yaml' },
  { label: 'CSV', value: 'csv' },
  { label: 'PO (gettext)', value: 'po' },
]

// 搜索结果（当搜索框有值时替代目录树）
const searchResults = ref<FileNode[]>([])
const searchLoading = ref(false)

// ── 编辑状态 ──────────────────────────────────────────────

const selectedFilePath = ref<string | null>(null)
const selectedFileLanguage = ref('')
const entries = ref<KeyValueEntry[]>([])
const originalEntriesMap = ref<Map<string, string>>(new Map())
const entriesLoading = ref(false)

// 键值列表分页
const entriesOffset = ref(0)
const entriesPageSize = 200
const entriesTotalCount = ref(0)

// 文件内搜索
const inlineSearchText = ref('')

// 差异视图
const showDiffView = ref(false)

// 保存
const saving = ref(false)
// 保存失败（含 IPC 未实现）时的错误条文案，空串 = 无错误
const saveError = ref('')

// ── 计算属性 ──────────────────────────────────────────────

const modifiedCount = computed(
  () => entries.value.filter((e) => e.editState === 'Modified').length,
)
const addedCount = computed(
  () => entries.value.filter((e) => e.editState === 'Added').length,
)
const deletedCount = computed(
  () => entries.value.filter((e) => e.editState === 'Deleted').length,
)

// 匹配当前内联搜索的条目 ID 集合
const matchedEntryIds = computed<Set<number>>(() => {
  if (!inlineSearchText.value) return new Set()
  const needle = inlineSearchText.value.toLowerCase()
  const ids = new Set<number>()
  for (const entry of entries.value) {
    if (
      entry.key.toLowerCase().includes(needle) ||
      entry.value.toLowerCase().includes(needle)
    ) {
      ids.add(entry.id)
    }
  }
  return ids
})

// 变更列表（用于差异视图）
const changedEntries = computed(() =>
  entries.value.filter((e) => e.editState !== 'Unchanged'),
)

// ── 文件树（懒加载） ─────────────────────────────────────

async function loadFileTreeRoots() {
  const gen = ++fileTreeGeneration
  fileTreeLoading.value = true
  try {
    // 契约方法 lang.files：载荷 { language, offset, take }，响应 { items: LangFileInfoItem[], totalCount }
    const result = await ipc.request<{
      items: { relativePath: string; sizeBytes: number; keyCount: number; isUtf8: boolean }[]
      totalCount: number
    }>('lang.files', { language: languageFilter.value, offset: 0, take: 200 })
    if (gen !== fileTreeGeneration) return
    // lang.files 是扁平文件列表（后端暂无目录层级接口），节点一律为叶子
    fileTreeRoots.value = result.items.map((f) => ({
      name: f.relativePath,
      path: f.relativePath,
      isLeaf: true,
      entryCount: f.keyCount,
      language: languageFilter.value,
      expanded: false,
      loading: false,
      children: null,
      depth: 0,
    }))
  } catch {
    if (gen !== fileTreeGeneration) return
    fileTreeRoots.value = []
  } finally {
    if (gen === fileTreeGeneration) fileTreeLoading.value = false
  }
}

async function toggleFileNode(node: FileNode) {
  if (node.isLeaf) {
    selectFileNode(node)
    return
  }

  node.expanded = !node.expanded

  // 折叠时释放子节点数组
  if (!node.expanded) {
    node.children = null
    return
  }

  if (node.expanded && node.children === null) {
    node.loading = true
    const gen = fileTreeGeneration
    try {
      const result = await ipc.request<{ nodes: FileNode[] }>('text.fileTreeChildren', {
        parentPath: node.path,
      })
      if (gen !== fileTreeGeneration) return
      node.children = result.nodes.map((n) => ({
        ...n,
        expanded: false,
        loading: false,
        children: null,
        depth: node.depth + 1,
      }))
    } catch {
      if (gen !== fileTreeGeneration) return
      node.children = []
    } finally {
      if (gen === fileTreeGeneration) node.loading = false
    }
  }
}

// ── 文件条目加载（服务端分页） ────────────────────────────

async function loadEntriesPage(path: string, offset: number, append: boolean) {
  entriesLoading.value = true
  try {
    const result = await ipc.request<{
      entries: Array<{ key: string; value: string }>
      totalCount: number
      language: string
    }>('text.fileEntries', {
      path,
      offset,
      take: entriesPageSize,
    })

    const originalMap = new Map<string, string>()
    for (const e of result.entries) {
      originalMap.set(e.key, e.value)
    }

    if (append) {
      // 追加模式：合并并保留已有修改
      const existingKeys = new Set(entries.value.map((e) => e.key))
      const newEntries = result.entries
        .filter((e) => !existingKeys.has(e.key))
        .map((e, i) => ({
          id: entries.value.length + i,
          key: e.key,
          value: e.value,
          originalValue: e.value,
          editState: 'Unchanged' as const,
        }))
      entries.value.push(...newEntries)
    } else {
      // 替换模式：重置
      originalEntriesMap.value = originalMap
      entries.value = result.entries.map((e, i) => ({
        id: i,
        key: e.key,
        value: e.value,
        originalValue: e.value,
        editState: 'Unchanged' as const,
      }))
    }

    entriesOffset.value = offset
    entriesTotalCount.value = result.totalCount
    selectedFileLanguage.value = result.language
  } catch {
    entries.value = []
    originalEntriesMap.value = new Map()
    entriesTotalCount.value = 0
  } finally {
    entriesLoading.value = false
  }
}

// ── 文件选择 ─────────────────────────────────────────────

function selectFileNode(node: FileNode) {
  selectedFilePath.value = node.path
  selectedFileLanguage.value = node.language
  entries.value = []
  entriesOffset.value = 0
  entriesTotalCount.value = 0
  inlineSearchText.value = ''
  showDiffView.value = false
  // 加载第一页
  loadEntriesPage(node.path, 0, false)
}

// ── 搜索防抖 ─────────────────────────────────────────────

let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null

function onSearchInput() {
  if (searchDebounceTimer) clearTimeout(searchDebounceTimer)
  searchDebounceTimer = setTimeout(() => {
    performFileSearch()
  }, 250)
}

async function performFileSearch() {
  const query = searchText.value.trim()
  if (!query) {
    // 清空搜索时恢复目录树
    searchResults.value = []
    searchLoading.value = false
    loadFileTreeRoots()
    return
  }

  searchLoading.value = true
  try {
    // 契约方法 lang.search：载荷 { language, keyword, offset, take }，响应 { items: LangSearchHitItem[] }
    const result = await ipc.request<{
      items: { relativePath: string; kind: string; keyPath?: string; snippet?: string }[]
      totalCount: number
    }>('lang.search', {
      language: languageFilter.value,
      keyword: query,
      offset: 0,
      take: 200,
    })
    searchResults.value = result.items.map((h) => ({
      name: h.keyPath ?? h.relativePath,
      path: h.relativePath,
      isLeaf: true,
      entryCount: 0,
      language: languageFilter.value,
      expanded: false,
      loading: false,
      children: null,
      depth: 0,
    }))
  } catch {
    // 搜索方法未实现时，降级为提示
    searchResults.value = []
  } finally {
    searchLoading.value = false
  }
}

watch([languageFilter, fileTypeFilter], () => {
  if (searchText.value.trim()) {
    performFileSearch()
  }
})

// ── 值编辑 ────────────────────────────────────────────────

function onValueEdit(entry: KeyValueEntry, newValue: string) {
  entry.value = newValue
  entry.editState = newValue === entry.originalValue ? 'Unchanged' : 'Modified'
}

/** NInput 的 update:value 载荷可能是 [string, string]（成对输入），这里只取单值文本 */
function onValueInput(entry: KeyValueEntry, value: string | [string, string]) {
  onValueEdit(entry, typeof value === 'string' ? value : '')
}

function deleteEntry(entry: KeyValueEntry) {
  if (entry.editState === 'Added') {
    // 新增的直接移除
    entries.value = entries.value.filter((e) => e.id !== entry.id)
  } else {
    entry.editState = 'Deleted'
  }
}

function restoreEntry(entry: KeyValueEntry) {
  if (entry.editState === 'Added') {
    entries.value = entries.value.filter((e) => e.id !== entry.id)
  } else {
    entry.value = entry.originalValue
    entry.editState = 'Unchanged'
  }
}

// ── 保存补丁 ─────────────────────────────────────────────

async function saveAsPatch() {
  if (!selectedFilePath.value) return
  saving.value = true
  saveError.value = ''
  const changeCount = changedEntries.value.length
  try {
    const patches = changedEntries.value
      .filter((e) => e.editState === 'Modified' || e.editState === 'Added' || e.editState === 'Deleted')
      .map((e) => ({
        key: e.key,
        original: e.originalValue,
        modified: e.editState === 'Deleted' ? null : e.value,
        state: e.editState,
      }))

    await ipc.request('text.applyPatch', {
      path: selectedFilePath.value,
      patches,
    })

    // 保存成功后重新加载条目（服务器可能规范化格式）
    await loadEntriesPage(selectedFilePath.value, 0, false)
    message.success(`已保存 ${changeCount} 处改动`)
  } catch {
    // IPC 方法未实现时显示提示（错误条，替代原生 alert）
    saveError.value = '暂未实现：text.applyPatch 不可用（保存补丁）'
  } finally {
    saving.value = false
  }
}

// ── 虚拟列表滚动加载（服务端分页） ─────────────────────────

function onEntriesRangeChange(_start: number, end: number) {
  // 当滚动到接近末尾时，加载下一页
  const shouldLoadMore = end >= entries.value.length - 10
  const hasMore = entriesOffset.value + entriesPageSize < entriesTotalCount.value
  if (shouldLoadMore && hasMore && !entriesLoading.value && selectedFilePath.value) {
    loadEntriesPage(selectedFilePath.value, entriesOffset.value + entriesPageSize, true)
  }
}

// ── 生命周期 ─────────────────────────────────────────────

// ═══════════ 按文件定位（维基「去编辑」深链）═══════════

/** /text?file=<语言文件相对路径> */
const fileFromQuery = computed(() => {
  const raw = route.query.file
  return typeof raw === 'string' ? raw.trim() : ''
})

/** 定位完就摘掉 file 参数：刷新（或后退重进）不会再重复触发一次定位 */
function clearFileParam() {
  if (route.query.file === undefined) return
  const next = { ...route.query }
  delete next.file
  void router.replace({ path: route.path, query: next })
}

onMounted(() => {
  // 深链带了文件：填进搜索框查一次（后端 lang.search 按关键字匹配路径）
  if (fileFromQuery.value) {
    searchText.value = fileFromQuery.value
    performFileSearch()
    clearFileParam()
    return
  }
  loadFileTreeRoots()
})

onUnmounted(() => {
  if (searchDebounceTimer) clearTimeout(searchDebounceTimer)
})
</script>

<template>
  <div class="text-view">
    <!-- 顶部细进度条（替代整屏遮罩） -->
    <div
      v-if="fileTreeLoading || searchLoading || entriesLoading"
      class="loading-bar"
      aria-hidden="true"
    />

    <!-- 页头工具栏 -->
    <header class="page-toolbar">
      <div class="toolbar-leading">
        <span class="toolbar-icon">📝</span>
        <div class="toolbar-titles">
          <h1 class="toolbar-name">文本 / 本地化</h1>
          <span class="toolbar-sub" v-if="selectedFilePath">
            <span class="toolbar-file lme-mono">{{ selectedFilePath }}</span>
            <span class="toolbar-dot">·</span>
            <span class="toolbar-language" v-if="selectedFileLanguage">
              {{ selectedFileLanguage }}
            </span>
          </span>
          <span class="toolbar-sub" v-else>
            从左侧选择一个语言文件，逐条改键名对应的文本
          </span>
        </div>
      </div>

      <div class="toolbar-actions">
        <NTag
          v-if="!fileTreeLoading && !searchLoading"
          class="result-count"
          size="small"
          :bordered="false"
        >
          共
          <strong>
            {{ (searchResults.length || fileTreeRoots.length).toLocaleString('zh-CN') }}
          </strong>
          个文件
        </NTag>
        <NTag v-if="modifiedCount > 0" class="toolbar-chip" size="small" type="warning" round>
          已修改 {{ modifiedCount }}
        </NTag>
        <NTag v-if="addedCount > 0" class="toolbar-chip" size="small" type="success" round>
          已添加 {{ addedCount }}
        </NTag>
        <NTag v-if="deletedCount > 0" class="toolbar-chip" size="small" type="error" round>
          已删除 {{ deletedCount }}
        </NTag>
      </div>
    </header>

    <!-- 错误态（保存失败 / IPC 未实现） -->
    <NAlert v-if="saveError" class="error-banner" type="error" closable @close="saveError = ''">
      {{ saveError }}
    </NAlert>

    <!-- 三栏主从：语言文件 | 键值条目 | 差异预览 -->
    <div class="workbench-body">
      <!-- ── 左：语言文件（搜索结果 / 文件树） ── -->
      <NCard
        class="panel panel-files"
        size="small"
        bordered
        :content-style="{ padding: '0px' }"
      >
        <template #header>
          <div class="panel-head">
            <div class="panel-search">
              <NInput
                v-model:value="searchText"
                class="search-input"
                size="small"
                clearable
                placeholder="搜索文件路径或键/值文本…"
                @update:value="onSearchInput"
                @clear="onSearchInput"
              >
                <template #prefix>
                  <span class="panel-search-icon">🔍</span>
                </template>
              </NInput>
            </div>

            <div class="filter-row">
              <span class="filter-label">语言</span>
              <NSelect
                v-model:value="languageFilter"
                class="filter-select"
                size="small"
                :options="languageOptions"
              />

              <span class="filter-label">类型</span>
              <NSelect
                v-model:value="fileTypeFilter"
                class="filter-select"
                size="small"
                :options="fileTypeOptions"
              />
            </div>
          </div>
        </template>

        <div class="panel-body scroll">
          <!-- 加载态 -->
          <div v-if="fileTreeLoading || searchLoading" class="state-block">
            <NSpin size="small" />
            <span class="state-text">正在读取语言文件…</span>
          </div>

          <!-- 搜索结果 -->
          <template v-else-if="searchResults.length > 0">
            <div class="results-header">搜索命中 {{ searchResults.length }} 个文件</div>
            <div
              v-for="node in searchResults"
              :key="node.path"
              class="file-row"
              :class="{ selected: selectedFilePath === node.path }"
              :title="node.path"
              @click="node.isLeaf && selectFileNode(node)"
            >
              <span class="file-name lme-mono lme-ellipsis">{{ node.path }}</span>
              <NTag v-if="node.language" class="file-tag" size="small" round :bordered="false">
                {{ node.language }}
              </NTag>
              <span v-if="node.entryCount > 0" class="file-count lme-mono">
                {{ node.entryCount }} 条
              </span>
            </div>

            <!-- 原文此处条件与外层互斥（恒不可达），保持原样未改 -->
            <div
              v-if="searchResults.length === 0 && searchText.trim()"
              class="state-block"
            >
              <NEmpty size="small" description="未找到匹配文件">
                <template #extra>
                  <span class="state-hint">尝试调整搜索关键词或清除筛选条件</span>
                </template>
              </NEmpty>
            </div>
          </template>

          <!-- 目录树 -->
          <template v-else>
            <div v-for="node in fileTreeRoots" :key="node.path" class="tree-group">
              <div
                class="tree-row"
                :class="{ selected: selectedFilePath === node.path }"
                :style="{ paddingLeft: 'calc(var(--lme-gap-lg) * ' + node.depth + ' + var(--lme-gap-sm))' }"
                @click="toggleFileNode(node)"
              >
                <span class="tree-arrow">
                  {{ node.isLeaf ? '•' : node.loading ? '⏳' : node.expanded ? '▼' : '▶' }}
                </span>
                <span class="tree-node-name lme-ellipsis" :title="node.name">
                  {{ node.name }}
                </span>
                <NTag class="tree-count" size="small" round :bordered="false">
                  {{ node.isLeaf ? node.entryCount : (node.children?.length ?? 0) }}
                </NTag>
              </div>

              <!-- 子节点（展开时渲染） -->
              <template v-if="node.expanded && node.children">
                <div
                  v-for="child in node.children"
                  :key="child.path"
                  class="tree-row"
                  :class="{ selected: selectedFilePath === child.path }"
                  :style="{ paddingLeft: 'calc(var(--lme-gap-lg) * ' + child.depth + ' + var(--lme-gap-sm))' }"
                  @click="toggleFileNode(child)"
                >
                  <span class="tree-arrow">
                    {{ child.isLeaf ? '•' : child.loading ? '⏳' : child.expanded ? '▼' : '▶' }}
                  </span>
                  <span class="tree-node-name lme-ellipsis" :title="child.name">
                    {{ child.name }}
                  </span>
                  <NTag class="tree-count" size="small" round :bordered="false">
                    {{ child.isLeaf ? child.entryCount : (child.children?.length ?? 0) }}
                  </NTag>
                </div>
              </template>
            </div>

            <!-- 空态 -->
            <div v-if="fileTreeRoots.length === 0" class="state-block">
              <NEmpty size="small" description="暂无语言文件">
                <template #extra>
                  <span class="state-hint">
                    未找到可用的本地化语言文件。请确认项目已包含 .json / .yaml 等文本资源。
                  </span>
                </template>
              </NEmpty>
            </div>
          </template>
        </div>
      </NCard>

      <!-- ── 中：键值条目（内联编辑） ── -->
      <NCard
        class="panel panel-entries"
        size="small"
        bordered
        :content-style="{ padding: '0px' }"
      >
        <template #header>
          <div class="panel-head compact">
            <div class="panel-title">
              <span class="panel-title-text">键值条目</span>
              <NTag class="panel-badge" size="small" round :bordered="false">
                {{ entries.length.toLocaleString('zh-CN') }}
              </NTag>
            </div>

            <div class="panel-head-actions">
              <div class="panel-search inline">
                <NInput
                  v-model:value="inlineSearchText"
                  class="search-input"
                  size="small"
                  clearable
                  placeholder="搜索键名或值内容…"
                >
                  <template #prefix>
                    <span class="panel-search-icon">🔍</span>
                  </template>
                </NInput>
              </div>

              <!-- 差异视图开关：保持原生 checkbox（截图脚本统计 input 元素） -->
              <label class="diff-toggle">
                <input type="checkbox" v-model="showDiffView" />
                差异视图
              </label>

              <NTooltip placement="bottom" :show-arrow="false">
                <template #trigger>
                  <NButton
                    size="small"
                    type="primary"
                    :disabled="saving || changedEntries.length === 0"
                    :loading="saving"
                    @click="saveAsPatch"
                  >
                    {{ saving ? '保存中…' : '💾 保存补丁' }}
                  </NButton>
                </template>
                把本文件的改动存成替换补丁（text.applyPatch）
              </NTooltip>
            </div>
          </div>
        </template>

        <div class="panel-body fill">
          <!-- 列标题 -->
          <div class="kv-columns">
            <span class="kv-col-key">键名</span>
            <span class="kv-col-value">值</span>
            <span class="kv-col-actions">操作</span>
          </div>

          <div class="kv-list-container">
            <!-- 加载态 -->
            <div v-if="entriesLoading && entries.length === 0" class="state-block">
              <NSpin size="small" />
              <span class="state-text">正在读取键值条目…</span>
            </div>

            <!-- 空态：未选文件 -->
            <div v-else-if="!selectedFilePath" class="state-block">
              <NEmpty size="small" description="未选择语言文件">
                <template #extra>
                  <span class="state-hint">
                    请从左侧文件树中选择一个语言文件进行编辑，或通过搜索框快速定位键值文本。支持
                    JSON、YAML、CSV、PO 等格式。
                  </span>
                </template>
              </NEmpty>
            </div>

            <!-- 空态：文件无条目 -->
            <div v-else-if="entries.length === 0" class="state-block">
              <NEmpty size="small" description="该文件无键值条目" />
            </div>

            <!-- 键值行（虚拟滚动，滚动到底自动取下一页） -->
            <VirtualList
              v-else
              :items="entries"
              :item-height="30"
              :height="600"
              :overscan="8"
              grow
              @range-change="onEntriesRangeChange"
            >
              <template #default="{ item }">
                <div
                  class="kv-row"
                  :class="{
                    'match-row': matchedEntryIds.has(item.id),
                    'modified-row': item.editState === 'Modified',
                    'added-row': item.editState === 'Added',
                    'deleted-row': item.editState === 'Deleted',
                  }"
                >
                  <span class="kv-key lme-mono lme-ellipsis" :title="item.key">
                    {{ item.key }}
                  </span>
                  <span class="kv-value" :title="item.value">
                    <NInput
                      v-if="item.editState !== 'Deleted'"
                      :value="item.value"
                      class="value-input"
                      size="small"
                      @update:value="onValueInput(item, $event)"
                    />
                    <del v-else class="deleted-text">{{ item.originalValue }}</del>
                  </span>
                  <span class="kv-actions">
                    <NButton
                      v-if="item.editState !== 'Unchanged'"
                      class="row-btn"
                      size="tiny"
                      quaternary
                      type="info"
                      title="恢复原值"
                      @click="restoreEntry(item)"
                    >
                      恢复
                    </NButton>
                    <NButton
                      v-if="item.editState !== 'Unchanged'"
                      class="row-btn delete-btn"
                      size="tiny"
                      quaternary
                      type="error"
                      title="删除条目"
                      @click="deleteEntry(item)"
                    >
                      删除
                    </NButton>
                  </span>
                </div>
              </template>
            </VirtualList>
          </div>

          <!-- 还有下一页时的提示（滚动即自动加载） -->
          <div
            v-if="entriesOffset + entriesPageSize < entriesTotalCount"
            class="panel-foot page-bar"
          >
            <span class="load-more-hint lme-mono">
              向下滚动加载更多（{{ entries.length }} / {{ entriesTotalCount }}）
            </span>
          </div>
        </div>
      </NCard>

      <!-- ── 右：差异预览 ── -->
      <NCard
        class="panel panel-diff"
        size="small"
        bordered
        :content-style="{ padding: '0px' }"
      >
        <template #header>
          <div class="panel-head compact">
            <div class="panel-title">
              <span class="panel-title-text">差异预览</span>
              <NTag class="panel-badge" size="small" round :bordered="false">
                {{ changedEntries.length }} 处变更
              </NTag>
            </div>
          </div>
        </template>

        <div class="panel-body scroll">
          <!-- 未开启 -->
          <div v-if="!showDiffView" class="state-block">
            <NEmpty size="small" description="差异视图已关闭">
              <template #extra>
                <span class="state-hint">勾选中间栏的「差异视图」在此查看原值 → 新值</span>
              </template>
            </NEmpty>
          </div>

          <!-- 无改动 -->
          <div v-else-if="changedEntries.length === 0" class="state-block">
            <NEmpty size="small" description="暂无修改">
              <template #extra>
                <span class="state-hint">在键值列表里改值后，此处将显示差异</span>
              </template>
            </NEmpty>
          </div>

          <!-- 变更列表 -->
          <div v-else class="diff-list">
            <div
              v-for="entry in changedEntries"
              :key="entry.id"
              class="diff-entry"
              :class="'diff-' + entry.editState.toLowerCase()"
            >
              <div class="diff-head">
                <span class="diff-key lme-mono">{{ entry.key }}</span>
                <NTag
                  size="small"
                  :bordered="false"
                  :type="
                    entry.editState === 'Deleted'
                      ? 'error'
                      : entry.editState === 'Added'
                        ? 'success'
                        : 'warning'
                  "
                >
                  {{ entry.editState === 'Deleted' ? '已删除' : entry.editState === 'Added' ? '已添加' : '已修改' }}
                </NTag>
              </div>
              <div class="diff-body">
                <div v-if="entry.editState === 'Deleted'" class="diff-old">
                  {{ entry.originalValue || '(空)' }}
                </div>
                <div v-else class="diff-old">
                  <del>{{ entry.originalValue || '(空)' }}</del>
                </div>
                <div class="diff-arrow">→</div>
                <div v-if="entry.editState === 'Deleted'" class="diff-new diff-deleted">
                  (已删除)
                </div>
                <div v-else class="diff-new">{{ entry.value || '(空)' }}</div>
              </div>
            </div>
          </div>
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.text-view {
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

.toolbar-file {
  color: var(--lme-text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.toolbar-language {
  color: var(--lme-accent);
  flex-shrink: 0;
}

.toolbar-dot {
  color: var(--lme-text-disabled);
}

.toolbar-actions {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-shrink: 0;
}

.result-count {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  white-space: nowrap;
}

.result-count :deep(strong) {
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-semibold);
}

.toolbar-chip {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
  white-space: nowrap;
}

.toolbar-chip :deep(.n-tag__text) {
  font-family: var(--lme-font-mono);
}

/* ── 错误条 ── */
.error-banner {
  flex-shrink: 0;
  margin: var(--lme-gap-md) var(--lme-gap-lg) 0;
  border-radius: var(--lme-radius-md);
  font-size: var(--lme-font-size-sm);
}

/* ── 三栏主从栅格 ── */
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

/* NCard 内部：头部 / 内容都交给 .panel-head / .panel-body 排版 */
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
  padding: 0;
}

.panel-files {
  flex: 0 0 320px;
}

.panel-entries {
  flex: 1 1 auto;
}

.panel-diff {
  flex: 0 0 320px;
}

@media (max-width: 1180px) {
  .panel-files,
  .panel-diff {
    flex-basis: 260px;
  }
}

.panel-head {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
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

.panel-badge {
  flex-shrink: 0;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
}

.panel-badge :deep(.n-tag__text) {
  font-family: var(--lme-font-mono);
}

.panel-head-actions {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex: 0 1 auto;
  min-width: 0;
}

.panel-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  position: relative;
}

.panel-body.scroll {
  overflow-y: auto;
}

.panel-body.fill {
  overflow: hidden;
}

.panel-foot {
  flex-shrink: 0;
  border-top: 1px solid var(--lme-border);
  background: var(--lme-bg-elevated);
}

/* ── 搜索框 / 筛选 ── */
.panel-search {
  display: flex;
  align-items: center;
}

.panel-search.inline {
  flex: 1 1 180px;
  min-width: 96px;
  max-width: 260px;
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

/* ── 差异视图开关（原生 checkbox，保留真实 input 元素） ── */
.diff-toggle {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
}

.diff-toggle:hover {
  color: var(--lme-text-primary);
}

.diff-toggle input[type='checkbox'] {
  accent-color: var(--lme-accent);
  cursor: pointer;
}

.diff-toggle input[type='checkbox']:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

/* ── 左栏：搜索结果与文件树行 ── */
.results-header {
  flex-shrink: 0;
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.file-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  height: 30px;
  flex-shrink: 0;
  padding: 0 var(--lme-gap-md);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.file-row:hover {
  background: var(--lme-bg-hover);
}

.file-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.file-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

.file-name {
  flex: 1;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 0;
}

.file-tag {
  flex-shrink: 0;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
}

.file-tag :deep(.n-tag__text) {
  font-family: var(--lme-font-mono);
}

.file-count {
  flex-shrink: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.tree-group {
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
}

.tree-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  height: 30px;
  flex-shrink: 0;
  padding-right: var(--lme-gap-md);
  cursor: pointer;
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  user-select: none;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.tree-row:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.tree-row.selected {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.tree-row:focus-visible {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}

.tree-arrow {
  width: 14px;
  text-align: center;
  font-size: 9px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.tree-node-name {
  flex: 1;
  min-width: 0;
}

.tree-count {
  flex-shrink: 0;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.tree-count :deep(.n-tag__text) {
  font-family: var(--lme-font-mono);
}

/* ── 中栏：键值表 ── */
.kv-columns {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  height: 26px;
  padding: 0 var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-panel);
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-muted);
  user-select: none;
}

.kv-col-key {
  flex: 2;
  min-width: 0;
}

.kv-col-value {
  flex: 3;
  min-width: 0;
}

.kv-col-actions {
  flex: 0 0 90px;
  text-align: center;
}

.kv-list-container {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  position: relative;
}

.kv-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  height: 100%;
  padding: 0 var(--lme-gap-md);
  border-left: 2px solid transparent;
  border-bottom: 1px solid var(--lme-row-divider);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.kv-row:hover {
  background: var(--lme-bg-hover);
}

.kv-row.match-row {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
}

.kv-row.modified-row {
  border-left-color: var(--lme-state-modified);
}

.kv-row.added-row {
  border-left-color: var(--lme-state-added);
}

.kv-row.deleted-row {
  border-left-color: var(--lme-state-deleted);
  opacity: 0.6;
}

.kv-key {
  flex: 2;
  min-width: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.kv-value {
  flex: 3;
  min-width: 0;
  display: flex;
  align-items: center;
}

/* 值输入框（NInput 渲染真实 input，行内透明底、聚焦才描边） */
.value-input {
  width: 100%;
}

.value-input :deep(.n-input__wrapper) {
  padding: 0 var(--lme-gap-xs);
  min-height: 24px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: var(--lme-radius-sm);
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.value-input :deep(.n-input__input-el) {
  height: 22px;
  line-height: var(--lme-line-height-normal);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
}

.value-input:hover :deep(.n-input__wrapper) {
  border-color: var(--lme-border);
}

.value-input.n-input--focus :deep(.n-input__wrapper) {
  border-color: var(--lme-accent);
  background: var(--lme-bg-input);
  box-shadow: var(--lme-shadow-focus);
}

.deleted-text {
  color: var(--lme-state-deleted);
  font-size: var(--lme-font-size-sm);
}

.kv-actions {
  flex: 0 0 90px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-xs);
}

.row-btn {
  font-size: var(--lme-font-size-xs);
}

.row-btn :deep(.n-button__content) {
  font-size: var(--lme-font-size-xs);
}

.load-more-hint {
  display: block;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  text-align: center;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 右栏：差异预览 ── */
.diff-list {
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
}

.diff-entry {
  flex-shrink: 0;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-row-divider);
  border-left: 2px solid transparent;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.diff-entry:hover {
  background: var(--lme-bg-hover);
}

.diff-entry.diff-modified {
  border-left-color: var(--lme-state-modified);
}

.diff-entry.diff-added {
  border-left-color: var(--lme-state-added);
}

.diff-entry.diff-deleted {
  border-left-color: var(--lme-state-deleted);
}

.diff-head {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.diff-key {
  flex: 1;
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.diff-body {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.diff-old,
.diff-new {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
}

.diff-old {
  color: var(--lme-text-secondary);
}

.diff-new {
  color: var(--lme-text-primary);
}

.diff-arrow {
  color: var(--lme-text-muted);
  flex-shrink: 0;
  font-size: var(--lme-font-size-xs);
}

.diff-deleted {
  color: var(--lme-state-deleted);
  font-style: italic;
}

/* ── 空态 / 加载态（与资源/静态工作台同一套） ── */
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
  line-height: var(--lme-line-height-relaxed);
  max-width: 280px;
}

/* ── 库组件聚焦环（沿用 v2 聚焦阴影令牌） ── */
:deep(.n-button:focus-visible),
:deep(.n-input:focus-visible),
:deep(.n-base-selection:focus-visible) {
  outline: none;
  box-shadow: var(--lme-shadow-focus);
}
</style>

<!--
  ── 与 WPF 旧界面 TextWorkbenchPage 的能力对照说明 ──

  等价能力：
  - 左侧文件树（懒展开）与搜索结果
  - 语言/文件类型筛选
  - 键值编辑表格（值可内联修改）
  - 文件内搜索（键名/值匹配高亮）
  - 差异视图（修改 vs 原始值对比）
  - 保存补丁（text.applyPatch → 注册为替换资源）
  - 服务端分页（滚动加载更多）
  - 虚拟滚动（复用 VirtualList）

  依赖的 IPC 方法：
  - text.fileTreeRoots    → 目录树根节点
  - text.fileTreeChildren → 目录树子节点（懒加载）
  - text.fileEntries      → 键值条目分页查询
  - text.fileSearch       → 全文搜索（文件路径 / 键 / 值）
  - text.applyPatch       → 保存补丁到替换资源

  未实现降级：
  - IPC 方法不可用时在页头下方显示 NAlert 错误条（原为原生 alert）
-->
