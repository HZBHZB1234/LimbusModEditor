<script setup lang="ts">
// 项目页面
// 当前项目信息 + 最近项目 + 项目操作 + 来源列表 + 编辑历史

import { ref, onMounted } from 'vue'
import { ipc } from '@/ipc'

// ── 当前项目信息 ──
interface CurrentProject {
  name: string
  path: string
  assetCount: number
  lastModified: string
}

const currentProject = ref<CurrentProject | null>(null)

// ── 最近项目 ──
interface RecentProject {
  name: string
  path: string
  lastOpened: string
}

const recentProjects = ref<RecentProject[]>([])

// ── 项目来源 ──
interface ProjectSource {
  id: string
  name: string
  type: string
  path: string
  assetCount: number
}

const projectSources = ref<ProjectSource[]>([])

// ── 编辑历史 ──
interface EditHistoryItem {
  id: string
  timestamp: string
  type: 'replacement' | 'field-edit' | 'sprite-metadata'
  assetName: string
  detail: string
}

const editHistory = ref<EditHistoryItem[]>([])

// ── 状态 ──
const isLoading = ref(false)
const errorMessage = ref('')

// ── 方法 ──
async function newProject() {
  try {
    const result = await ipc.request<{ path: string }>('project.create', {})
    await loadProject(result.path)
  } catch {
    // 暂未实现：新建项目失败
  }
}

async function openProject() {
  try {
    // 契约方法 dialog.openFile：filters 是 Win32 过滤串（不是数组）
    const result = await ipc.request<{ path: string }>('dialog.openFile', {
      title: '打开项目',
      filters: 'Limbus 模组项目 (*.lme)|*.lme|所有文件 (*.*)|*.*',
    })
    if (result.path) {
      await loadProject(result.path)
    }
  } catch {
    // 暂未实现：打开项目取消或失败
  }
}

async function saveProject() {
  if (!currentProject.value) return
  try {
    await ipc.request('project.save', { projectFile: currentProject.value.path })
  } catch {
    // 暂未实现：保存项目失败
  }
}

async function loadProject(path: string) {
  isLoading.value = true
  errorMessage.value = ''
  try {
    // 契约方法 project.open：载荷 { projectFile }，响应 { ok, name, assetCount }
    // 后端不返回 sources / history，保持为空（不编造）
    const result = await ipc.request<{ ok: boolean; name: string; assetCount: number }>(
      'project.open',
      { projectFile: path },
    )
    currentProject.value = {
      name: result.name,
      path,
      assetCount: result.assetCount,
      lastModified: '',
    }
    await loadRecentProjects()
  } catch (e: unknown) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    isLoading.value = false
  }
}

async function loadRecentProjects() {
  try {
    const result = await ipc.request<{ projects: RecentProject[] }>('project.recent', {})
    recentProjects.value = result.projects
  } catch {
    // 暂未实现：加载最近项目失败
  }
}

function openRecentProject(path: string) {
  loadProject(path)
}

function formatTime(timestamp: string): string {
  try {
    const date = new Date(timestamp)
    return date.toLocaleString('zh-CN')
  } catch {
    return timestamp
  }
}

function historyTypeLabel(type: string): string {
  const map: Record<string, string> = {
    'replacement': '替换',
    'field-edit': '字段编辑',
    'sprite-metadata': '精灵元数据',
  }
  return map[type] ?? type
}

function historyTypeColor(type: string): string {
  const map: Record<string, string> = {
    'replacement': 'var(--lme-state-modified)',
    'field-edit': 'var(--lme-info)',
    'sprite-metadata': 'var(--lme-accent)',
  }
  return map[type] ?? 'var(--lme-text-secondary)'
}

// ── 初始化 ──
onMounted(async () => {
  await loadRecentProjects()
})
</script>

<template>
  <div class="project-view">
    <div class="project-container">
      <h2 class="project-title">项目</h2>

      <!-- 当前项目信息 -->
      <section class="project-section">
        <h3 class="section-title">当前项目</h3>
        <div v-if="currentProject" class="current-project">
          <div class="project-info-grid">
            <div class="info-item">
              <span class="info-label">名称</span>
              <span class="info-value">{{ currentProject.name }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">路径</span>
              <span class="info-value lme-mono">{{ currentProject.path }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">资源数量</span>
              <span class="info-value">{{ currentProject.assetCount.toLocaleString('zh-CN') }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">最后修改</span>
              <span class="info-value">{{ formatTime(currentProject.lastModified) }}</span>
            </div>
          </div>
        </div>
        <div v-else class="no-project">
          <p>未打开项目</p>
        </div>
      </section>

      <!-- 项目操作 -->
      <section class="project-section">
        <h3 class="section-title">项目操作</h3>
        <div class="project-actions">
          <button class="btn btn-primary" @click="newProject">
            📄 新建项目
          </button>
          <button class="btn btn-secondary" @click="openProject">
            📂 打开项目
          </button>
          <button
            class="btn btn-secondary"
            :disabled="!currentProject"
            @click="saveProject"
          >
            💾 保存项目
          </button>
        </div>
        <div v-if="errorMessage" class="error-banner">
          ⚠️ {{ errorMessage }}
        </div>
      </section>

      <!-- 最近项目 -->
      <section class="project-section">
        <h3 class="section-title">最近项目</h3>
        <div v-if="recentProjects.length > 0" class="recent-list">
          <div
            v-for="project in recentProjects"
            :key="project.path"
            class="recent-item"
            @click="openRecentProject(project.path)"
          >
            <span class="recent-name">{{ project.name }}</span>
            <span class="recent-path lme-mono">{{ project.path }}</span>
            <span class="recent-time">{{ formatTime(project.lastOpened) }}</span>
          </div>
        </div>
        <div v-else class="empty-state">
          <p>暂无最近项目</p>
        </div>
      </section>

      <!-- 项目来源 -->
      <section class="project-section">
        <h3 class="section-title">项目来源</h3>
        <div v-if="projectSources.length > 0" class="source-list">
          <div
            v-for="source in projectSources"
            :key="source.id"
            class="source-item"
          >
            <span class="source-name">{{ source.name }}</span>
            <span class="source-type">{{ source.type }}</span>
            <span class="source-path lme-mono">{{ source.path }}</span>
            <span class="source-count">{{ source.assetCount }} 项</span>
          </div>
        </div>
        <div v-else class="empty-state">
          <p v-if="currentProject">暂无项目来源</p>
          <p v-else>打开项目后查看来源列表</p>
        </div>
      </section>

      <!-- 编辑历史 -->
      <section class="project-section">
        <h3 class="section-title">编辑历史</h3>
        <div v-if="editHistory.length > 0" class="history-list">
          <div
            v-for="item in editHistory"
            :key="item.id"
            class="history-item"
          >
            <span class="history-time lme-mono">{{ formatTime(item.timestamp) }}</span>
            <span
              class="history-type"
              :style="{ color: historyTypeColor(item.type) }"
            >
              {{ historyTypeLabel(item.type) }}
            </span>
            <span class="history-asset">{{ item.assetName }}</span>
            <span class="history-detail">{{ item.detail }}</span>
          </div>
        </div>
        <div v-else class="empty-state">
          <p v-if="currentProject">暂无编辑历史</p>
          <p v-else>打开项目后查看编辑历史</p>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.project-view {
  height: 100%;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
  background: var(--lme-bg-base);
}

.project-container {
  max-width: 900px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

.project-title {
  margin: 0;
  font-size: var(--lme-font-size-xl);
  color: var(--lme-text-primary);
}

/* ── 分区 ── */
.project-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.section-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-primary);
}

/* ── 当前项目信息 ── */
.current-project {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.project-info-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: var(--lme-gap-md);
}

.info-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.info-label {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.info-value {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  word-break: break-all;
}

.no-project {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  text-align: center;
  padding: var(--lme-gap-lg);
}

/* ── 操作按钮 ── */
.project-actions {
  display: flex;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.btn-primary {
  background: var(--lme-accent);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.btn-primary:hover {
  background: var(--lme-accent-hover);
}

.btn-secondary {
  background: var(--lme-bg-elevated);
  color: var(--lme-text-secondary);
}

.btn-secondary:hover {
  background: var(--lme-bg-hover);
}

.btn-secondary:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ── 错误提示 ── */
.error-banner {
  padding: var(--lme-gap-md);
  background: var(--lme-error-banner-bg);
  border: 1px solid var(--lme-error);
  border-radius: var(--lme-radius-md);
  color: var(--lme-error);
  font-size: var(--lme-font-size-sm);
}

/* ── 最近项目 ── */
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
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  transition: background 0.15s;
}

.recent-item:hover {
  background: var(--lme-bg-hover);
}

.recent-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
  min-width: 120px;
}

.recent-path {
  flex: 1;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.recent-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}

/* ── 来源列表 ── */
.source-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.source-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-sm);
  background: var(--lme-bg-elevated);
}

.source-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
  min-width: 100px;
}

.source-type {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-accent);
  padding: 1px 6px;
  background: var(--lme-accent-muted);
  border-radius: var(--lme-radius-sm);
  min-width: 60px;
  text-align: center;
}

.source-path {
  flex: 1;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.source-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  flex-shrink: 0;
}

/* ── 编辑历史 ── */
.history-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  max-height: 400px;
  overflow-y: auto;
}

.history-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-sm);
  background: var(--lme-bg-elevated);
}

.history-time {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
  min-width: 140px;
}

.history-type {
  font-size: var(--lme-font-size-xs);
  font-weight: 500;
  min-width: 70px;
  text-align: center;
  padding: 1px 6px;
  border-radius: var(--lme-radius-sm);
}

.history-asset {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  min-width: 120px;
}

.history-detail {
  flex: 1;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 空状态 ── */
.empty-state {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  text-align: center;
  padding: var(--lme-gap-lg);
}
</style>
