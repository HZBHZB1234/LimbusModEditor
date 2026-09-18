<script setup lang="ts">
/**
 * 启动「项目门」—— 启动流程第 ② 步，**不可关闭**的模态窗。
 *
 * 职责：软件启动后强制要求「新建」或「打开」一个项目，选不出来就不放行
 * （用户要求 2）。顺带把启动时已完成的路径定位结果摆出来（用户要求 1），
 * 让用户在选项目之前就知道游戏目录/缓存目录有没有认到。
 *
 * 关闭口一律封死：无关闭按钮、点遮罩不关、Esc 不关；项目就绪后由 store 把
 * `phase` 推到 `scanning`，本窗自然消失（不存在「用户手动关掉它」的路径）。
 *
 * IPC 只走既有方法（project.create / project.open / project.recent / dialog.openFile /
 * config.read），载荷字段与 ProjectView 完全一致。
 */
import { computed, ref } from 'vue'
import { NAlert, NButton, NInput, NModal } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import { useStartupStore } from '@/stores/startup'

const startup = useStartupStore()

const newName = ref('')
const busy = ref(false)
const error = ref('')

/** 门只在 phase=project 时出现（store 是唯一事实来源） */
const show = computed(() => startup.gateOpen)

/** 上次项目的显示名（末段去掉 .lmeproj） */
const lastName = computed(() => {
  const path = startup.lastProjectFile
  if (!path) return ''
  const parts = path.split(/[\\/]/)
  return (parts[parts.length - 1] || path).replace(/\.lmeproj$/i, '')
})

/** 路径定位四行（探不到照实写「未找到」，不猜） */
const pathRows = computed(() => [
  { key: 'game', label: '游戏目录', path: startup.paths.gameDirectory },
  { key: 'cache', label: 'Unity 缓存目录', path: startup.paths.unityCacheDirectory },
  { key: 'mod', label: '模组目录', path: startup.paths.modDirectory },
  { key: 'fmod', label: 'FMOD DLL 目录', path: startup.paths.fmodLibraryDirectory },
])

function describe(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

/** 统一包一层：按钮 loading + 错误就地显示（失败不关窗，用户可换一个项目） */
async function run(fn: () => Promise<void>): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await fn()
  } catch (e: unknown) {
    error.value = describe(e)
  } finally {
    busy.value = false
  }
}

function create(): void {
  void run(async () => {
    await startup.createProject(newName.value)
    newName.value = ''
  })
}

function open(): void {
  void run(async () => {
    await startup.pickProjectFile()
  })
}

function openRecent(path: string): void {
  void run(async () => {
    await startup.openProject(path)
  })
}

function continueLast(): void {
  void run(async () => {
    await startup.openProject(startup.lastProjectFile)
  })
}

function formatTime(value: string): string {
  if (!value) return '—'
  try {
    return new Date(value).toLocaleString('zh-CN')
  } catch {
    return value
  }
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
    style="width: min(760px, 94vw)"
  >
    <div class="gate" role="dialog" aria-modal="true" aria-label="选择模组项目">
      <!-- ── 标题 ── -->
      <header class="gate-head">
        <span class="gate-badge">
          <AppIcon name="project" :size="20" />
        </span>
        <div class="gate-head-text">
          <h2 class="gate-title">开始前先选择一个项目</h2>
          <p class="gate-sub">
            编辑器需要项目才能工作：改动清单、来源与导出方案都记在项目里。
            新建或打开一个项目后，会自动扫描游戏资源并建立索引。
          </p>
        </div>
      </header>

      <!-- ── 路径定位结果（启动时已自动完成） ── -->
      <section class="gate-paths">
        <div class="gp-head">
          <span class="gp-title lme-caps">路径定位（启动时自动完成）</span>
          <span class="gp-count">{{ startup.locatedCount }} / 4</span>
        </div>
        <ul class="gp-list">
          <li
            v-for="row in pathRows"
            :key="row.key"
            class="gp-row"
            :class="{ 'is-missing': !row.path }"
          >
            <AppIcon :name="row.path ? 'success' : 'warning'" :size="12" class="gp-icon" />
            <span class="gp-label">{{ row.label }}</span>
            <span v-if="row.path" class="gp-path lme-mono lme-ellipsis" :title="row.path">
              {{ row.path }}
            </span>
            <span v-else class="gp-missing">未找到</span>
          </li>
        </ul>
        <p v-if="startup.paths.error" class="gp-note gp-note-error">
          路径定位失败：{{ startup.paths.error }}
        </p>
        <p v-else-if="startup.paths.info" class="gp-note">{{ startup.paths.info }}</p>
        <p v-if="!startup.hasCacheDirectory" class="gp-note gp-note-warn">
          未定位到 Unity 缓存目录：本次扫描会跳过资源索引。可以先进工作台，在「设置」页手动指定缓存目录后重新扫描。
        </p>
      </section>

      <NAlert
        v-if="error"
        class="gate-error"
        type="error"
        :closable="false"
        title="项目没有打开"
      >
        {{ error }}
      </NAlert>

      <!-- ── ① 新建 ── -->
      <section class="gate-block">
        <div class="gb-head">
          <span class="gb-index">1</span>
          <span class="gb-title">新建项目</span>
          <span class="gb-hint">默认建在程序目录的 projects 下，只需一个名字</span>
        </div>
        <div class="gb-row">
          <NInput
            v-model:value="newName"
            class="gb-input"
            clearable
            placeholder="模组名称，例如「我的汉化补丁」"
            @keyup.enter="create"
          >
            <template #prefix>
              <AppIcon name="edit" :size="13" />
            </template>
          </NInput>
          <NButton type="primary" :loading="busy" :disabled="!newName.trim()" @click="create">
            <template #icon><AppIcon name="fileAdd" :size="13" /></template>
            新建并开始
          </NButton>
        </div>
      </section>

      <!-- ── ② 打开 ── -->
      <section class="gate-block">
        <div class="gb-head">
          <span class="gb-index">2</span>
          <span class="gb-title">打开已有项目</span>
        </div>
        <div class="gb-row">
          <NButton :loading="busy" @click="open">
            <template #icon><AppIcon name="folderOpen" :size="13" /></template>
            选择 .lmeproj 文件…
          </NButton>
          <NButton v-if="startup.lastProjectFile" :loading="busy" @click="continueLast">
            <template #icon><AppIcon name="clock" :size="13" /></template>
            继续上次项目：{{ lastName }}
          </NButton>
        </div>
      </section>

      <!-- ── ③ 最近项目 ── -->
      <section class="gate-block">
        <div class="gb-head">
          <span class="gb-index">3</span>
          <span class="gb-title">最近项目</span>
          <span v-if="startup.recentInfo" class="gb-hint">{{ startup.recentInfo }}</span>
        </div>
        <ul v-if="startup.recentProjects.length > 0" class="recent-list">
          <li v-for="item in startup.recentProjects" :key="item.path">
            <button class="recent-row" :disabled="busy" @click="openRecent(item.path)">
              <AppIcon name="project" :size="14" class="recent-icon" />
              <span class="recent-name">{{ item.name }}</span>
              <span class="recent-path lme-mono lme-ellipsis" :title="item.path">{{ item.path }}</span>
              <span class="recent-time">{{ formatTime(item.lastOpened) }}</span>
            </button>
          </li>
        </ul>
        <p v-else class="recent-empty">
          还没有最近项目：在上面新建一个，或直接选择已有的 .lmeproj 文件。
        </p>
      </section>

      <footer class="gate-foot">
        <AppIcon name="lock" :size="12" />
        <span>此窗口必须完成一次「新建」或「打开」才能继续</span>
      </footer>
    </div>
  </NModal>
</template>

<style scoped>
.gate {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
  max-height: 86vh;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-xl);
  box-shadow: var(--lme-shadow-xl);
}

/* ── 标题 ── */
.gate-head {
  display: flex;
  align-items: flex-start;
  gap: var(--lme-gap-md);
}

.gate-badge {
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

.gate-head-text {
  min-width: 0;
}

.gate-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.gate-sub {
  margin: var(--lme-gap-xs) 0 0;
  font-size: var(--lme-font-size-sm);
  line-height: var(--lme-line-height-relaxed);
  color: var(--lme-text-secondary);
}

/* ── 路径定位 ── */
.gate-paths {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-md);
  background: var(--lme-bg-inset);
  border: 1px solid var(--lme-border-subtle);
  border-radius: var(--lme-radius-lg);
}

.gp-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-sm);
}

.gp-title {
  color: var(--lme-text-muted);
}

.gp-count {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.gp-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.gp-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.gp-icon {
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.gp-row.is-missing .gp-icon {
  color: var(--lme-warning);
}

.gp-label {
  flex-shrink: 0;
  min-width: 132px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

.gp-path {
  flex: 1;
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-primary);
}

.gp-missing {
  flex: 1;
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.gp-note {
  margin: 0;
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
  color: var(--lme-text-muted);
}

.gp-note-error {
  color: var(--lme-error);
}

.gp-note-warn {
  color: var(--lme-warning);
}

.gate-error {
  font-size: var(--lme-font-size-sm);
}

/* ── 三个入口 ── */
.gate-block {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  border: 1px solid var(--lme-border-subtle);
  border-radius: var(--lme-radius-lg);
}

.gb-head {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  min-width: 0;
}

.gb-index {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-full);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
  font-size: var(--lme-font-size-2xs);
  font-weight: var(--lme-font-weight-bold);
}

.gb-title {
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
}

.gb-hint {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.gb-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.gb-input {
  flex: 1;
  min-width: 240px;
  max-width: 420px;
}

/* ── 最近项目 ── */
.recent-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-2xs);
  max-height: 200px;
  overflow-y: auto;
}

.recent-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  width: 100%;
  padding: 6px var(--lme-gap-sm);
  background: none;
  border: 1px solid transparent;
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-primary);
  font-family: inherit;
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.recent-row:hover:not(:disabled) {
  background: var(--lme-bg-hover);
  border-color: var(--lme-surface-hover-border);
}

.recent-row:disabled {
  cursor: default;
  opacity: 0.6;
}

.recent-icon {
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.recent-name {
  flex-shrink: 0;
  min-width: 110px;
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-medium);
}

.recent-path {
  flex: 1;
  min-width: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.recent-time {
  flex-shrink: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.recent-empty {
  margin: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 脚注 ── */
.gate-foot {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}
</style>
