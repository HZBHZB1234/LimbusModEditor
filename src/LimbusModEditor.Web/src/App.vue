<script setup lang="ts">
// v2 外壳：顶部命令栏（品牌 + Ctrl+K 命令面板 + 常驻入口）
//        + 工作区 tab 条（横向 tab，维基工作区激活时金色下划线）
// 替代 v1 的左侧窄活动栏；全部路由保持原路径，功能无损
import { ref, onMounted, onUnmounted } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import CommandPalette from '@/components/CommandPalette.vue'

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

function navigate(routePath: string) {
  router.push(routePath)
}

function isActive(routePath: string): boolean {
  if (routePath === '/assets') {
    return route.path.startsWith('/assets')
  }
  return route.path === routePath || route.path.startsWith(routePath + '/')
}

/** 维基工作区激活时 tab 下划线换维基金（视觉上区分「资料区」与「工作区」） */
function tabAccent(routePath: string): string {
  return routePath.startsWith('/wiki') && isActive(routePath)
    ? 'var(--lme-tab-active-underline-wiki)'
    : 'var(--lme-tab-active-underline)'
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
  <div class="app-shell">
    <!-- 顶部命令栏 -->
    <header class="topbar">
      <div class="brand" @click="navigate('/assets')" title="回到资源工作台">
        <span class="brand-mark">LME</span>
        <span class="brand-name">Limbus Mod Editor</span>
      </div>

      <button
        class="command-trigger"
        title="全局搜索与跳转（Ctrl+K）"
        @click="paletteOpen = true"
      >
        <span class="command-trigger-icon">🔍</span>
        <span class="command-trigger-text">搜索或跳转：工作台、维基分类…</span>
        <kbd class="command-trigger-kbd">Ctrl+K</kbd>
      </button>

      <div class="topbar-actions">
        <button
          class="topbar-icon-btn"
          title="帮助"
          @click="navigate('/help')"
        >
          ❓
        </button>
        <button
          class="topbar-icon-btn"
          title="设置"
          @click="navigate('/settings')"
        >
          ⚙️
        </button>
      </div>
    </header>

    <!-- 工作区 tab 条 -->
    <nav class="workspace-tabs">
      <button
        v-for="item in navItems"
        :key="item.key"
        class="workspace-tab"
        :class="{ active: isActive(item.route), 'wiki-tab': item.route.startsWith('/wiki') }"
        :style="{ '--tab-accent': tabAccent(item.route) }"
        @click="navigate(item.route)"
      >
        <span class="workspace-tab-icon">{{ item.icon }}</span>
        <span class="workspace-tab-label">{{ item.label }}</span>
      </button>
    </nav>

    <!-- 页面宿主 -->
    <main class="page-host">
      <router-view />
    </main>

    <!-- 全局命令面板 -->
    <CommandPalette :open="paletteOpen" @close="paletteOpen = false" />
  </div>
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
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin: 0 auto;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-cmdbar-bg);
  border: 1px solid var(--lme-cmdbar-border);
  border-radius: var(--lme-radius-full);
  color: var(--lme-cmdbar-placeholder);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  cursor: pointer;
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.command-trigger:hover {
  border-color: var(--lme-accent);
}

.command-trigger-icon {
  font-size: var(--lme-font-size-sm);
  opacity: 0.8;
}

.command-trigger-text {
  flex: 1;
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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

.topbar-icon-btn {
  width: 30px;
  height: 30px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: none;
  border: none;
  border-radius: var(--lme-radius-md);
  font-size: var(--lme-font-size-md);
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.topbar-icon-btn:hover {
  background: var(--lme-topbar-icon-hover-bg);
}

/* ── 工作区 tab 条 ── */
.workspace-tabs {
  height: var(--lme-tabbar-height);
  flex-shrink: 0;
  display: flex;
  align-items: stretch;
  gap: var(--lme-gap-xs);
  padding: 0 var(--lme-gap-md);
  background: var(--lme-tabbar-bg);
  border-bottom: 1px solid var(--lme-tabbar-border);
  overflow-x: auto;
}

.workspace-tab {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: 0 var(--lme-gap-md);
  background: none;
  border: none;
  color: var(--lme-tab-text);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  cursor: pointer;
  white-space: nowrap;
  transition: color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.workspace-tab:hover {
  color: var(--lme-tab-text-hover);
  background: var(--lme-tab-hover-bg);
}

.workspace-tab.active {
  color: var(--lme-tab-active-text);
  font-weight: var(--lme-font-weight-medium);
}

.workspace-tab.active::after {
  content: '';
  position: absolute;
  left: var(--lme-gap-sm);
  right: var(--lme-gap-sm);
  bottom: 0;
  height: 2px;
  border-radius: 2px 2px 0 0;
  background: var(--tab-accent);
}

.workspace-tab-icon {
  font-size: var(--lme-font-size-md);
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
