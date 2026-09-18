<script setup lang="ts">
// 项目管理页
// 当前项目信息 + 项目操作（新建/打开/保存/重新扫描）+ 最近项目 + 来源与编辑历史
//
// 2026-09-18 UI 重构：
//  · 去 emoji（图标走 AppIcon）；加 PageHeader 说明「这页能做什么」与下一步建议
//  · 新建项目补上「模组名称」输入（后端 project.create 本来就收 Name，此前没传）
//  · 新增「重新扫描游戏资源」—— 用既有 scan.run，进度走全局状态栏
//  · 进页先读 config.read('lastProjectFile')，让「当前项目」在未手动打开时也有真实内容
//  · 三态统一 StateBlock；反馈接全局状态 store
// IPC 方法名与载荷字段一律沿用既有契约。

import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NInput,
  NList,
  NListItem,
  NTag,
  NTooltip,
  useMessage,
} from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import StateBlock from '@/components/StateBlock.vue'
import { useStatusStore } from '@/stores/status'

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()
const status = useStatusStore()
const router = useRouter()

// ── 当前项目信息 ──
interface CurrentProject {
  name: string
  path: string
  assetCount: number | null
  directory: string
}

const currentProject = ref<CurrentProject | null>(null)

// ── 最近项目 ──
interface RecentProject {
  name: string
  path: string
  lastOpened: string
}

const recentProjects = ref<RecentProject[]>([])
const recentInfo = ref('')

// ── 项目来源 / 编辑历史 ──
// 后端 project.open 只回 { ok, name, assetCount }，不返回来源与历史；
// 保持为空并在界面上如实说明，不编造数据。
interface ProjectSource {
  id: string
  name: string
  type: string
  path: string
  assetCount: number
}

interface EditHistoryItem {
  id: string
  timestamp: string
  type: 'replacement' | 'field-edit' | 'sprite-metadata'
  assetName: string
  detail: string
}

const projectSources = ref<ProjectSource[]>([])
const editHistory = ref<EditHistoryItem[]>([])

// ── 状态 ──
const isLoading = ref(false)
const errorMessage = ref('')
const newProjectName = ref('')
const scanSteps = ref<{ key: string; label: string; status: string; detail: string }[]>([])
const lastScanSummary = ref('')

const scanRunning = computed(() => status.activities.some((a) => a.id === 'scan'))

function formatTime(timestamp: string): string {
  if (!timestamp) return '—'
  try {
    return new Date(timestamp).toLocaleString('zh-CN')
  } catch {
    return timestamp
  }
}

function basename(path: string): string {
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1] || path
}

// ── 方法 ──

/** 新建项目：把用户填的模组名传给 project.create（此前调用时没传 Name） */
async function newProject() {
  const name = newProjectName.value.trim()
  if (!name) {
    message.warning('请先填写模组名称')
    return
  }
  try {
    const result = await ipc.request<{ path: string; name: string; directory: string }>(
      'project.create',
      { name },
    )
    newProjectName.value = ''
    await loadProject(result.path, result.name, result.directory)
    status.notify('success', `已新建项目：${result.name}`)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : String(e)
    errorMessage.value = msg
    status.notify('error', `新建项目失败：${msg}`)
  }
}

/** 打开项目：宿主原生文件对话框 */
async function openProject() {
  try {
    // 契约方法 dialog.openFile：filters 是 Win32 过滤串（不是数组）
    const result = await ipc.request<{ path?: string }>('dialog.openFile', {
      title: '打开项目',
      filters: 'Limbus 模组项目 (*.lmeproj)|*.lmeproj|所有文件 (*.*)|*.*',
    })
    if (result?.path) await loadProject(result.path)
  } catch {
    // 用户取消或对话框失败：不报错（与既有行为一致）
  }
}

async function saveProject() {
  if (!currentProject.value) return
  const file = currentProject.value.path
  try {
    await ipc.request('project.save', { projectFile: file })
    status.notify('success', `项目已保存：${currentProject.value.name}`)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : String(e)
    status.notify('error', `保存项目失败：${msg}`)
  }
}

async function loadProject(path: string, knownName?: string, knownDirectory?: string) {
  isLoading.value = true
  errorMessage.value = ''
  try {
    // 契约方法 project.open：载荷 { projectFile }，响应 { ok, name, assetCount }
    const result = await ipc.request<{ ok: boolean; name: string; assetCount: number }>(
      'project.open',
      { projectFile: path },
    )
    currentProject.value = {
      name: knownName || result.name || basename(path),
      path,
      assetCount: typeof result.assetCount === 'number' ? result.assetCount : null,
      directory: knownDirectory ?? '',
    }
    // 同步到全局状态栏
    status.setProject(currentProject.value.name, path)
    await loadRecentProjects()
  } catch (e: unknown) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
    status.notify('error', `打开项目失败：${errorMessage.value}`)
  } finally {
    isLoading.value = false
  }
}

async function loadRecentProjects() {
  try {
    const result = await ipc.request<{ projects?: RecentProject[]; info?: string }>(
      'project.recent',
      {},
    )
    recentProjects.value = result?.projects ?? []
    recentInfo.value = result?.info ?? ''
  } catch {
    recentProjects.value = []
  }
}

/**
 * 重新扫描游戏资源（既有 scan.run，scope=all 会连带刷新音频/静态/文本索引）。
 * 进度由宿主 progress 事件回传，状态 store 会把它显示在底部状态栏。
 */
async function rescan() {
  if (scanRunning.value) return
  const operationId = 'scan'
  scanSteps.value = []
  lastScanSummary.value = ''
  status.beginActivity(operationId, '正在扫描游戏资源', '准备中')
  try {
    const res = await ipc.request<{
      scope: string
      status: string
      detail: string
      bundleCount: number
      assetCount: number
      elapsedSeconds: number
      cacheDirectory: string
      steps?: { key: string; label: string; status: string; detail: string }[]
      info?: string
    }>('scan.run', { scope: 'all', operationId }, 600000)

    scanSteps.value = res?.steps ?? []
    lastScanSummary.value =
      `扫描完成：${res?.assetCount ?? 0} 条资源 / ${res?.bundleCount ?? 0} 个 bundle` +
      `，用时 ${(res?.elapsedSeconds ?? 0).toFixed(1)} 秒`
    status.notify(
      'success',
      `${lastScanSummary.value}（缓存目录：${res?.cacheDirectory ?? '—'}）`,
    )
    // 扫描会改变项目资源数，重开一次拿最新计数
    if (currentProject.value) await loadProject(currentProject.value.path)
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : String(e)
    errorMessage.value = msg
    status.notify('error', `扫描失败：${msg}`)
  } finally {
    status.endActivity(operationId)
  }
}

function openRecentProject(path: string) {
  void loadProject(path)
}

// ── 初始化 ──
onMounted(async () => {
  await loadRecentProjects()

  // 上次打开的项目：读共享配置里的 lastProjectFile，让本页在未手动打开时也有真实内容。
  // 只读展示，不触发 project.open（避免页面加载产生副作用）。
  try {
    const res = await ipc.request<{ value?: string }>('config.read', { key: 'lastProjectFile' })
    const path = res?.value
    if (path && !currentProject.value) {
      currentProject.value = {
        name: basename(path).replace(/\.lmeproj$/i, ''),
        path,
        assetCount: null,
        directory: '',
      }
      status.setProject(currentProject.value.name, path)
    }
  } catch {
    // 读不到就算了，界面照常显示「未打开项目」
  }
})
</script>

<template>
  <div class="project-view">
    <PageHeader
      icon="project"
      title="项目管理"
      description="新建或打开一个模组项目，保存进度，并在游戏更新后重新扫描资源索引"
      hint="第一次使用：先在下面填一个模组名称并「新建项目」，再到「设置」确认游戏目录，然后点「重新扫描游戏资源」"
      hint-key="project"
    >
      <template #meta>
        <NTag v-if="currentProject" size="small" :bordered="false" type="success">
          已打开
        </NTag>
        <NTag v-else size="small" :bordered="false">未打开项目</NTag>
      </template>
    </PageHeader>

    <div class="project-scroll">
      <div class="project-container">
        <NAlert
          v-if="errorMessage"
          class="error-banner"
          type="error"
          :closable="true"
          title="操作失败"
          @close="errorMessage = ''"
        >
          {{ errorMessage }}
        </NAlert>

        <!-- ── 当前项目 ── -->
        <NCard class="project-section" size="small" title="当前项目">
          <NDescriptions
            v-if="currentProject"
            class="project-info-grid"
            :column="2"
            size="small"
            label-placement="top"
            bordered
          >
            <NDescriptionsItem label="名称">
              <span class="info-value">{{ currentProject.name }}</span>
            </NDescriptionsItem>
            <NDescriptionsItem label="资源数量">
              <span class="info-value">
                {{ currentProject.assetCount === null ? '—' : currentProject.assetCount.toLocaleString('zh-CN') }}
              </span>
            </NDescriptionsItem>
            <NDescriptionsItem label="项目文件" :span="2">
              <span class="info-value lme-mono">{{ currentProject.path }}</span>
            </NDescriptionsItem>
          </NDescriptions>

          <StateBlock
            v-else
            state="empty"
            icon="project"
            title="还没有打开项目"
            description="用下面的「新建项目」创建一个，或「打开项目」选择已有的 .lmeproj 文件；也可以在右侧最近项目里直接点一条"
          />
        </NCard>

        <!-- ── 项目操作 ── -->
        <NCard class="project-section" size="small" title="项目操作">
          <div class="action-block">
            <div class="new-project-row">
              <NInput
                v-model:value="newProjectName"
                class="new-project-input"
                size="small"
                clearable
                placeholder="新模组的名称，例如「我的汉化补丁」"
                @keyup.enter="newProject"
              >
                <template #prefix>
                  <AppIcon name="edit" :size="13" />
                </template>
              </NInput>
              <NButton size="small" type="primary" @click="newProject">
                <template #icon><AppIcon name="fileAdd" :size="13" /></template>
                新建项目
              </NButton>
            </div>

            <div class="action-row">
              <NButton size="small" @click="openProject">
                <template #icon><AppIcon name="folderOpen" :size="13" /></template>
                打开项目…
              </NButton>

              <NTooltip :show-arrow="false" placement="bottom">
                <template #trigger>
                  <NButton size="small" :disabled="!currentProject" @click="saveProject">
                    <template #icon><AppIcon name="save" :size="13" /></template>
                    保存项目
                  </NButton>
                </template>
                把当前项目的编辑清单写回 .lmeproj 文件
              </NTooltip>

              <NTooltip :show-arrow="false" placement="bottom">
                <template #trigger>
                  <NButton size="small" :loading="scanRunning" @click="rescan">
                    <template #icon><AppIcon name="refresh" :size="13" /></template>
                    重新扫描游戏资源
                  </NButton>
                </template>
                游戏更新或改了缓存目录后点这里；会刷新资源索引与音频/文本/静态数据索引
              </NTooltip>
            </div>

            <!-- 扫描结果 -->
            <div v-if="lastScanSummary || scanSteps.length > 0" class="scan-result">
              <div v-if="lastScanSummary" class="scan-summary">
                <AppIcon name="success" :size="13" />
                <span>{{ lastScanSummary }}</span>
              </div>
              <ul v-if="scanSteps.length > 0" class="scan-steps">
                <li v-for="s in scanSteps" :key="s.key" class="scan-step">
                  <AppIcon
                    :name="s.status === 'Failed' ? 'error' : s.status === 'Skipped' ? 'warning' : 'success'"
                    :size="12"
                  />
                  <span class="scan-step-label">{{ s.label }}</span>
                  <span class="scan-step-detail lme-ellipsis" :title="s.detail">{{ s.detail }}</span>
                </li>
              </ul>
            </div>
          </div>
        </NCard>

        <!-- ── 最近项目 ── -->
        <NCard class="project-section" size="small" title="最近项目">
          <NList v-if="recentProjects.length > 0" class="recent-list" hoverable clickable>
            <NListItem
              v-for="project in recentProjects"
              :key="project.path"
              class="recent-item"
              @click="openRecentProject(project.path)"
            >
              <div class="recent-row">
                <AppIcon name="project" :size="14" class="recent-icon" />
                <span class="recent-name">{{ project.name }}</span>
                <span class="recent-path lme-mono lme-ellipsis">{{ project.path }}</span>
                <span class="recent-time">{{ formatTime(project.lastOpened) }}</span>
              </div>
            </NListItem>
          </NList>

          <StateBlock
            v-else
            state="empty"
            icon="clock"
            title="暂无最近项目"
            description="新建或打开一个项目后，它会出现在这里，方便下次一键回到工作现场"
          />
          <div v-if="recentInfo" class="section-note">{{ recentInfo }}</div>
        </NCard>

        <!-- ── 项目来源 ── -->
        <NCard class="project-section" size="small" title="项目来源">
          <NList v-if="projectSources.length > 0" class="source-list">
            <NListItem v-for="source in projectSources" :key="source.id" class="source-item">
              <div class="source-row">
                <span class="source-name">{{ source.name }}</span>
                <NTag class="source-type" size="small" :bordered="false">{{ source.type }}</NTag>
                <span class="source-path lme-mono lme-ellipsis">{{ source.path }}</span>
                <span class="source-count">{{ source.assetCount }} 项</span>
              </div>
            </NListItem>
          </NList>
          <StateBlock
            v-else
            state="empty"
            icon="layers"
            title="没有来源记录"
            description="导入的模组包会登记在这里。当前后端尚未提供来源清单接口，因此列表恒为空——这不是出错"
          />
        </NCard>

        <!-- ── 编辑历史 ── -->
        <NCard class="project-section" size="small" title="编辑历史">
          <NList v-if="editHistory.length > 0" class="history-list">
            <NListItem v-for="item in editHistory" :key="item.id" class="history-item">
              <div class="history-row">
                <span class="history-time lme-mono">{{ formatTime(item.timestamp) }}</span>
                <span class="history-type">{{ item.type }}</span>
                <span class="history-asset">{{ item.assetName }}</span>
                <span class="history-detail lme-ellipsis">{{ item.detail }}</span>
              </div>
            </NListItem>
          </NList>
          <StateBlock
            v-else
            state="empty"
            icon="clock"
            title="没有编辑记录"
            description="编辑历史尚未由后端提供接口，因此列表恒为空。想看当前项目的改动，请到「导出」页生成导出方案"
          >
            <template #actions>
              <NButton size="small" @click="router.push('/export')">
                <template #icon><AppIcon name="export" :size="13" /></template>
                去导出页看改动
              </NButton>
            </template>
          </StateBlock>
        </NCard>
      </div>
    </div>
  </div>
</template>

<style scoped>
.project-view {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--lme-bg-base);
}

.project-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
}

.project-container {
  max-width: 980px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
}

/* ── 分区（NCard 容器，内边距由卡片给定） ── */
.project-section {
  display: block;
}

/* ── 当前项目信息 ── */
.project-info-grid {
  width: 100%;
}

.info-value {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  word-break: break-all;
}

/* ── 错误提示 ── */
.error-banner {
  font-size: var(--lme-font-size-sm);
}

/* ── 操作区 ── */
.action-block {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.new-project-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.new-project-input {
  flex: 1;
  min-width: 0;
  max-width: 420px;
}

.action-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

/* ── 扫描结果 ── */
.scan-result {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-inset);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.scan-summary {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-success);
}

.scan-steps {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.scan-step {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

.scan-step-label {
  flex-shrink: 0;
  min-width: 110px;
  color: var(--lme-text-primary);
}

.scan-step-detail {
  min-width: 0;
  color: var(--lme-text-muted);
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

.recent-icon {
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.recent-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-medium);
  min-width: 120px;
}

.recent-path {
  flex: 1;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  min-width: 0;
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
  font-weight: var(--lme-font-weight-medium);
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
  font-weight: var(--lme-font-weight-medium);
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
}

/* ── 分区脚注 ── */
.section-note {
  margin-top: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  line-height: var(--lme-line-height-relaxed);
}
</style>
