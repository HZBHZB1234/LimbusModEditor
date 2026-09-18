<script setup lang="ts">
/**
 * 应用外壳（VS Code 式布局）。
 *
 *  ┌──────────────────────────────────────────────┐
 *  │ 工具条：品牌 + 命令面板入口 + 外观控制            │
 *  ├────┬─────────────────────────────────────────┤
 *  │活动 │                                         │
 *  │栏  │           页面宿主（router-view）          │
 *  │48px│                                         │
 *  ├────┴─────────────────────────────────────────┤
 *  │ 状态栏：项目 / 进度 / 通知 / 连接 / 版本          │
 *  └──────────────────────────────────────────────┘
 *
 * 导航收敛到左侧活动栏（唯一入口），不再有工作区 Tab 条。
 * 路由、query 深链、各页 IPC 调用一律未改。
 *
 * 启动流程（2026-09-19 新增，见 stores/startup.ts）：
 *   挂载即自动跑「路径定位 → 强制选择项目 → 自动扫描」，三步走完之前
 *   工作区被锁定（`.locked` 不吃指针事件 + 全局快捷键短路），两个模态窗
 *   由 ProjectGateDialog / StartupScanDialog 承担。
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NConfigProvider,
  NDialogProvider,
  NMessageProvider,
  NNotificationProvider,
  NTooltip,
  zhCN,
  dateZhCN,
} from 'naive-ui'
import CommandPalette from '@/components/CommandPalette.vue'
import StatusBar from '@/components/StatusBar.vue'
import AppearanceControl from '@/components/AppearanceControl.vue'
import AppIcon from '@/components/AppIcon.vue'
import ProjectGateDialog from '@/components/ProjectGateDialog.vue'
import StartupScanDialog from '@/components/StartupScanDialog.vue'
import type { IconName } from '@/components/icons'
import { useAppearanceStore } from '@/stores/appearance'
import { useStatusStore } from '@/stores/status'
import { useStartupStore } from '@/stores/startup'

const router = useRouter()
const route = useRoute()
const appearance = useAppearanceStore()
const status = useStatusStore()
const startup = useStartupStore()

interface NavItem {
  key: string
  label: string
  icon: IconName
  route: string
  /** 一句话说明（悬停提示用） */
  hint: string
  /** 快捷键序号（Ctrl+N） */
  order: number
}

/** 主模块（活动栏上半部分） */
const modules: NavItem[] = [
  {
    key: 'assets',
    label: '资源',
    icon: 'assets',
    route: '/assets',
    hint: '浏览、预览与替换游戏资源',
    order: 1,
  },
  {
    key: 'bank',
    label: '音频',
    icon: 'audio',
    route: '/bank',
    hint: '试听与替换音频库中的音效',
    order: 2,
  },
  {
    key: 'text',
    label: '文本',
    icon: 'text',
    route: '/text',
    hint: '编辑游戏语言文本并生成补丁',
    order: 3,
  },
  {
    key: 'static',
    label: '静态数据',
    icon: 'staticData',
    route: '/static',
    hint: '查看与修改静态数据表',
    order: 4,
  },
  {
    key: 'export',
    label: '导出',
    icon: 'export',
    route: '/export',
    hint: '把改动打包成可用的模组',
    order: 5,
  },
  {
    key: 'project',
    label: '项目',
    icon: 'project',
    route: '/project',
    hint: '新建 / 打开 / 保存模组项目',
    order: 6,
  },
  {
    key: 'wiki',
    label: '维基',
    icon: 'wiki',
    route: '/wiki',
    hint: '查阅游戏资料，并直达对应资源',
    order: 7,
  },
]

/** 次要入口（活动栏下半部分） */
const secondary: NavItem[] = [
  { key: 'help', label: '帮助', icon: 'help', route: '/help', hint: '使用指南与快捷键', order: 0 },
  { key: 'settings', label: '设置', icon: 'settings', route: '/settings', hint: '路径、外观与偏好', order: 0 },
]

const paletteOpen = ref(false)

const activeKey = computed(() => {
  const path = route.path
  const hit = [...modules, ...secondary].find(
    (i) => path === i.route || path.startsWith(i.route + '/'),
  )
  return hit?.key ?? 'assets'
})

/** 维基区（含子路由）——用于在工具条上提示当前区域 */
const currentModule = computed(() =>
  [...modules, ...secondary].find((i) => i.key === activeKey.value),
)

function navigate(routePath: string) {
  if (route.path === routePath) return
  void router.push(routePath)
}

function togglePalette() {
  paletteOpen.value = !paletteOpen.value
}

function onGlobalKeydown(e: KeyboardEvent) {
  const mod = e.ctrlKey || e.metaKey

  // Ctrl+K / Ctrl+Shift+P：命令面板
  if (mod && !e.altKey && (e.key.toLowerCase() === 'k' || (e.shiftKey && e.key.toLowerCase() === 'p'))) {
    e.preventDefault()
    togglePalette()
    return
  }

  // Ctrl+,：设置
  if (mod && e.key === ',') {
    e.preventDefault()
    navigate('/settings')
    return
  }

  // Ctrl+1..7：切换主模块
  if (mod && !e.shiftKey && !e.altKey && /^[1-9]$/.test(e.key)) {
    const target = modules.find((m) => m.order === Number(e.key))
    if (target) {
      e.preventDefault()
      navigate(target.route)
    }
    return
  }

  // Alt+← / Alt+→：前进后退
  if (e.altKey && !mod && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
    e.preventDefault()
    if (e.key === 'ArrowLeft') router.back()
    else router.forward()
  }
}

onMounted(() => {
  window.addEventListener('keydown', onGlobalKeydown, true)
  // 全局状态订阅（宿主事件 + 在途请求）只挂一次
  status.attach()
  // 启动流程：自动路径定位 → 强制选择项目 → 自动扫描（幂等，见 stores/startup.ts）
  void startup.start()
})
onUnmounted(() => window.removeEventListener('keydown', onGlobalKeydown, true))
</script>

<template>
  <NConfigProvider
    :theme="appearance.baseTheme"
    :theme-overrides="appearance.themeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <NMessageProvider>
      <NDialogProvider>
        <NNotificationProvider>
          <div class="app-shell">
            <!-- ══ 工具条 ══ -->
            <header class="toolbar">
              <div class="brand" title="Limbus Mod Editor" @click="navigate('/assets')">
                <span class="brand-mark">LME</span>
                <span class="brand-name">Limbus Mod Editor</span>
                <span v-if="currentModule" class="brand-context">{{ currentModule.label }}</span>
              </div>

              <button class="command-trigger" title="搜索或跳转（Ctrl+K）" @click="togglePalette">
                <AppIcon name="search" :size="14" class="ct-icon" />
                <span class="ct-text">搜索或跳转：工作台、维基分类…</span>
                <kbd class="lme-kbd ct-kbd">Ctrl K</kbd>
              </button>

              <div class="toolbar-actions">
                <AppearanceControl />
              </div>
            </header>

            <!-- ══ 主体：活动栏 + 页面 ══ -->
            <div class="app-body">
              <nav class="activitybar" aria-label="主导航">
                <div class="ab-group">
                  <NTooltip
                    v-for="item in modules"
                    :key="item.key"
                    placement="right"
                    :show-arrow="false"
                    :delay="300"
                  >
                    <template #trigger>
                      <button
                        class="ab-item"
                        :class="{ active: activeKey === item.key }"
                        :aria-current="activeKey === item.key ? 'page' : undefined"
                        @click="navigate(item.route)"
                      >
                        <AppIcon :name="item.icon" :size="20" :stroke="1.7" />
                        <span class="ab-indicator" />
                      </button>
                    </template>
                    <div class="ab-tip">
                      <span class="ab-tip-title">{{ item.label }}</span>
                      <span class="ab-tip-hint">{{ item.hint }}</span>
                      <span v-if="item.order > 0" class="ab-tip-key">
                        快捷键 <kbd class="lme-kbd">Ctrl {{ item.order }}</kbd>
                      </span>
                    </div>
                  </NTooltip>
                </div>

                <div class="ab-spacer" />

                <div class="ab-group">
                  <NTooltip
                    v-for="item in secondary"
                    :key="item.key"
                    placement="right"
                    :show-arrow="false"
                    :delay="300"
                  >
                    <template #trigger>
                      <button
                        class="ab-item"
                        :class="{ active: activeKey === item.key }"
                        :aria-current="activeKey === item.key ? 'page' : undefined"
                        @click="navigate(item.route)"
                      >
                        <AppIcon :name="item.icon" :size="20" :stroke="1.7" />
                        <span class="ab-indicator" />
                      </button>
                    </template>
                    <div class="ab-tip">
                      <span class="ab-tip-title">{{ item.label }}</span>
                      <span class="ab-tip-hint">{{ item.hint }}</span>
                    </div>
                  </NTooltip>
                </div>
              </nav>

              <main class="page-host">
                <!-- 启动流程（定位 → 选项目 → 扫描）走完之前不挂载工作台：
                     ① 这是「强制先选项目」最硬的一道锁；
                     ② 各页挂载即查询索引，等扫描结束再挂载，首屏就是扫完的数据，
                        不用为「扫完还要刷新」再造一套跨页失效通知。 -->
                <template v-if="!startup.blocking">
                  <router-view />
                </template>
                <div v-else class="boot-placeholder">
                  <span class="bp-spinner">
                    <AppIcon name="loader" :size="22" />
                  </span>
                  <p class="bp-title">正在准备启动</p>
                  <p class="bp-desc">自动定位目录 → 选择项目 → 扫描游戏资源</p>
                </div>
              </main>
            </div>

            <!-- ══ 状态栏 ══ -->
            <StatusBar />

            <!-- ══ 命令面板 ══ -->
            <CommandPalette v-model:open="paletteOpen" />

            <!-- ══ 启动流程 ══
                 ① 路径定位在挂载时自动跑；② 项目门不可关闭；③ 扫描模态显示进度。
                 两个窗都在 stores/startup.ts 的 phase 驱动下出现/消失。 -->
            <ProjectGateDialog />
            <StartupScanDialog />
          </div>
        </NNotificationProvider>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>

<style scoped>
.app-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  background: var(--lme-bg-base);
}

/* 启动流程未走完（项目门 / 扫描窗开着）：工作区整块不吃指针事件。
   模态窗本体由 naive-ui Teleport 到 body，不在 .app-shell 内，不受影响。 */
.app-shell.locked {
  pointer-events: none;
}

/* ══ 工具条 ══ */
.toolbar {
  height: var(--lme-toolbar-height);
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: 0 var(--lme-gap-md);
  background: var(--lme-toolbar-bg);
  border-bottom: 1px solid var(--lme-toolbar-border);
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-shrink: 0;
  cursor: pointer;
  user-select: none;
}

.brand-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: var(--lme-radius-md);
  background: var(--lme-brand-mark-bg);
  color: var(--lme-brand-mark-text);
  font-size: var(--lme-font-size-2xs);
  font-weight: var(--lme-font-weight-bold);
  letter-spacing: 0.4px;
}

.brand-name {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  white-space: nowrap;
}

/* 当前区域：跟随活动栏，给出「我在哪」 */
.brand-context {
  padding: 1px 7px;
  border-radius: var(--lme-radius-full);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
  font-size: var(--lme-font-size-2xs);
  font-weight: var(--lme-font-weight-medium);
  white-space: nowrap;
}

/* ── 命令面板入口 ── */
.command-trigger {
  flex: 1;
  max-width: 560px;
  margin: 0 auto;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  height: var(--lme-control-height-md);
  padding: 0 var(--lme-gap-sm);
  border: 1px solid var(--lme-cmdbar-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-cmdbar-bg);
  color: var(--lme-cmdbar-placeholder);
  font-family: inherit;
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.command-trigger:hover {
  border-color: var(--lme-accent-border);
  background: var(--lme-bg-elevated);
}

.ct-icon {
  color: var(--lme-text-muted);
}

.ct-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: left;
}

.ct-kbd {
  flex-shrink: 0;
}

.toolbar-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  flex-shrink: 0;
}

/* ══ 主体 ══ */
.app-body {
  flex: 1;
  display: flex;
  min-height: 0;
  overflow: hidden;
}

/* ── 活动栏 ── */
.activitybar {
  width: var(--lme-activitybar-width);
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  align-items: stretch;
  padding: var(--lme-gap-xs) 0;
  background: var(--lme-activitybar-bg);
  border-right: 1px solid var(--lme-activitybar-border);
}

.ab-group {
  display: flex;
  flex-direction: column;
  align-items: stretch;
}

.ab-spacer {
  flex: 1;
}

.ab-item {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 42px;
  padding: 0;
  border: none;
  background: none;
  color: var(--lme-activitybar-item-text);
  cursor: pointer;
  transition: color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.ab-item:hover {
  color: var(--lme-activitybar-item-hover-text);
  background: var(--lme-activitybar-item-hover-bg);
}

.ab-item.active {
  color: var(--lme-activitybar-item-active-text);
  background: var(--lme-activitybar-item-active-bg);
}

/* 左侧激活指示条（VS Code 特征） */
.ab-indicator {
  position: absolute;
  left: 0;
  top: 50%;
  width: 2px;
  height: 0;
  border-radius: 0 var(--lme-radius-full) var(--lme-radius-full) 0;
  background: var(--lme-activitybar-indicator);
  transform: translateY(-50%);
  transition: height var(--lme-dur-base) var(--lme-ease-emphasized);
}

.ab-item.active .ab-indicator {
  height: 22px;
}

/* ── 活动栏提示气泡 ── */
.ab-tip {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-width: 240px;
}

.ab-tip-title {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.ab-tip-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  line-height: var(--lme-line-height-normal);
}

.ab-tip-key {
  display: flex;
  align-items: center;
  gap: 5px;
  margin-top: 2px;
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-text-muted);
}

/* ── 页面宿主 ── */
.page-host {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* ── 启动占位（启动流程未走完时取代工作台；正常情况下被模态遮罩盖住） ── */
.boot-placeholder {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  color: var(--lme-text-muted);
}

.bp-spinner {
  display: inline-flex;
  color: var(--lme-accent);
  animation: lme-spin 1.1s linear infinite;
}

.bp-title {
  margin: 0;
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-secondary);
}

.bp-desc {
  margin: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}
</style>
