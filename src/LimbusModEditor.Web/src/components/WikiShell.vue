<script setup lang="ts">
// 维基页面布局外壳：侧边分类导航 + 内容区

import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'

const router = useRouter()
const route = useRoute()

interface BreadcrumbItem {
  label: string
  route: string
}

defineProps<{
  currentPage?: string
  breadcrumbs?: BreadcrumbItem[]
}>()

// 11 个分类 + 概览
const categoryNav = [
  { key: 'overview', label: '概览', icon: '🏠', route: '/wiki' },
  { key: 'persona', label: '人格', icon: '🎭', route: '/wiki/category/persona' },
  { key: 'enemy', label: '敌方单位', icon: '👹', route: '/wiki/category/enemy' },
  { key: 'abnormality', label: '异想体', icon: '🌀', route: '/wiki/category/abnormality' },
  { key: 'ego', label: 'E.G.O 装备', icon: '⚔️', route: '/wiki/category/ego' },
  { key: 'ego_gift', label: 'E.G.O 饰品', icon: '💍', route: '/wiki/category/ego_gift' },
  { key: 'announcer', label: '播报员', icon: '📢', route: '/wiki/category/announcer' },
  { key: 'story', label: '剧情', icon: '📖', route: '/wiki/category/story' },
  { key: 'stage', label: '关卡', icon: '🗺️', route: '/wiki/category/stage' },
  { key: 'item', label: '物品', icon: '🎒', route: '/wiki/category/item' },
  { key: 'mechanism', label: '机制', icon: '⚙️', route: '/wiki/category/mechanism' },
  { key: 'keyword', label: '关键词', icon: '🔑', route: '/wiki/category/keyword' },
]

const sidebarCollapsed = ref(false)
const searchQuery = ref('')

function navigate(path: string) {
  router.push(path)
}

function isActive(routePath: string): boolean {
  if (routePath === '/wiki') return route.path === '/wiki'
  return route.path.startsWith(routePath)
}

/** 侧栏搜索：回车跳搜索路由（复用全局搜索页，不再发 emit） */
function submitSearch() {
  const q = searchQuery.value.trim()
  if (!q) return
  router.push({ path: '/wiki/search', query: { q } })
  searchQuery.value = ''
}
</script>

<template>
  <div class="wiki-shell">
    <!-- 维基侧边栏 -->
    <nav class="wiki-sidebar" :class="{ collapsed: sidebarCollapsed }">
      <div class="sidebar-header">
        <span v-if="!sidebarCollapsed" class="sidebar-title">维基导航</span>
        <button class="sidebar-toggle" :title="sidebarCollapsed ? '展开' : '折叠'" @click="sidebarCollapsed = !sidebarCollapsed">
          {{ sidebarCollapsed ? '▶' : '◀' }}
        </button>
      </div>
      <!-- 侧栏搜索：回车跳转搜索页（原 TODO 死代码已接通） -->
      <div v-if="!sidebarCollapsed" class="sidebar-search">
        <input
          v-model="searchQuery"
          class="sidebar-search-input"
          type="text"
          placeholder="搜索维基…"
          @keyup.enter="submitSearch"
        />
      </div>
      <ul class="sidebar-nav">
        <li
          v-for="item in categoryNav"
          :key="item.key"
          class="sidebar-nav-item"
          :class="{ active: isActive(item.route) }"
          :title="item.label"
          @click="navigate(item.route)"
        >
          <span class="nav-icon">{{ item.icon }}</span>
          <span v-if="!sidebarCollapsed" class="nav-label">{{ item.label }}</span>
        </li>
      </ul>
    </nav>

    <!-- 内容区 -->
    <div class="wiki-content">
      <!-- 面包屑 -->
      <nav v-if="breadcrumbs && breadcrumbs.length > 0" class="wiki-breadcrumb">
        <span v-for="(crumb, i) in breadcrumbs" :key="i" class="breadcrumb-item">
          <span class="breadcrumb-link" :class="{ current: i === breadcrumbs.length - 1 }" @click="i < breadcrumbs.length - 1 && navigate(crumb.route)">
            {{ crumb.label }}
          </span>
          <span v-if="i < breadcrumbs.length - 1" class="breadcrumb-sep">/</span>
        </span>
      </nav>

      <!-- 插槽 -->
      <div class="wiki-slot">
        <slot />
      </div>
    </div>
  </div>
</template>

<style scoped>
.wiki-shell { display: flex; height: 100%; overflow: hidden; }

/* ── 侧边栏 ── */
.wiki-sidebar { width: 180px; flex-shrink: 0; background: var(--lme-bg-panel); border-right: 1px solid var(--lme-border); display: flex; flex-direction: column; transition: width 0.2s; }
.wiki-sidebar.collapsed { width: 56px; }
.sidebar-header { display: flex; align-items: center; justify-content: space-between; padding: var(--lme-gap-sm) var(--lme-gap-md); border-bottom: 1px solid var(--lme-border); }
.sidebar-title { font-size: var(--lme-font-size-sm); font-weight: 600; color: var(--wiki-section-title); letter-spacing: 1px; }
.sidebar-toggle { background: none; border: none; color: var(--lme-text-muted); cursor: pointer; font-size: 12px; }

.sidebar-search { padding: var(--lme-gap-sm) var(--lme-gap-md) 0; }
.sidebar-search-input { width: 100%; padding: var(--lme-gap-xs) var(--lme-gap-sm); background: var(--wiki-shell-search-bg); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-sm); color: var(--lme-text-primary); font-size: var(--lme-font-size-xs); font-family: var(--lme-font-family); }
.sidebar-search-input:focus { outline: none; border-color: var(--wiki-accent); }

.sidebar-nav { list-style: none; margin: 0; padding: var(--lme-gap-sm); flex: 1; overflow-y: auto; }
.sidebar-nav-item { position: relative; display: flex; align-items: center; gap: var(--lme-gap-sm); padding: var(--lme-gap-sm) var(--lme-gap-md); border-radius: var(--lme-radius-md); cursor: pointer; color: var(--lme-text-secondary); transition: background 0.15s, color 0.15s; }
.sidebar-nav-item:hover { background: var(--lme-bg-hover); color: var(--lme-text-primary); }
.sidebar-nav-item.active { background: var(--wiki-nav-active-bg); color: var(--wiki-nav-active-text); font-weight: 600; }
.sidebar-nav-item.active::before { content: ''; position: absolute; left: 0; top: 6px; bottom: 6px; width: 3px; border-radius: 2px; background: var(--wiki-nav-active-bar); }
.nav-icon { font-size: var(--lme-font-size-lg); width: 24px; text-align: center; }
.nav-label { font-size: var(--lme-font-size-sm); }

/* ── 内容区 ── */
.wiki-content { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
.wiki-breadcrumb { display: flex; align-items: center; gap: var(--lme-gap-xs); padding: var(--lme-gap-sm) var(--lme-gap-lg); border-bottom: 1px solid var(--lme-border); font-size: var(--lme-font-size-sm); background: var(--lme-bg-panel); }
.breadcrumb-item { display: flex; align-items: center; gap: var(--lme-gap-xs); }
.breadcrumb-link { color: var(--wiki-breadcrumb-link); cursor: pointer; }
.breadcrumb-link:hover { text-decoration: underline; }
.breadcrumb-link.current { color: var(--wiki-breadcrumb-text); cursor: default; }
.breadcrumb-sep { color: var(--lme-text-muted); }

.wiki-slot { flex: 1; overflow: auto; }
</style>
