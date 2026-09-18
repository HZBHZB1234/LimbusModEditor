<script setup lang="ts">
// 导出向导页面
// 三栏布局：格式矩阵 | 分隔条 | 预览/分析/进度/报告

import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ipc } from '@/ipc'
import type { ProgressPayload } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NCheckbox,
  NEmpty,
  NProgress,
  useMessage,
} from 'naive-ui'

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()

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

  try {
    // 契约方法 export.run：载荷只有 { targetDirectory }（契约 §2.6），目录走宿主原生对话框
    const picked = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: '选择导出目标目录',
    })
    if (!picked.path) return

    const result = await ipc.request<{ ok: boolean; root: string; slots: number }>('export.run', {
      targetDirectory: picked.path,
    })
    outputDir.value = result.root
    // 后端只回 root + 槽位数量，没有逐条清单：报告列表留空，不编造
    exportReport.value = []
    exportComplete.value = true
    message.success(`导出完成：${result.root}（${result.slots} 个槽位）`)
  } catch (e: unknown) {
    exportError.value = e instanceof Error ? e.message : String(e)
    message.error(`导出失败：${exportError.value}`)
  } finally {
    isExporting.value = false
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
      // 暂未实现：错误提示
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
    <!-- 左栏：格式矩阵 -->
    <div class="format-matrix-column">
      <div class="column-header">
        <h3>格式矩阵</h3>
        <span class="column-subtitle">源格式 → 目标兼容性</span>
      </div>

      <!-- 兼容性矩阵：源格式 × 目标格式（✓/✗） -->
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
                {{ target.compatible ? '✓' : '✗' }}
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
          <div v-if="advisorItems.length === 0" class="advisor-empty">
            <NEmpty size="small" description="暂无分析建议" />
          </div>
        </div>
      </NCard>

      <!-- 导出操作：开始 / 取消（IPC 调用点未变） -->
      <NCard class="progress-section" size="small" title="导出操作">
        <div class="progress-actions">
          <NButton
            v-if="isExporting"
            class="btn btn-danger"
            type="error"
            size="small"
            @click="cancelExport"
          >
            取消导出
          </NButton>
          <NButton
            v-else-if="!exportComplete"
            class="btn btn-primary"
            type="primary"
            size="small"
            @click="startExport"
          >
            开始导出
          </NButton>
          <NButton
            v-else
            class="btn btn-secondary"
            size="small"
            @click="startExport"
          >
            再次导出
          </NButton>
        </div>

        <!-- 进度条 -->
        <div v-if="isExporting || exportComplete" class="progress-block">
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

      <!-- 错误提示 -->
      <NAlert
        v-if="exportError"
        class="error-banner"
        type="error"
        :closable="false"
        title="导出失败"
      >
        {{ exportError }}
      </NAlert>

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
            <NButton class="btn btn-primary" type="primary" size="small" @click="openOutputLocation">
              📂 打开输出目录
            </NButton>
          </div>
        </template>
      </NCard>

      <!-- 空状态 -->
      <div v-if="!isExporting && !exportComplete && !exportError" class="empty-state">
        <NEmpty size="small" description="配置导出选项后点击「开始导出」">
          <template #extra>
            <span class="state-hint">左侧勾选导出槽位与分组，导出方向由宿主目录对话框决定</span>
          </template>
        </NEmpty>
      </div>
    </div>
  </div>
</template>

<style scoped>
.export-view {
  display: flex;
  height: 100%;
  overflow: hidden;
  position: relative;
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
  font-weight: 500;
  position: sticky;
  top: 0;
}

.source-cell {
  text-align: left;
  color: var(--lme-text-primary);
  font-weight: 500;
}

.compat-cell {
  font-weight: 600;
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
  transition: background 0.15s;
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
  font-weight: 500;
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
  font-weight: 600;
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
  background: rgba(91, 155, 213, 0.08);
}

.advisor-warning {
  border-left-color: var(--lme-warning);
  background: rgba(224, 168, 58, 0.08);
}

.advisor-error {
  border-left-color: var(--lme-error);
  background: rgba(217, 83, 79, 0.08);
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
  font-weight: 500;
}

.advisor-reasoning {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.advisor-empty {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  text-align: center;
  padding: var(--lme-gap-lg);
}

/* ── 进度条（NProgress，尺寸由属性给定） ── */
.progress-block {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-md);
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
  font-weight: 600;
}

.progress-actions {
  display: flex;
  gap: var(--lme-gap-sm);
}

/* ── 按钮 ── */
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

.btn-danger {
  background: var(--lme-error);
  border-color: var(--lme-error);
  color: var(--lme-text-primary);
}

.btn-danger:hover {
  opacity: 0.9;
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
  font-weight: 600;
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
  font-weight: 500;
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

/* ── 空状态 ── */
.empty-state {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--lme-gap-xl);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}
</style>
