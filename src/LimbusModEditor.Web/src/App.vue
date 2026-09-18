<script setup lang="ts">
// v3 外壳（组件库版 Naive UI）：
//   顶部命令栏（品牌 + 全局命令面板入口 + 常驻入口）+ 工作区 tab 条
//   命令面板由 NModal + NInput 实现；主题从 tokens.css 派生（见 src/theme/naiveTheme.ts）
// 路由与 v1/v2 完全一致，功能无损
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import {
  NConfigProvider,
  NDialogProvider,
  NMessageProvider,
  NNotificationProvider,
  NTabs,
  NTab,
  NTooltip,
  NButton,
  NInput,
  zhCN,
  dateZhCN,
} from 'naive-ui'
import CommandPalette from '@/components/CommandPalette.vue'
import { buildThemeOverrides, lmeDarkTheme } from '@/theme/naiveTheme'

const router = useRouter()
const route = useRoute()

const navItems = [
  { key: 'assets', label: '资源', icon: '📦', route: '/assets' },
  { key: 'bank', label: '音频', icon: '🎵', route: '/bank' },
  { key: 'text', label: '文本', icon: '📝', route: '/text' },
  { key: 'static', label: '静态数据', icon: '📊', route: '/static' },
  { key: 'export', label: '导出', icon: '📤', route: '/export' },
  { key: 'project', label: '项目', icon: '📁', route: '/project' },
  { key: 'settings', label: '设置', icon: '⚙️', route: '/settings' },
  { key: 'help', label: '帮助', icon: '❓', route: '/help' },
  { key: 'wiki', label: '维基', icon: '📖', route: '/wiki' },
]

const paletteOpen = ref(false)

const isWikiZone = computed(() => route.path.startsWith('/wiki'))

/** 库主题：维基区主色用金色，其余用紫色（两者都来自 tokens.css） */
const themeOverrides = computed(() =>
  buildThemeOverrides(isWikiZone.value ? '--wiki-accent' : '--lme-accent'),
)

const activeKey = computed(() => {
  const path = route.path
  if (path.startsWith('/wiki')) return 'wiki'
  const hit = navItems.find((i) => path === i.route || path.startsWith(i.route + '/'))
  return hit?.key ?? 'assets'
})

function onTabChange(key: string) {
  const item = navItems.find((i) => i.key === key)
  if (item) void router.push(item.route)
}

function navigate(routePath: string) {
  void router.push(routePath)
}

function onGlobalKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault()
    paletteOpen.value = !paletteOpen.value
  }
}

onMounted(() => window.addEventListener('keydown', onGlobalKeydown, true))
onUnmounted(() => window.removeEventListener('keydown', onGlobalKeydown, true))
</script>

<template>
  <NConfigProvider
    :theme="lmeDarkTheme"
    :theme-overrides="themeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <NMessageProvider>
      <NDialogProvider>
        <NNotificationProvider>
          <div class="app-shell">
            <!-- 顶部命令栏 -->
            <header class="topbar">
              <div class="brand" title="回到资源工作台" @click="navigate('/assets')">
                <span class="brand-mark">LME</span>
                <span class="brand-name">Limbus Mod Editor</span>
              </div>

              <!-- 命令面板入口（Naive UI Input，只读 + 点击唤起） -->
              <div class="command-trigger" @click="paletteOpen = true">
                <NInput
                  readonly
                  class="command-input"
                  placeholder="搜索或跳转：工作台、维基分类…"
                >
                  <template #prefix>
                    <span class="command-trigger-icon">🔍</span>
                  </template>
                  <template #suffix>
                    <kbd class="command-trigger-kbd">Ctrl+K</kbd>
                  </template>
                </NInput>
              </div>

              <div class="topbar-actions">
                <NTooltip placement="bottom" :show-arrow="false">
                  <template #trigger>
                    <NButton quaternary circle @click="navigate('/help')">❓</NButton>
                  </template>
                  帮助
                </NTooltip>
                <NTooltip placement="bottom" :show-arrow="false">
                  <template #trigger>
                    <NButton quaternary circle @click="navigate('/settings')">⚙️</NButton>
                  </template>
                  设置
                </NTooltip>
              </div>
            </header>

            <!-- 工作区 tab 条 -->
            <nav class="workspace-tabs">
              <NTabs
                type="line"
                size="small"
                :value="activeKey"
                class="workspace-tabs-inner"
                @update:value="onTabChange"
              >
                <NTab v-for="item in navItems" :key="item.key" :name="item.key">
                  <span class="workspace-tab-icon">{{ item.icon }}</span>
                  <span class="workspace-tab-label">{{ item.label }}</span>
                </NTab>
              </NTabs>
            </nav>

            <!-- 页面宿主 -->
            <main class="page-host">
              <router-view />
            </main>

            <!-- 全局命令面板 -->
            <CommandPalette v-model:open="paletteOpen" />
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
}

/* ── 顶部命令栏 ── */
.topbar {
  height: var(--lme-topbar-height);
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-lg);
  padding: 0 var(--lme-gap-md);
  background: var(--lme-topbar-bg);
  border-bottom: 1px solid var(--lme-topbar-border);
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  cursor: pointer;
  user-select: none;
  flex-shrink: 0;
}

.brand-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border-radius: var(--lme-radius-md);
  background: var(--lme-brand-mark-bg);
  color: var(--lme-brand-mark-text);
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-bold);
  letter-spacing: 0.5px;
}

.brand-name {
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  white-space: nowrap;
}

.command-trigger {
  flex: 1;
  max-width: 520px;
  margin: 0 auto;
  cursor: pointer;
}

.command-trigger :deep(.n-input) {
  background: var(--lme-cmdbar-bg);
  border-radius: var(--lme-radius-full);
  cursor: pointer;
}

.command-trigger :deep(.n-input:hover) {
  border-color: var(--lme-accent);
}

.command-trigger-icon {
  font-size: var(--lme-font-size-sm);
  opacity: 0.8;
}

.command-trigger-kbd {
  padding: 1px 6px;
  background: var(--lme-cmdbar-kbd-bg);
  border: 1px solid var(--lme-cmdbar-kbd-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-cmdbar-kbd-text);
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-mono);
  white-space: nowrap;
}

.topbar-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  flex-shrink: 0;
}

/* ── 工作区 tab 条 ── */
.workspace-tabs {
  height: var(--lme-tabbar-height);
  flex-shrink: 0;
  display: flex;
  align-items: stretch;
  padding: 0 var(--lme-gap-sm);
  background: var(--lme-tabbar-bg);
  border-bottom: 1px solid var(--lme-tabbar-border);
  overflow: hidden;
}

.workspace-tabs-inner {
  width: 100%;
}

.workspace-tabs :deep(.n-tabs-nav) {
  height: var(--lme-tabbar-height);
}

.workspace-tab-icon {
  font-size: var(--lme-font-size-md);
  margin-right: var(--lme-gap-xs);
}

/* ── 页面宿主 ── */
.page-host {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  min-height: 0;
}
</style>
