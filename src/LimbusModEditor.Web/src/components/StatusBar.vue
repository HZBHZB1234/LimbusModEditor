<script setup lang="ts">
/**
 * 底部全局状态栏（VS Code 式）。
 *
 * 左区：当前项目 / 索引健康度 / 正在进行的操作与进度
 * 右区：未读通知 / 在途请求 / 主题切换 / 宿主连接 / 版本
 *
 * 数据全部来自 stores/status.ts 与 stores/appearance.ts，本组件不发 IPC。
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { NPopover } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import { useStatusStore } from '@/stores/status'
import type { NoticeLevel } from '@/stores/status'

const router = useRouter()
const status = useStatusStore()

// ── 让「值得显示的操作」在无进度时也能按时出现（showActivity 依赖时间） ──
const tick = ref(0)
let timer: ReturnType<typeof setInterval> | null = null
onMounted(() => {
  timer = setInterval(() => {
    tick.value++
  }, 400)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
})

const active = computed(() => {
  void tick.value
  return status.showActivity ? status.activeActivity : null
})

const percent = computed(() => {
  const a = active.value
  if (!a || a.total === null || a.total <= 0) return null
  return Math.min(100, Math.round((a.current ?? 0) / a.total * 100))
})

const projectLabel = computed(() => status.projectName || '未打开项目')

const health = computed(() => status.healthSummary)

/** 通知级别 → 图标名 */
const LEVEL_ICON: Record<NoticeLevel, 'info' | 'success' | 'warning' | 'error'> = {
  info: 'info',
  success: 'success',
  warning: 'warning',
  error: 'error',
}

function levelIcon(level: NoticeLevel) {
  return LEVEL_ICON[level]
}

function formatTime(at: number): string {
  const d = new Date(at)
  const hh = String(d.getHours()).padStart(2, '0')
  const mm = String(d.getMinutes()).padStart(2, '0')
  const ss = String(d.getSeconds()).padStart(2, '0')
  return `${hh}:${mm}:${ss}`
}

const noticeOpen = ref(false)

function openNotices(value: boolean) {
  noticeOpen.value = value
  if (value) status.markAllRead()
}

function goProject() {
  void router.push('/project')
}
</script>

<template>
  <footer class="statusbar">
    <!-- ══ 左区 ══ -->
    <button class="sb-item clickable" :title="status.projectPath || '尚未打开项目'" @click="goProject">
      <AppIcon name="project" :size="13" />
      <span class="sb-text sb-strong">{{ projectLabel }}</span>
    </button>

    <span class="sb-item" :class="'health-' + health.level" :title="'索引与运行状态：' + health.text">
      <AppIcon :name="health.level === 'error' ? 'error' : health.level === 'warning' ? 'warning' : 'success'" :size="13" />
      <span class="sb-text">{{ health.text }}</span>
    </span>

    <span v-if="status.lastScanAt" class="sb-item" title="最近一次资源扫描完成时间">
      <AppIcon name="clock" :size="13" />
      <span class="sb-text">{{ formatTime(status.lastScanAt) }}</span>
    </span>

    <!-- 正在进行的操作（有进度则显示进度条） -->
    <div v-if="active" class="sb-item sb-activity" :title="active.detail || active.label">
      <AppIcon name="loader" :size="13" class="spin" />
      <span class="sb-text sb-strong">{{ active.label }}</span>
      <template v-if="percent !== null">
        <span class="sb-progress-track">
          <span class="sb-progress-fill" :style="{ width: percent + '%' }" />
        </span>
        <span class="sb-text">{{ percent }}%</span>
      </template>
      <span v-else-if="active.detail" class="sb-text sb-dim">{{ active.detail }}</span>
    </div>

    <span v-if="status.activities.length > 1" class="sb-item" :title="'另有 ' + (status.activities.length - 1) + ' 项操作在进行'">
      <AppIcon name="stack" :size="13" />
      <span class="sb-text">+{{ status.activities.length - 1 }}</span>
    </span>

    <!-- ══ 右区 ══ -->
    <div class="sb-spacer" />

    <NPopover
      v-if="status.notices.length > 0"
      trigger="click"
      placement="top-end"
      :show-arrow="false"
      :width="340"
      @update:show="openNotices"
    >
      <template #trigger>
        <button class="sb-item clickable" :title="'通知（' + status.notices.length + ' 条）'">
          <AppIcon name="info" :size="13" />
          <span class="sb-text">通知</span>
          <span v-if="status.unreadCount > 0" class="sb-badge">{{ status.unreadCount }}</span>
        </button>
      </template>

      <div class="notice-panel">
        <div class="notice-head">
          <span>通知记录</span>
          <button class="notice-clear" @click="status.clearNotices()">清空</button>
        </div>
        <ul class="notice-list">
          <li v-for="n in status.notices" :key="n.id" class="notice-row" :class="'lvl-' + n.level">
            <AppIcon :name="levelIcon(n.level)" :size="14" class="notice-icon" />
            <span class="notice-msg">{{ n.message }}</span>
            <span class="notice-time lme-mono">{{ formatTime(n.at) }}</span>
          </li>
        </ul>
      </div>
    </NPopover>

    <span
      v-if="status.pendingRequests > 0"
      class="sb-item"
      :title="'与宿主通信中：' + status.pendingRequests + ' 个请求在途（最近 ' + status.lastMethod + '）'"
    >
      <AppIcon name="activity" :size="13" />
      <span class="sb-text">{{ status.pendingRequests }}</span>
    </span>

    <span
      class="sb-item"
      :class="status.connected ? 'conn-ok' : 'conn-off'"
      :title="status.connected ? '宿主 IPC 已连接' : '宿主 IPC 未连接（离线降级模式）'"
    >
      <AppIcon :name="status.connected ? 'wifi' : 'wifiOff'" :size="13" />
      <span class="sb-text">{{ status.connected ? '已连接' : '离线' }}</span>
    </span>

    <span
      v-if="status.hostVersion"
      class="sb-item"
      :title="
        '宿主版本 ' +
        status.hostVersion +
        '，契约 ' +
        status.contractVersion +
        (status.runtimeVersion ? '，WebView2 ' + status.runtimeVersion : '')
      "
    >
      <span class="sb-text lme-mono">v{{ status.hostVersion }}</span>
    </span>
  </footer>
</template>

<style scoped>
.statusbar {
  height: var(--lme-statusbar-height);
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-2xs);
  padding: 0 var(--lme-gap-sm);
  background: var(--lme-statusbar-bg);
  border-top: 1px solid var(--lme-statusbar-border);
  color: var(--lme-statusbar-text);
  font-size: var(--lme-font-size-xs);
  user-select: none;
  overflow: hidden;
}

.sb-spacer {
  flex: 1;
  min-width: var(--lme-gap-sm);
}

.sb-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 18px;
  padding: 0 6px;
  border: none;
  border-radius: var(--lme-radius-xs);
  background: none;
  color: inherit;
  font-family: inherit;
  font-size: inherit;
  white-space: nowrap;
  flex-shrink: 0;
  max-width: 320px;
  overflow: hidden;
}

.sb-item.clickable {
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.sb-item.clickable:hover {
  background: var(--lme-statusbar-item-hover-bg);
  color: var(--lme-text-primary);
}

.sb-text {
  overflow: hidden;
  text-overflow: ellipsis;
}

.sb-strong {
  color: var(--lme-statusbar-text-strong);
  font-weight: var(--lme-font-weight-medium);
}

.sb-dim {
  color: var(--lme-text-disabled);
}

/* ── 健康度配色 ── */
.health-success {
  color: var(--lme-statusbar-text);
}
.health-warning {
  color: var(--lme-statusbar-warning);
}
.health-error {
  color: var(--lme-statusbar-error);
}
.conn-ok {
  color: var(--lme-statusbar-text);
}
.conn-off {
  color: var(--lme-statusbar-error);
}

/* ── 进行中的操作 ── */
.sb-activity {
  max-width: 420px;
  color: var(--lme-statusbar-text-strong);
}

.sb-activity .spin {
  animation: lme-spin 1.1s linear infinite;
  color: var(--lme-statusbar-accent);
}

.sb-progress-track {
  display: inline-block;
  width: 72px;
  height: 4px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-full);
  background: var(--lme-progressbar-bg);
  overflow: hidden;
}

.sb-progress-fill {
  display: block;
  height: 100%;
  border-radius: var(--lme-radius-full);
  background: var(--lme-progressbar-fill);
  transition: width var(--lme-dur-base) var(--lme-ease-standard);
}

/* ── 未读角标 ── */
.sb-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 15px;
  height: 15px;
  padding: 0 4px;
  border-radius: var(--lme-radius-full);
  background: var(--lme-activitybar-badge-bg);
  color: var(--lme-activitybar-badge-text);
  font-size: var(--lme-font-size-2xs);
  font-weight: var(--lme-font-weight-semibold);
  line-height: 1;
}

/* ── 通知面板 ── */
.notice-panel {
  display: flex;
  flex-direction: column;
  max-height: 360px;
}

.notice-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: var(--lme-gap-sm);
  border-bottom: 1px solid var(--lme-border);
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.notice-clear {
  background: none;
  border: none;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  font-family: inherit;
  cursor: pointer;
  padding: 2px 6px;
  border-radius: var(--lme-radius-xs);
}

.notice-clear:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.notice-list {
  list-style: none;
  margin: 0;
  padding: var(--lme-gap-xs) 0 0;
  overflow-y: auto;
}

.notice-row {
  display: flex;
  align-items: flex-start;
  gap: var(--lme-gap-sm);
  padding: 6px 2px;
  font-size: var(--lme-font-size-sm);
  line-height: var(--lme-line-height-normal);
}

.notice-row + .notice-row {
  border-top: 1px solid var(--lme-row-divider);
}

.notice-icon {
  margin-top: 2px;
}

.lvl-info .notice-icon {
  color: var(--lme-info);
}
.lvl-success .notice-icon {
  color: var(--lme-success);
}
.lvl-warning .notice-icon {
  color: var(--lme-warning);
}
.lvl-error .notice-icon {
  color: var(--lme-error);
}

.notice-msg {
  flex: 1;
  min-width: 0;
  color: var(--lme-text-secondary);
  word-break: break-word;
}

.lvl-error .notice-msg {
  color: var(--lme-error);
}

.notice-time {
  flex-shrink: 0;
  color: var(--lme-text-disabled);
  font-size: var(--lme-font-size-2xs);
}
</style>
