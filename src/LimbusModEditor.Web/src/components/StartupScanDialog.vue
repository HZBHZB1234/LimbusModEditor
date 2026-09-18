<script setup lang="ts">
/**
 * 启动「扫描」模态窗 —— 启动流程第 ③ 步。
 *
 * 软件启动、项目就绪后自动触发 `scan.run(scope=all)`，本窗把六个步骤的
 * 状态、当前阶段与耗时实时摊开（用户要求 1 的「扫描需要有相关模态窗口」）。
 *
 * 关窗规则：
 *  · 扫描中 → 只能「取消扫描」（协作式取消），不能直接关掉；
 *  · 成功   → 2.5 秒后自动进工作台，也可点「进入工作台」立刻继续；
 *  · 失败   → 停在窗里给中文原因 + 「重试」/「跳过并进入工作台」两条出路，
 *             不让用户卡死（失败也绝不静默）。
 */
import { computed, onUnmounted, ref, watch } from 'vue'
import { NAlert, NButton, NModal, NProgress } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import { useStartupStore } from '@/stores/startup'
import type { IconName } from '@/components/icons'

const startup = useStartupStore()

/** 扫描阶段 → 行状态（Pending 行只有在「当前阶段命中它」时才算进行中） */
type RowState = 'pending' | 'running' | 'done' | 'warn' | 'error'

const show = computed(() => startup.scanOpen)

const rows = computed(() => {
  const active = startup.activeStepKey
  return startup.stepRows.map((row) => {
    let state: RowState = 'pending'
    if (row.status === 'Scanned' || row.status === 'AlreadyFresh') state = 'done'
    else if (row.status === 'Skipped') state = 'warn'
    else if (row.status === 'Failed') state = 'error'
    else if (row.key === active && startup.scanRunning) state = 'running'
    return { ...row, state }
  })
})

const STATUS_TEXT: Record<string, string> = {
  Scanned: '已完成',
  AlreadyFresh: '已是最新',
  Skipped: '已跳过',
  Failed: '失败',
  Pending: '待执行',
}

const ROW_ICON: Record<RowState, IconName> = {
  pending: 'dot',
  running: 'loader',
  done: 'success',
  warn: 'warning',
  error: 'error',
}

const completedCount = computed(
  () => rows.value.filter((r) => r.state !== 'pending' && r.state !== 'running').length,
)

const percentage = computed(() => {
  if (startup.scanSummary) return 100
  return Math.min(99, Math.round((completedCount.value / Math.max(1, rows.value.length)) * 100))
})

/** 标题随状态变化（进行中 / 完成 / 失败） */
const title = computed(() => {
  if (startup.scanSummary) return '扫描完成'
  if (startup.scanError) return '扫描失败'
  return '正在扫描游戏资源'
})

const hasFailure = computed(() => rows.value.some((r) => r.state === 'error' || r.state === 'warn'))

// ── 耗时（每秒刷新，扫描结束后冻结） ──
const now = ref(Date.now())
let timer: ReturnType<typeof setInterval> | null = null

watch(
  () => startup.scanRunning,
  (running) => {
    if (running) {
      now.value = Date.now()
      if (!timer) timer = setInterval(() => (now.value = Date.now()), 500)
    } else if (timer) {
      clearInterval(timer)
      timer = null
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  if (timer) clearInterval(timer)
})

const elapsedText = computed(() => {
  const start = startup.scanStartedAt || now.value
  const ms = startup.scanSummary
    ? startup.scanSummary.elapsedSeconds * 1000
    : Math.max(0, now.value - start)
  return `${(ms / 1000).toFixed(1)} 秒`
})

function rowDetail(row: { state: RowState; detail: string; elapsedSeconds: number }): string {
  if (row.state === 'running') return startup.scanDetail || startup.scanPhase || '进行中…'
  if (row.detail) return row.detail
  return row.state === 'pending' ? '待执行' : '—'
}

function formatSeconds(value: number): string {
  return value > 0 ? `${value.toFixed(1)}s` : ''
}
</script>

<template>
  <NModal
    :show="show"
    :closable="false"
    :mask-closable="false"
    :close-on-esc="false"
    :block-scroll="true"
    :trap-focus="true"
    style="width: min(700px, 94vw)"
  >
    <div class="scan" role="dialog" aria-modal="true" aria-label="扫描游戏资源">
      <!-- ── 标题 ── -->
      <header class="scan-head">
        <span class="scan-badge" :class="{ ok: !!startup.scanSummary, bad: !!startup.scanError }">
          <AppIcon
            :name="startup.scanSummary ? 'success' : startup.scanError ? 'error' : 'scan'"
            :size="20"
          />
        </span>
        <div class="scan-head-text">
          <h2 class="scan-title">{{ title }}</h2>
          <p class="scan-sub">
            项目：<span class="scan-strong">{{ startup.projectName || '—' }}</span>
            <template v-if="startup.scanSummary?.cacheDirectory || startup.paths.unityCacheDirectory">
              · 缓存目录：<span class="lme-mono">{{
                startup.scanSummary?.cacheDirectory || startup.paths.unityCacheDirectory
              }}</span>
            </template>
          </p>
        </div>
        <span class="scan-elapsed lme-mono">{{ elapsedText }}</span>
      </header>

      <!-- ── 进度条 ── -->
      <NProgress
        type="line"
        :percentage="percentage"
        :processing="startup.scanRunning"
        :show-indicator="false"
        :height="6"
        :border-radius="3"
      />

      <!-- ── 当前阶段 ── -->
      <div class="scan-current">
        <AppIcon
          :name="startup.scanRunning ? 'loader' : startup.scanSummary ? 'check' : 'info'"
          :size="13"
          :class="{ spinning: startup.scanRunning }"
        />
        <span class="sc-phase">{{ startup.scanPhase || '准备中' }}</span>
        <span class="sc-detail lme-ellipsis" :title="startup.scanDetail">{{ startup.scanDetail }}</span>
      </div>

      <!-- ── 六个步骤 ── -->
      <ul class="scan-steps">
        <li v-for="row in rows" :key="row.key" class="step-row" :class="'is-' + row.state">
          <AppIcon
            :name="ROW_ICON[row.state]"
            :size="13"
            class="step-icon"
            :class="{ spinning: row.state === 'running' }"
          />
          <span class="step-label">{{ row.label }}</span>
          <span class="step-status">{{ STATUS_TEXT[row.status] ?? '—' }}</span>
          <span class="step-detail lme-ellipsis" :title="rowDetail(row)">{{ rowDetail(row) }}</span>
          <span class="step-time lme-mono">{{ formatSeconds(row.elapsedSeconds) }}</span>
        </li>
      </ul>

      <!-- ── 失败原因（不静默） ── -->
      <NAlert v-if="startup.scanError" type="error" :closable="false" title="扫描没有完成">
        {{ startup.scanError }}
      </NAlert>
      <p v-else-if="startup.scanSummary && hasFailure" class="scan-note">
        有步骤被跳过或失败：索引可能不完整，可在「项目」页重新扫描，或在「设置」页核对目录。
      </p>

      <!-- ── 完成摘要 ── -->
      <div v-if="startup.scanSummary" class="scan-summary">
        <span class="ss-item">
          <AppIcon name="database" :size="13" />
          {{ startup.scanSummary.bundleCount.toLocaleString('zh-CN') }} 个 bundle
        </span>
        <span class="ss-item">
          <AppIcon name="assets" :size="13" />
          {{ startup.scanSummary.assetCount.toLocaleString('zh-CN') }} 条资源
        </span>
        <span class="ss-item">
          <AppIcon name="timer" :size="13" />
          用时 {{ startup.scanSummary.elapsedSeconds.toFixed(1) }} 秒
        </span>
      </div>

      <!-- ── 操作 ── -->
      <footer class="scan-foot">
        <template v-if="startup.scanRunning">
          <span class="foot-hint">首次扫描要枚举全部缓存 bundle，请稍候；中断可以点右侧按钮</span>
          <NButton size="small" @click="startup.cancelScan()">
            <template #icon><AppIcon name="stopCircle" :size="13" /></template>
            取消扫描
          </NButton>
        </template>

        <template v-else-if="startup.scanError">
          <span class="foot-hint">可以重试，也可以先进工作台再手动扫描</span>
          <div class="foot-actions">
            <NButton size="small" @click="startup.skipScan()">跳过并进入工作台</NButton>
            <NButton size="small" type="primary" @click="startup.retryScan()">
              <template #icon><AppIcon name="refresh" :size="13" /></template>
              重试扫描
            </NButton>
          </div>
        </template>

        <template v-else>
          <span class="foot-hint">即将自动进入工作台</span>
          <NButton size="small" type="primary" @click="startup.enterWorkspace()">
            <template #icon><AppIcon name="arrowRight" :size="13" /></template>
            进入工作台
          </NButton>
        </template>
      </footer>
    </div>
  </NModal>
</template>

<style scoped>
.scan {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-xl);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-xl);
  box-shadow: var(--lme-shadow-xl);
}

/* ── 标题 ── */
.scan-head {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
}

.scan-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-full);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
}

.scan-badge.ok {
  background: var(--lme-success-subtle);
  color: var(--lme-success);
}

.scan-badge.bad {
  background: var(--lme-error-subtle);
  color: var(--lme-error);
}

.scan-head-text {
  flex: 1;
  min-width: 0;
}

.scan-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.scan-sub {
  margin: 3px 0 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  word-break: break-all;
}

.scan-strong {
  color: var(--lme-text-secondary);
  font-weight: var(--lme-font-weight-medium);
}

.scan-elapsed {
  flex-shrink: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

/* ── 当前阶段 ── */
.scan-current {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

.sc-phase {
  flex-shrink: 0;
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
}

.sc-detail {
  min-width: 0;
  color: var(--lme-text-muted);
}

.spinning {
  animation: lme-spin 1.1s linear infinite;
}

/* ── 步骤表 ── */
.scan-steps {
  list-style: none;
  margin: 0;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  display: flex;
  flex-direction: column;
  gap: 5px;
  background: var(--lme-bg-inset);
  border: 1px solid var(--lme-border-subtle);
  border-radius: var(--lme-radius-lg);
}

.step-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
  font-size: var(--lme-font-size-xs);
}

.step-icon {
  flex-shrink: 0;
  color: var(--lme-text-disabled);
}

.is-running .step-icon {
  color: var(--lme-accent);
}

.is-done .step-icon {
  color: var(--lme-success);
}

.is-warn .step-icon {
  color: var(--lme-warning);
}

.is-error .step-icon {
  color: var(--lme-error);
}

.step-label {
  flex-shrink: 0;
  min-width: 108px;
  color: var(--lme-text-primary);
}

.is-pending .step-label {
  color: var(--lme-text-muted);
}

.step-status {
  flex-shrink: 0;
  min-width: 56px;
  color: var(--lme-text-secondary);
}

.step-detail {
  flex: 1;
  min-width: 0;
  color: var(--lme-text-muted);
}

.step-time {
  flex-shrink: 0;
  color: var(--lme-text-disabled);
}

/* ── 摘要 / 备注 ── */
.scan-summary {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-lg);
  flex-wrap: wrap;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

.ss-item {
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-xs);
}

.scan-note {
  margin: 0;
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
  color: var(--lme-warning);
}

/* ── 底部操作 ── */
.scan-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-md);
  padding-top: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border-subtle);
}

.foot-hint {
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.foot-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-shrink: 0;
}
</style>
