<script setup lang="ts">
// 导出向导页面
// 三栏布局：格式矩阵 | 分隔条 | 预览/分析/进度/报告
// ui-redesign r6：emoji/几何符号换 AppIcon；页头换 PageHeader；空/错误态换 StateBlock；
//                 导出过程登记进 status store；硬编码色值收进令牌。export.run 调用与 progress 订阅未动

import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ipc } from '@/ipc'
import type { ProgressPayload } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NCheckbox,
  NProgress,
  NTooltip,
  useMessage,
} from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import StateBlock from '@/components/StateBlock.vue'
import { useStatusStore } from '@/stores/status'

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()

/** 全局状态：导出过程在底部状态栏可见（本 store 不发 IPC，也不改调用时序） */
const status = useStatusStore()

// ── 导出槽位定义 ──
interface ExportSlot {
  id: string
  label: string
  ext: string
  description: string
  enabled: boolean
}

const exportSlots = ref<ExportSlot[]>([
  { id: 'bank', label: 'Bank', ext: '.bank', description: '音频银行导出', enabled: true },
  { id: 'rebank', label: 'Rebank', ext: '.rebank', description: '音频重打包', enabled: false },
  { id: 'carra', label: 'Carra', ext: '.carra', description: 'Carra 格式导出', enabled: false },
  { id: 'lunartique', label: 'Lunartique', ext: '.zip', description: 'Lunartique 压缩包', enabled: false },
  { id: 'langbus', label: 'LangBus', ext: '.json', description: '语言总线数据', enabled: true },
  { id: 'langpatch', label: 'LangPatch', ext: '.json', description: '语言补丁数据', enabled: true },
  { id: 'langpathset', label: 'LangPathset', ext: '.json', description: '语言路径集', enabled: false },
  { id: 'staticmod', label: 'StaticMod', ext: '.staticmod', description: '静态模组数据', enabled: false },
])

// ── 导出分组定义 ──
interface ExportGroup {
  id: string
  label: string
  description: string
  enabled: boolean
}

const exportGroups = ref<ExportGroup[]>([
  { id: '_fmod', label: '_fmod', description: '音频资源', enabled: true },
  { id: '_data', label: '_data', description: '通用资源', enabled: true },
  { id: '_text', label: '_text', description: '语言文本', enabled: true },
  { id: '_static', label: '_static', description: '静态数据', enabled: false },
])

// ── 格式矩阵（源 → 目标兼容性） ──
interface FormatCompat {
  source: string
  targets: { format: string; compatible: boolean }[]
}

const formatMatrix = ref<FormatCompat[]>([
  { source: 'Texture', targets: [{ format: 'PNG', compatible: true }, { format: 'DDS', compatible: true }, { format: 'TGA', compatible: false }] },
  { source: 'Sprite', targets: [{ format: 'PNG', compatible: true }, { format: 'DDS', compatible: true }, { format: 'TGA', compatible: false }] },
  { source: 'Audio', targets: [{ format: 'WAV', compatible: true }, { format: 'OGG', compatible: true }, { format: 'MP3', compatible: false }] },
  { source: 'Text', targets: [{ format: 'JSON', compatible: true }, { format: 'YAML', compatible: true }, { format: 'XML', compatible: false }] },
  { source: 'Json', targets: [{ format: 'JSON', compatible: true }, { format: 'YAML', compatible: false }, { format: 'XML', compatible: false }] },
  { source: 'MonoBehaviour', targets: [{ format: 'JSON', compatible: true }, { format: 'YAML', compatible: false }, { format: 'XML', compatible: false }] },
])

// ── 导出预览 ──
const exportPreview = ref({
  groups: 0,
  slots: 0,
  estimatedFiles: 0,
  estimatedSize: '0 MB',
})

// ── 导出顾问分析 ──
interface AdvisorItem {
  level: 'info' | 'warning' | 'error'
  message: string
  reasoning: string
}

const advisorItems = ref<AdvisorItem[]>([])

// ── 进度状态 ──
const progress = ref<ProgressPayload | null>(null)
const isExporting = ref(false)
const exportComplete = ref(false)
const exportError = ref<string | null>(null)

// ── 导出报告 ──
interface ExportReportItem {
  assetId: string
  name: string
  status: '已应用' | '已跳过' | '保留未知' | '已转换'
  detail: string
}

const exportReport = ref<ExportReportItem[]>([])
const outputDir = ref('')

// ── 计算属性 ──
const enabledSlots = computed(() => exportSlots.value.filter((s) => s.enabled))
const enabledGroups = computed(() => exportGroups.value.filter((g) => g.enabled))
const progressPercent = computed(() => {
  if (!progress.value || progress.value.total === 0) return 0
  return Math.round((progress.value.current / progress.value.total) * 100)
})

// ── 方法 ──
function toggleSlot(id: string) {
  const slot = exportSlots.value.find((s) => s.id === id)
  if (slot) slot.enabled = !slot.enabled
  updatePreview()
}

function toggleGroup(id: string) {
  const group = exportGroups.value.find((g) => g.id === id)
  if (group) group.enabled = !group.enabled
  updatePreview()
}

function updatePreview() {
  const slotCount = enabledSlots.value.length
  const groupCount = enabledGroups.value.length
  exportPreview.value = {
    groups: groupCount,
    slots: slotCount,
    estimatedFiles: slotCount * groupCount * 3,
    estimatedSize: `${(slotCount * groupCount * 2.4).toFixed(1)} MB`,
  }
}

function runAdvisor() {
  advisorItems.value = []
  const slots = enabledSlots.value
  const groups = enabledGroups.value

  if (slots.length === 0) {
    advisorItems.value.push({
      level: 'warning',
      message: '未选择任何导出槽位',
      reasoning: '至少需要启用一个导出槽位才能执行导出操作。',
    })
  }
  if (groups.length === 0) {
    advisorItems.value.push({
      level: 'warning',
      message: '未选择任何导出分组',
      reasoning: '至少需要启用一个导出分组才能执行导出操作。',
    })
  }
  if (slots.length > 0 && groups.length > 0) {
    advisorItems.value.push({
      level: 'info',
      message: '导出配置正常',
      reasoning: `已启用 ${slots.length} 个槽位和 ${groups.length} 个分组，预计生成 ${exportPreview.value.estimatedFiles} 个文件。`,
    })
  }
  if (slots.some((s) => s.id === 'bank') && !slots.some((s) => s.id === 'rebank')) {
    advisorItems.value.push({
      level: 'info',
      message: '建议同时启用 Rebank',
      reasoning: 'Bank 导出后通常需要 Rebank 重新打包以优化音频加载性能。',
    })
  }
  if (groups.some((g) => g.id === '_fmod') && !slots.some((s) => s.id === 'bank')) {
    advisorItems.value.push({
      level: 'warning',
      message: '_fmod 分组建议搭配 Bank 槽位',
      reasoning: '音频资源分组 _fmod 通常需要 Bank 槽位来生成对应的音频银行文件。',
    })
  }
}

async function startExport() {
  if (isExporting.value) return
  isExporting.value = true
  exportComplete.value = false
  exportError.value = null
  exportReport.value = []
  progress.value = null

  status.beginActivity('export-run', '正在导出模组')
  try {
    // 契约方法 export.run：载荷只有 { targetDirectory }（契约 §2.6），目录走宿主原生对话框
    const picked = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: '选择导出目标目录',
    })
    if (!picked.path) return

    status.updateActivity('export-run', { detail: '正在写出模组文件' })

    const result = await ipc.request<{ ok: boolean; root: string; slots: number }>('export.run', {
      targetDirectory: picked.path,
    })
    outputDir.value = result.root
    // 后端只回 root + 槽位数量，没有逐条清单：报告列表留空，不编造
    exportReport.value = []
    exportComplete.value = true
    message.success(`导出完成：${result.root}（${result.slots} 个槽位）`)
    status.notify('success', `导出完成：${result.root}（${result.slots} 个槽位）`)
  } catch (e: unknown) {
    exportError.value = e instanceof Error ? e.message : String(e)
    message.error(`导出失败：${exportError.value}`)
    status.notify('error', `导出失败：${exportError.value}`)
  } finally {
    isExporting.value = false
    status.endActivity('export-run')
  }
}

function cancelExport() {
  ipc.cancel('export')
  isExporting.value = false
  progress.value = null
}

function openOutputLocation() {
  if (outputDir.value) {
    ipc.request('process.start', { appId: 'explorer', args: outputDir.value }).catch(() => {
      status.notify('error', `无法打开输出目录：${outputDir.value}`)
    })
  }
}

function statusColor(status: string): string {
  const map: Record<string, string> = {
    已应用: 'var(--lme-success)',
    已跳过: 'var(--lme-text-muted)',
    保留未知: 'var(--lme-warning)',
    已转换: 'var(--lme-info)',
  }
  return map[status] ?? 'var(--lme-text-secondary)'
}

// ── 进度事件订阅 ──
let unsubscribeProgress: (() => void) | null = null

onMounted(() => {
  unsubscribeProgress = ipc.on('progress', (payload) => {
    progress.value = payload as ProgressPayload
  })
  updatePreview()
  runAdvisor()
})

onUnmounted(() => {
  unsubscribeProgress?.()
})
</script>

<template>
  <div class="export-view">
    <!-- 页头：标题 + 一句话说明 + 可关闭的操作指引 + 主操作 -->
    <PageHeader
      icon="export"
      title="导出工作台"
      description="把当前项目的所有改动打包成游戏能加载的模组"
      hint="先在左侧勾选导出槽位与分组，右侧「导出预览」会实时估算写出的文件数，确认后再点「开始导出」选目标目录"
      hint-key="export"
    >
      <template #meta>
        <span class="export-count">
          {{ enabledSlots.length }} 个槽位 · {{ enabledGroups.length }} 个分组
        </span>
      </template>

      <template #actions>
        <div class="progress-actions">
          <NButton
            v-if="isExporting"
            class="btn btn-danger"
            type="error"
            size="small"
            @click="cancelExport"
          >
            <template #icon>
              <AppIcon name="stopCircle" :size="13" />
            </template>
            取消导出
          </NButton>

          <NTooltip v-else placement="bottom" :show-arrow="false">
            <template #trigger>
              <NButton
                class="btn"
                :class="exportComplete ? 'btn-secondary' : 'btn-primary'"
                :type="exportComplete ? 'default' : 'primary'"
                size="small"
                @click="startExport"
              >
                <template #icon>
                  <AppIcon name="export" :size="13" />
                </template>
                {{ exportComplete ? '再次导出' : '开始导出' }}
              </NButton>
            </template>
            选一个目标目录，写出所有已启用槽位与分组的文件
          </NTooltip>
        </div>
      </template>
    </PageHeader>

    <!-- 两栏主体：格式矩阵 | 预览 / 分析 / 进度 / 报告 -->
    <div class="export-body">
      <!-- 左栏：格式矩阵 -->
      <div class="format-matrix-column">
        <div class="column-header">
          <h3>格式矩阵</h3>
          <NTooltip placement="bottom" :show-arrow="false">
            <template #trigger>
              <span class="column-subtitle">源格式 → 目标兼容性</span>
            </template>
            打勾表示该目标格式可由后端无损写出，打叉表示暂不支持
          </NTooltip>
        </div>

        <!-- 兼容性矩阵：源格式 × 目标格式（可写 / 不可写） -->
        <div class="matrix-container">
          <table class="matrix-table">
            <thead>
              <tr>
                <th>源格式</th>
                <th v-for="target in ['PNG', 'DDS', 'TGA', 'WAV', 'OGG', 'JSON', 'YAML']" :key="target">
                  {{ target }}
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in formatMatrix" :key="row.source">
                <td class="source-cell">{{ row.source }}</td>
                <td
                  v-for="target in row.targets"
                  :key="target.format"
                  class="compat-cell"
                  :class="{ compatible: target.compatible, incompatible: !target.compatible }"
                >
                  <AppIcon :name="target.compatible ? 'check' : 'close'" :size="13" />
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <!-- 导出槽位：NCheckbox 只承载勾选态，切换仍走原 toggleSlot -->
        <div class="slot-section">
          <h4>导出槽位</h4>
          <div class="slot-list">
            <div
              v-for="slot in exportSlots"
              :key="slot.id"
              class="slot-item"
              :class="{ enabled: slot.enabled }"
            >
              <NCheckbox
                class="slot-check"
                size="small"
                :checked="slot.enabled"
                @update:checked="toggleSlot(slot.id)"
              />
              <span class="slot-label">{{ slot.label }}</span>
              <span class="slot-ext lme-mono">{{ slot.ext }}</span>
              <span class="slot-desc">{{ slot.description }}</span>
            </div>
          </div>
        </div>

        <div class="group-section">
          <h4>导出分组</h4>
          <div class="group-list">
            <div
              v-for="group in exportGroups"
              :key="group.id"
              class="group-item"
              :class="{ enabled: group.enabled }"
            >
              <NCheckbox
                class="group-check"
                size="small"
                :checked="group.enabled"
                @update:checked="toggleGroup(group.id)"
              />
              <span class="group-label lme-mono">{{ group.label }}</span>
              <span class="group-desc">{{ group.description }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 分隔条 -->
      <div class="column-splitter" />

      <!-- 右栏：预览 / 分析 / 进度 / 报告 -->
      <div class="preview-column">
        <!-- 导出预览 -->
        <NCard class="preview-section" size="small" title="导出预览">
          <div class="preview-grid">
            <div class="preview-item">
              <span class="preview-value">{{ exportPreview.groups }}</span>
              <span class="preview-label">分组</span>
            </div>
            <div class="preview-item">
              <span class="preview-value">{{ exportPreview.slots }}</span>
              <span class="preview-label">槽位</span>
            </div>
            <div class="preview-item">
              <span class="preview-value">{{ exportPreview.estimatedFiles }}</span>
              <span class="preview-label">预计文件</span>
            </div>
            <div class="preview-item">
              <span class="preview-value">{{ exportPreview.estimatedSize }}</span>
              <span class="preview-label">预计大小</span>
            </div>
          </div>
        </NCard>

        <!-- 导出顾问分析 -->
        <NCard class="advisor-section" size="small" title="导出顾问">
          <div class="advisor-list">
            <NAlert
              v-for="(item, i) in advisorItems"
              :key="i"
              class="advisor-item"
              :class="'advisor-' + item.level"
              :type="item.level === 'info' ? 'info' : item.level === 'warning' ? 'warning' : 'error'"
              :title="item.message"
              :bordered="false"
            >
              {{ item.reasoning }}
            </NAlert>
            <StateBlock
              v-if="advisorItems.length === 0"
              class="advisor-empty"
              state="empty"
              icon="hint"
              title="暂无分析建议"
              description="勾选导出槽位或分组后，这里会给出配置建议"
            />
          </div>
        </NCard>

        <!-- 导出进度（开始 / 取消已移到页头，这里只看进度；IPC 调用点未变） -->
        <NCard
          v-if="isExporting || exportComplete"
          class="progress-section"
          size="small"
          title="导出进度"
        >
          <div class="progress-block">
            <NProgress
              class="progress-bar-container"
              type="line"
              :percentage="progressPercent"
              :height="8"
              :show-indicator="false"
              :border-radius="4"
            />
            <div class="progress-info">
              <span v-if="progress" class="progress-message">{{ progress.message }}</span>
              <span class="progress-percent lme-mono">{{ progressPercent }}%</span>
            </div>
          </div>
        </NCard>

        <!-- 错误提示：给出路，而不是只重复一句报错 -->
        <StateBlock
          v-if="exportError"
          class="error-banner"
          state="error"
          :title="exportError"
          description="检查目标目录是否可写、项目是否已打开，然后重新点右上角「开始导出」"
        />

        <!-- 导出报告 -->
        <NCard
          v-if="exportComplete && exportReport.length > 0"
          class="report-section"
          size="small"
          title="导出报告"
        >
          <div class="report-summary">
            <span class="report-stat">
              <span class="stat-value" style="color: var(--lme-success)">{{ exportReport.filter((r) => r.status === '已应用').length }}</span>
              已应用
            </span>
            <span class="report-stat">
              <span class="stat-value" style="color: var(--lme-text-muted)">{{ exportReport.filter((r) => r.status === '已跳过').length }}</span>
              已跳过
            </span>
            <span class="report-stat">
              <span class="stat-value" style="color: var(--lme-warning)">{{ exportReport.filter((r) => r.status === '保留未知').length }}</span>
              保留未知
            </span>
            <span class="report-stat">
              <span class="stat-value" style="color: var(--lme-info)">{{ exportReport.filter((r) => r.status === '已转换').length }}</span>
              已转换
            </span>
          </div>
          <div class="report-list">
            <div
              v-for="item in exportReport"
              :key="item.assetId"
              class="report-item"
            >
              <span class="report-name lme-ellipsis">{{ item.name }}</span>
              <span class="report-status" :style="{ color: statusColor(item.status) }">
                {{ item.status }}
              </span>
              <span class="report-detail">{{ item.detail }}</span>
            </div>
          </div>
          <template #action>
            <div class="report-actions">
              <NTooltip placement="top" :show-arrow="false">
                <template #trigger>
                  <NButton class="btn btn-primary" type="primary" size="small" @click="openOutputLocation">
                    <template #icon>
                      <AppIcon name="folderOpen" :size="13" />
                    </template>
                    打开输出目录
                  </NButton>
                </template>
                {{ outputDir || '导出完成后才有输出目录' }}
              </NTooltip>
            </div>
          </template>
        </NCard>

        <!-- 空状态：还没开始导出 -->
        <div v-if="!isExporting && !exportComplete && !exportError" class="empty-state">
          <StateBlock
            state="empty"
            icon="export"
            title="还没有开始导出"
            description="在左侧勾选要导出的槽位与分组，再点右上角「开始导出」选择目标目录"
          />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* ── 页面骨架：页头在上，两栏主体在下 ── */
.export-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  position: relative;
  background: var(--lme-bg-base);
}

.export-body {
  flex: 1;
  display: flex;
  min-height: 0;
  overflow: hidden;
}

/* 页头 #meta 的计数 */
.export-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 左栏：格式矩阵 ── */
.format-matrix-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
  padding: var(--lme-gap-lg);
  gap: var(--lme-gap-lg);
}

.column-header {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.column-header h3 {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-primary);
}

.column-subtitle {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  cursor: help;
}

.matrix-container {
  overflow: auto;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-panel);
}

.matrix-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--lme-font-size-sm);
}

.matrix-table th,
.matrix-table td {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  text-align: center;
  border-bottom: 1px solid var(--lme-border);
}

.matrix-table th {
  background: var(--lme-bg-elevated);
  color: var(--lme-text-secondary);
  font-weight: var(--lme-font-weight-medium);
  position: sticky;
  top: 0;
}

.source-cell {
  text-align: left;
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-medium);
}

.compat-cell {
  font-weight: var(--lme-font-weight-semibold);
}

.compat-cell.compatible {
  color: var(--lme-success);
}

.compat-cell.incompatible {
  color: var(--lme-text-disabled);
}

/* ── 槽位列表 ── */
.slot-section,
.group-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.slot-section h4,
.group-section h4 {
  margin: 0;
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-secondary);
}

.slot-list,
.group-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.slot-item,
.group-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  transition: background var(--lme-dur-base) var(--lme-ease-standard);
  border: 1px solid transparent;
}

.slot-item:hover,
.group-item:hover {
  background: var(--lme-bg-hover);
}

.slot-item.enabled,
.group-item.enabled {
  background: var(--lme-bg-selected);
  border-color: var(--lme-accent-muted);
}

.slot-check,
.group-check {
  flex-shrink: 0;
}

.slot-label,
.group-label {
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
  min-width: 80px;
}

.slot-ext {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

.slot-desc,
.group-desc {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  margin-left: auto;
}

/* ── 分隔条 ── */
.column-splitter {
  width: 6px;
  flex-shrink: 0;
  background: var(--lme-border);
  cursor: col-resize;
}

/* ── 右栏：预览/分析/进度/报告 ── */
.preview-column {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow-y: auto;
  padding: var(--lme-gap-lg);
  gap: var(--lme-gap-lg);
}

.preview-section,
.advisor-section,
.progress-section,
.report-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.preview-section h3,
.advisor-section h3,
.progress-section h3,
.report-section h3 {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-primary);
}

/* ── 预览网格 ── */
.preview-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--lme-gap-md);
}

.preview-item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  padding: var(--lme-gap-md);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.preview-value {
  font-size: var(--lme-font-size-xl);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-accent);
}

.preview-label {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 顾问列表 ── */
.advisor-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.advisor-item {
  display: flex;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-sm);
  border-left: 3px solid;
}

.advisor-info {
  border-left-color: var(--lme-info);
  background: var(--lme-info-subtle);
}

.advisor-warning {
  border-left-color: var(--lme-warning);
  background: var(--lme-warning-subtle);
}

.advisor-error {
  border-left-color: var(--lme-error);
  background: var(--lme-error-subtle);
}

.advisor-icon {
  font-size: var(--lme-font-size-md);
  flex-shrink: 0;
}

.advisor-content {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.advisor-message {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-medium);
}

.advisor-reasoning {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* 顾问空态由 StateBlock 呈现，这里只保留定位钩子 */
.advisor-empty {
  flex-shrink: 0;
}

/* ── 进度条（NProgress，尺寸由属性给定） ── */
.progress-block {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.progress-bar-container {
  width: 100%;
}

.progress-info {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.progress-message {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.progress-percent {
  color: var(--lme-text-primary);
  font-weight: var(--lme-font-weight-semibold);
}

.progress-actions {
  display: flex;
  gap: var(--lme-gap-sm);
}

/* ── 按钮 ──
   外观一律交给 Naive UI 的 NButton（配色/内边距由 naiveTheme 从 tokens 派生）；
   .btn / .btn-primary / .btn-secondary / .btn-danger 仅作回归定位钩子，不再覆盖库样式 */

/* ── 错误提示：呈现交给 StateBlock，这里只保留定位钩子 ── */
.error-banner {
  flex-shrink: 0;
}

/* ── 报告 ── */
.report-summary {
  display: flex;
  gap: var(--lme-gap-lg);
  flex-wrap: wrap;
}

.report-stat {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.stat-value {
  font-weight: var(--lme-font-weight-semibold);
  font-size: var(--lme-font-size-md);
}

.report-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  max-height: 300px;
  overflow-y: auto;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-panel);
}

.report-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-sm);
}

.report-name {
  flex: 1;
  color: var(--lme-text-primary);
  min-width: 0;
}

.report-status {
  font-weight: var(--lme-font-weight-medium);
  min-width: 60px;
  text-align: center;
}

.report-detail {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  min-width: 80px;
  text-align: right;
}

.report-actions {
  display: flex;
  gap: var(--lme-gap-sm);
}

/* ── 空状态（内容交给 StateBlock，本页只让它撑满右栏） ── */
.empty-state {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
}
</style>
