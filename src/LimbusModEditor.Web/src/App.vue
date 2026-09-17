<script setup lang="ts">
import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'

const router = useRouter()
const route = useRoute()
const sidebarCollapsed = ref(false)

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

function navigate(routePath: string) {
  router.push(routePath)
}

function isActive(routePath: string): boolean {
  if (routePath === '/assets') {
    return route.path.startsWith('/assets')
  }
  return route.path === routePath || route.path.startsWith(routePath + '/')
}
</script>

<template>
  <div class="app-shell">
    <!-- 活动栏 -->
    <nav class="activity-bar" :class="{ collapsed: sidebarCollapsed }">
      <div class="activity-bar-header">
        <span class="app-title">Limbus Mod Editor</span>
      </div>
      <ul class="activity-bar-list">
        <li
          v-for="item in navItems"
          :key="item.key"
          class="activity-bar-item"
          :class="{ active: isActive(item.route) }"
          :title="item.label"
          @click="navigate(item.route)"
        >
          <span class="activity-bar-icon">{{ item.icon }}</span>
          <span v-if="!sidebarCollapsed" class="activity-bar-label">{{ item.label }}</span>
        </li>
      </ul>
    </nav>

    <!-- 页面宿主 -->
    <main class="page-host">
      <router-view />
    </main>
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  height: 100%;
  overflow: hidden;
}

.activity-bar {
  width: 180px;
  flex-shrink: 0;
  background: var(--lme-bg-panel);
  border-right: 1px solid var(--lme-border);
  display: flex;
  flex-direction: column;
  transition: width 0.2s ease;
}

.activity-bar.collapsed {
  width: 56px;
}

.activity-bar-header {
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.app-title {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.activity-bar-list {
  list-style: none;
  margin: 0;
  padding: var(--lme-gap-sm);
  flex: 1;
}

.activity-bar-item {
  position: relative;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-md);
  cursor: pointer;
  color: var(--lme-text-secondary);
  transition: background 0.15s, color 0.15s;
}

.activity-bar-item:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.activity-bar-item.active {
  background: var(--lme-bg-active, var(--lme-bg-hover));
  color: var(--lme-text-primary);
  font-weight: 600;
}

.activity-bar-item.active::before {
  content: '';
  position: absolute;
  left: 0;
  top: 50%;
  transform: translateY(-50%);
  width: 3px;
  height: 60%;
  background: var(--lme-accent, #4a9eff);
  border-radius: 0 2px 2px 0;
}

.activity-bar-icon {
  font-size: var(--lme-font-size-lg);
  width: 24px;
  text-align: center;
}

.activity-bar-label {
  font-size: var(--lme-font-size-sm);
}

.page-host {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
</style>
