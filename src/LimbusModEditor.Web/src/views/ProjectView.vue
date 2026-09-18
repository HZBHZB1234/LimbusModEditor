<script setup lang="ts">
// 项目页面
// 当前项目信息 + 最近项目 + 项目操作 + 来源列表 + 编辑历史

import { ref, onMounted } from 'vue'
import { ipc } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NEmpty,
  NList,
  NListItem,
  useMessage,
} from 'naive-ui'

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()

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
    message.success(`已新建项目：${result.path}`)
  } catch {
    // 暂未实现：新建项目失败
    message.error('新建项目失败')
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
    message.success(`项目已保存：${currentProject.value.name}`)
  } catch {
    // 暂未实现：保存项目失败
    message.error('保存项目失败')
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
    message.error(`打开项目失败：${errorMessage.value}`)
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
      <NCard class="project-section" size="small" title="当前项目">
        <div v-if="currentProject" class="current-project">
          <NDescriptions
            class="project-info-grid"
            :column="2"
            size="small"
            label-placement="top"
            bordered
          >
            <NDescriptionsItem label="名称">
              <span class="info-value">{{ currentProject.name }}</span>
            </NDescriptionsItem>
            <NDescriptionsItem label="路径">
              <span class="info-value lme-mono">{{ currentProject.path }}</span>
            </NDescriptionsItem>
            <NDescriptionsItem label="资源数量">
              <span class="info-value">{{ currentProject.assetCount.toLocaleString('zh-CN') }}</span>
            </NDescriptionsItem>
            <NDescriptionsItem label="最后修改">
              <span class="info-value">{{ formatTime(currentProject.lastModified) }}</span>
            </NDescriptionsItem>
          </NDescriptions>
        </div>
        <div v-else class="no-project">
          <NEmpty size="small" description="未打开项目">
            <template #extra>
              <span class="state-hint">用下方「打开项目」选择 .lme 文件，或「新建项目」</span>
            </template>
          </NEmpty>
        </div>
      </NCard>

      <!-- 项目操作 -->
      <NCard class="project-section" size="small" title="项目操作">
        <div class="project-actions">
          <NButton class="btn btn-primary" type="primary" size="small" @click="newProject">
            📄 新建项目
          </NButton>
          <NButton class="btn btn-secondary" size="small" @click="openProject">
            📂 打开项目
          </NButton>
          <NButton
            class="btn btn-secondary"
            size="small"
            :disabled="!currentProject"
            @click="saveProject"
          >
            💾 保存项目
          </NButton>
        </div>
        <NAlert
          v-if="errorMessage"
          class="error-banner"
          type="error"
          :closable="false"
          title="项目操作失败"
        >
          {{ errorMessage }}
        </NAlert>
      </NCard>

      <!-- 最近项目 -->
      <NCard class="project-section" size="small" title="最近项目">
        <NList v-if="recentProjects.length > 0" class="recent-list" hoverable clickable>
          <NListItem
            v-for="project in recentProjects"
            :key="project.path"
            class="recent-item"
            @click="openRecentProject(project.path)"
          >
            <div class="recent-row">
              <span class="recent-name">{{ project.name }}</span>
              <span class="recent-path lme-mono">{{ project.path }}</span>
              <span class="recent-time">{{ formatTime(project.lastOpened) }}</span>
            </div>
          </NListItem>
        </NList>
        <div v-else class="empty-state">
          <NEmpty size="small" description="暂无最近项目" />
        </div>
      </NCard>

      <!-- 项目来源 -->
      <NCard class="project-section" size="small" title="项目来源">
        <NList v-if="projectSources.length > 0" class="source-list">
          <NListItem
            v-for="source in projectSources"
            :key="source.id"
            class="source-item"
          >
            <div class="source-row">
              <span class="source-name">{{ source.name }}</span>
              <NTag class="source-type" size="small" :bordered="false">{{ source.type }}</NTag>
              <span class="source-path lme-mono">{{ source.path }}</span>
              <span class="source-count">{{ source.assetCount }} 项</span>
            </div>
          </NListItem>
        </NList>
        <div v-else class="empty-state">
          <NEmpty
            size="small"
            :description="currentProject ? '暂无项目来源' : '打开项目后查看来源列表'"
          />
        </div>
      </NCard>

      <!-- 编辑历史 -->
      <NCard class="project-section" size="small" title="编辑历史">
        <NList v-if="editHistory.length > 0" class="history-list">
          <NListItem
            v-for="item in editHistory"
            :key="item.id"
            class="history-item"
          >
            <div class="history-row">
              <span class="history-time lme-mono">{{ formatTime(item.timestamp) }}</span>
              <span class="history-type" :style="{ color: historyTypeColor(item.type) }">
                {{ historyTypeLabel(item.type) }}
              </span>
              <span class="history-asset">{{ item.assetName }}</span>
              <span class="history-detail">{{ item.detail }}</span>
            </div>
          </NListItem>
        </NList>
        <div v-else class="empty-state">
          <NEmpty
            size="small"
            :description="currentProject ? '暂无编辑历史' : '打开项目后查看编辑历史'"
          />
        </div>
      </NCard>
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

/* ── 分区（NCard 容器，内边距由卡片给定） ── */
.project-section {
  display: block;
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
  width: 100%;
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
  margin: calc(-1 * var(--lme-gap-sm)) 0;
}

.recent-item {
  padding: 0;
  cursor: pointer;
}

.recent-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  width: 100%;
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
  margin: calc(-1 * var(--lme-gap-sm)) 0;
}

.source-item {
  padding: 0;
}

.source-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  width: 100%;
}

.source-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
  min-width: 100px;
}

.source-type {
  min-width: 60px;
  text-align: center;
  flex-shrink: 0;
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
  max-height: 400px;
  overflow-y: auto;
  margin: calc(-1 * var(--lme-gap-sm)) 0;
}

.history-item {
  padding: 0;
}

.history-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  width: 100%;
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
  flex-shrink: 0;
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
