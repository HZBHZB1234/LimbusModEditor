<script setup lang="ts">
// 文本/本地化工作台页面（TextView）
// 三栏布局：浏览 | 分隔条 | 编辑
// 对应 WPF 旧界面 TextWorkbenchPage
// IPC 方法：text.fileTreeRoots / text.fileTreeChildren / text.fileEntries / text.applyPatch / text.fileSearch
// 未实现的 IPC 方法降级为「暂未实现」提示

import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import VirtualList from '@/components/VirtualList.vue'

const route = useRoute()
const router = useRouter()

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
  } catch {
    // IPC 方法未实现时显示提示
    alert('暂未实现')
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
    <!-- 左栏：浏览 -->
    <div class="browse-column">
      <!-- 搜索框 -->
      <div class="search-box-row">
        <input
          v-model="searchText"
          class="search-input"
          type="text"
          placeholder="搜索文件路径或键/值文本…"
          @input="onSearchInput"
        />
      </div>

      <!-- 筛选行 -->
      <div class="filter-row">
        <label class="filter-label">语言</label>
        <select v-model="languageFilter" class="filter-select">
          <option value="">全部语言</option>
          <option value="zh-CN">简体中文 (zh-CN)</option>
          <option value="en-US">English (en-US)</option>
          <option value="ja-JP">日本語 (ja-JP)</option>
          <option value="ko-KR">한국어 (ko-KR)</option>
        </select>

        <label class="filter-label">类型</label>
        <select v-model="fileTypeFilter" class="filter-select">
          <option value="">全部类型</option>
          <option value="json">JSON</option>
          <option value="yaml">YAML</option>
          <option value="csv">CSV</option>
          <option value="po">PO (gettext)</option>
        </select>
      </div>

      <!-- 文件树 / 搜索结果 -->
      <div class="file-tree-container">
        <div v-if="fileTreeLoading || searchLoading" class="tree-loading">
          <span class="loading-spinner">⏳</span>
          <span>加载中…</span>
        </div>

        <!-- 搜索结果列表 -->
        <template v-else-if="searchResults.length > 0">
          <div class="results-header">
            搜索命中 {{ searchResults.length }} 个文件
          </div>
          <div
            v-for="node in searchResults"
            :key="node.path"
            class="file-result-item"
            @click="node.isLeaf && selectFileNode(node)"
          >
            <div class="result-path lme-mono">{{ node.path }}</div>
            <div class="result-meta">
              <span class="meta-tag language-tag">{{ node.language }}</span>
              <span v-if="node.entryCount > 0" class="meta-tag count-tag">{{ node.entryCount }} 条</span>
            </div>
          </div>
          <div v-if="searchResults.length === 0 && searchText.trim()" class="tree-empty">
            <div class="empty-icon">🔍</div>
            <div class="empty-title">未找到匹配文件</div>
            <div class="empty-desc">尝试调整搜索关键词或清除筛选条件</div>
          </div>
        </template>

        <!-- 目录树 -->
        <template v-else>
          <template v-for="node in fileTreeRoots" :key="node.path">
            <div class="tree-node-entry">
              <div
                class="tree-node-row"
                :class="{ leaf: node.isLeaf, expanded: node.expanded }"
                :style="{ paddingLeft: node.depth * 16 + 8 + 'px' }"
                @click="toggleFileNode(node)"
              >
                <span class="tree-arrow">
                  {{ node.isLeaf ? '•' : node.loading ? '⏳' : node.expanded ? '▼' : '▶' }}
                </span>
                <span class="tree-node-name">{{ node.name }}</span>
                <span v-if="node.isLeaf" class="tree-count">{{ node.entryCount }}</span>
                <span v-else class="tree-count">{{ node.children?.length ?? 0 }}</span>
              </div>

              <!-- 子节点（展开时渲染） -->
              <template v-if="node.expanded && node.children">
                <div
                  v-for="child in node.children"
                  :key="child.path"
                  class="tree-node-row"
                  :class="{ leaf: child.isLeaf, expanded: child.expanded }"
                  :style="{ paddingLeft: (child.depth) * 16 + 8 + 'px' }"
                  @click="toggleFileNode(child)"
                >
                  <span class="tree-arrow">
                    {{ child.isLeaf ? '•' : child.loading ? '⏳' : child.expanded ? '▼' : '▶' }}
                  </span>
                  <span class="tree-node-name">{{ child.name }}</span>
                  <span v-if="child.isLeaf" class="tree-count">{{ child.entryCount }}</span>
                  <span v-else class="tree-count">{{ child.children?.length ?? 0 }}</span>
                </div>
              </template>
            </div>
          </template>

          <!-- 空状态 -->
          <div v-if="fileTreeRoots.length === 0" class="tree-empty">
            <div class="empty-icon">📝</div>
            <div class="empty-title">暂无语言文件</div>
            <div class="empty-desc">
              未找到可用的本地化语言文件。请确认项目已包含 .json / .yaml 等文本资源。
            </div>
          </div>
        </template>
      </div>
    </div>

    <!-- 分隔条 -->
    <div class="column-splitter" />

    <!-- 右栏：编辑 -->
    <div v-if="selectedFilePath" class="edit-column">
      <!-- 文件头 -->
      <div class="edit-header">
        <div class="header-title">
          编辑语言文件
        </div>
        <div class="header-path lme-mono" :title="selectedFilePath">
          {{ selectedFilePath }}
        </div>
        <div class="header-meta">
          <span class="meta-tag language-tag">{{ selectedFileLanguage }}</span>
          <span class="meta-tag count-tag">{{ entries.length }} 条</span>
          <span v-if="modifiedCount > 0" class="meta-tag modified-tag">
            已修改 {{ modifiedCount }}
          </span>
          <span v-if="addedCount > 0" class="meta-tag added-tag">
            已添加 {{ addedCount }}
          </span>
          <span v-if="deletedCount > 0" class="meta-tag deleted-tag">
            已删除 {{ deletedCount }}
          </span>
        </div>
      </div>

      <!-- 编辑工具栏 -->
      <div class="edit-toolbar">
        <input
          v-model="inlineSearchText"
          class="inline-search-input"
          type="text"
          placeholder="搜索键名或值内容…"
        />
        <label class="diff-toggle">
          <input type="checkbox" v-model="showDiffView" />
          差异视图
        </label>
        <button
          class="tool-btn save-btn"
          :disabled="saving || changedEntries.length === 0"
          @click="saveAsPatch"
        >
          {{ saving ? '保存中…' : '保存补丁' }}
        </button>
      </div>

      <!-- 键值编辑表 -->
      <div class="kv-list-wrapper">
        <!-- 表头 -->
        <div class="kv-list-header">
          <span class="kv-key-col">键名 ({{ entries.length }})</span>
          <span class="kv-value-col">值</span>
          <span class="kv-actions-col">操作</span>
        </div>

        <!-- 虚拟列表 -->
        <div class="kv-list-container">
          <div v-if="entriesLoading && entries.length === 0" class="entries-loading">
            <span class="loading-spinner">⏳</span>
            <span>加载条目…</span>
          </div>
          <div v-else-if="entries.length === 0" class="entries-empty">
            该文件无键值条目
          </div>
          <VirtualList
            v-else
            :items="entries"
            :item-height="32"
            :height="600"
            :overscan="8"
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
                <div class="kv-key-cell lme-mono" :title="item.key">
                  {{ item.key }}
                </div>
                <div class="kv-value-cell">
                  <input
                    v-if="item.editState !== 'Deleted'"
                    type="text"
                    :value="item.value"
                    class="value-input"
                    @input="
                      onValueEdit(item, ($event.target as HTMLInputElement).value)
                    "
                  />
                  <del v-else class="deleted-text">{{ item.originalValue }}</del>
                </div>
                <div class="kv-actions-cell">
                  <button
                    v-if="item.editState !== 'Unchanged'"
                    class="action-btn restore-btn"
                    title="恢复原值"
                    @click="restoreEntry(item)"
                  >
                    恢复
                  </button>
                  <button
                    v-if="item.editState !== 'Unchanged'"
                    class="action-btn delete-btn"
                    title="删除条目"
                    @click="deleteEntry(item)"
                  >
                    删除
                  </button>
                </div>
              </div>
            </template>
          </VirtualList>

          <!-- 分页加载提示 -->
          <div
            v-if="
              entriesOffset + entriesPageSize < entriesTotalCount
            "
            class="load-more-hint lme-mono"
          >
            向下滚动加载更多（{{ entries.length }} / {{ entriesTotalCount }}）
          </div>
        </div>
      </div>

      <!-- 差异视图 -->
      <div v-if="showDiffView" class="diff-view">
        <div class="diff-header">
          <span>差异预览</span>
          <span class="diff-count">{{ changedEntries.length }} 处变更</span>
        </div>

        <div v-if="changedEntries.length === 0" class="diff-empty">
          暂无修改——在编辑表格中修改值后，此处将显示差异
        </div>

        <div v-else class="diff-list">
          <div
            v-for="entry in changedEntries"
            :key="entry.id"
            class="diff-entry"
            :class="'diff-' + entry.editState.toLowerCase()"
          >
            <div class="diff-key lme-mono">{{ entry.key }}</div>
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
              <div v-else class="diff-new">
                {{ entry.value || '(空)' }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 空状态：未选择文件 -->
    <div v-else class="edit-column empty-edit">
      <div class="empty-state">
        <div class="empty-icon">📝</div>
        <div class="empty-title">未选择语言文件</div>
        <div class="empty-desc">
          请从左侧文件树中选择一个语言文件进行编辑，或通过搜索框快速定位键值文本。支持 JSON、YAML、CSV、PO 等格式。
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* ── 三栏布局 ── */
.text-view {
  display: flex;
  height: 100%;
  overflow: hidden;
}

/* ── 浏览列 ── */
.browse-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
}

.search-box-row {
  padding: var(--lme-gap-md);
  padding-bottom: var(--lme-gap-sm);
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
  padding: 0 var(--lme-gap-md) var(--lme-gap-md);
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

/* ── 文件树 ── */
.file-tree-container {
  flex: 1;
  overflow: auto;
  padding: var(--lme-gap-sm);
}

.tree-loading {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

.results-header {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding: var(--lme-gap-xs) 0;
  border-bottom: 1px solid var(--lme-border);
  margin-bottom: var(--lme-gap-sm);
}

.file-result-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  cursor: pointer;
  border-radius: var(--lme-radius-sm);
}

.file-result-item:hover {
  background: var(--lme-bg-hover);
}

.result-path {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  word-break: break-all;
}

.result-meta {
  display: flex;
  gap: var(--lme-gap-xs);
}

.tree-empty {
  padding: var(--lme-gap-xl);
  text-align: center;
  color: var(--lme-text-muted);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-sm);
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
  max-width: 280px;
  line-height: 1.5;
}

/* ── 树节点 ── */
.tree-node-entry {
  /* 递归树节点容器 */
}

.tree-node-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: 2px var(--lme-gap-sm);
  cursor: pointer;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.tree-node-row:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.tree-node-row.leaf {
  color: var(--lme-text-muted);
}

.tree-arrow {
  width: 14px;
  text-align: center;
  font-size: 10px;
  flex-shrink: 0;
}

.tree-node-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tree-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  background: var(--lme-bg-elevated);
  padding: 1px 6px;
  border-radius: 8px;
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
  min-width: 0;
  overflow: hidden;
  background: var(--lme-bg-panel);
  min-width: 320px;
}

.edit-header {
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.header-title {
  font-size: var(--lme-font-size-lg);
  font-weight: 600;
  color: var(--lme-text-primary);
}

.header-path {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  word-break: break-all;
}

.header-meta {
  display: flex;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.meta-tag {
  padding: 2px 8px;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
}

.language-tag {
  color: var(--lme-accent);
}

.count-tag {
  color: var(--lme-text-muted);
}

.modified-tag {
  color: var(--lme-state-modified);
  border-color: var(--lme-state-modified);
}

.added-tag {
  color: var(--lme-state-added);
  border-color: var(--lme-state-added);
}

.deleted-tag {
  color: var(--lme-state-deleted);
  border-color: var(--lme-state-deleted);
}

/* ── 编辑工具栏 ── */
.edit-toolbar {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  flex-wrap: wrap;
}

.inline-search-input {
  flex: 1;
  min-width: 140px;
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
}

.inline-search-input:focus {
  outline: none;
  border-color: var(--lme-accent);
}

.diff-toggle {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  white-space: nowrap;
}

.diff-toggle input[type='checkbox'] {
  accent-color: var(--lme-accent);
}

.tool-btn {
  padding: 3px 12px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.tool-btn:hover:not(:disabled) {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.tool-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.save-btn {
  background: var(--lme-accent-muted);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.save-btn:hover:not(:disabled) {
  background: var(--lme-accent);
  border-color: var(--lme-accent-hover);
  color: var(--lme-text-primary);
}

/* ── 键值列表 ── */
.kv-list-wrapper {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

.kv-list-header {
  display: flex;
  align-items: center;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-muted);
}

.kv-key-col {
  flex: 2;
}

.kv-value-col {
  flex: 3;
}

.kv-actions-col {
  flex: 0 0 90px;
  text-align: center;
}

.kv-list-container {
  flex: 1;
  position: relative;
  min-height: 0;
}

.entries-loading,
.entries-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  height: 120px;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

/* ── 键值行 ── */
.kv-row {
  display: flex;
  align-items: center;
  padding: 0 var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  border-left: 3px solid transparent;
  transition: background 0.1s;
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

.kv-key-cell {
  flex: 2;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kv-value-cell {
  flex: 3;
  min-width: 0;
}

.value-input {
  width: 100%;
  padding: 2px var(--lme-gap-xs);
  background: transparent;
  border: 1px solid transparent;
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
}

.value-input:hover {
  border-color: var(--lme-border);
}

.value-input:focus {
  outline: none;
  border-color: var(--lme-accent);
  background: var(--lme-bg-input);
}

.deleted-text {
  color: var(--lme-state-deleted);
  font-size: var(--lme-font-size-sm);
}

.kv-actions-cell {
  flex: 0 0 90px;
  display: flex;
  justify-content: center;
  gap: var(--lme-gap-xs);
}

.action-btn {
  padding: 1px 6px;
  background: transparent;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
  transition: all 0.1s;
}

.restore-btn:hover {
  border-color: var(--lme-info);
  color: var(--lme-info);
}

.delete-btn:hover {
  border-color: var(--lme-state-deleted);
  color: var(--lme-state-deleted);
}

.load-more-hint {
  padding: var(--lme-gap-sm);
  text-align: center;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 差异视图 ── */
.diff-view {
  border-top: 1px solid var(--lme-border);
  max-height: 280px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.diff-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.diff-count {
  font-weight: normal;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.diff-empty {
  padding: var(--lme-gap-xl);
  text-align: center;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

.diff-list {
  flex: 1;
  overflow: auto;
}

.diff-entry {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  border-left: 3px solid transparent;
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

.diff-key {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  margin-bottom: 2px;
}

.diff-body {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  font-size: var(--lme-font-size-sm);
}

.diff-old {
  flex: 1;
  color: var(--lme-text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
}

.diff-new {
  flex: 1;
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
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

/* ── 空状态 ── */
.empty-edit {
  justify-content: center;
  align-items: center;
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-xl);
  text-align: center;
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
  - IPC 方法不可用时显示「暂未实现」提示（alert）
-->
