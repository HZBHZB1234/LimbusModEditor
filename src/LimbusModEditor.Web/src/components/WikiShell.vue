<script setup lang="ts">
// 维基页面布局外壳：侧边分类导航 + 内容区
// ui-redesign r6：分类图标由 emoji 改为 AppIcon（IconName 语义名），配色统一到全局令牌。

import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { NInput, NTooltip } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'

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

interface NavItem {
  key: string
  label: string
  icon: IconName
  route: string
}

// 11 个分类 + 概览 + Spine 总览
const categoryNav: NavItem[] = [
  { key: 'overview', label: '概览', icon: 'wikiOverview', route: '/wiki' },
  { key: 'persona', label: '人格', icon: 'wikiPersona', route: '/wiki/category/persona' },
  { key: 'enemy', label: '敌方单位', icon: 'wikiEnemy', route: '/wiki/category/enemy' },
  { key: 'abnormality', label: '异想体', icon: 'wikiAbnormality', route: '/wiki/category/abnormality' },
  { key: 'ego', label: 'E.G.O 装备', icon: 'wikiEgo', route: '/wiki/category/ego' },
  { key: 'ego_gift', label: 'E.G.O 饰品', icon: 'wikiEgoGift', route: '/wiki/category/ego_gift' },
  { key: 'announcer', label: '播报员', icon: 'wikiAnnouncer', route: '/wiki/category/announcer' },
  { key: 'story', label: '剧情', icon: 'wikiStory', route: '/wiki/category/story' },
  { key: 'stage', label: '关卡', icon: 'wikiStage', route: '/wiki/category/stage' },
  { key: 'item', label: '物品', icon: 'wikiItem', route: '/wiki/category/item' },
  { key: 'mechanism', label: '机制', icon: 'wikiMechanism', route: '/wiki/category/mechanism' },
  { key: 'keyword', label: '关键词', icon: 'wikiKeyword', route: '/wiki/category/keyword' },
  // Spine 总览：全库 Spine 挂点（含从未被页面绑定、界面上从来见不到的那批）
  { key: 'spine', label: 'Spine 总览', icon: 'wikiSpine', route: '/wiki/spine' },
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
        <NTooltip placement="right" :show-arrow="false">
          <template #trigger>
            <button
              class="sidebar-toggle"
              :aria-label="sidebarCollapsed ? '展开导航' : '折叠导航'"
              @click="sidebarCollapsed = !sidebarCollapsed"
            >
              <AppIcon :name="sidebarCollapsed ? 'chevronRight' : 'chevronLeft'" :size="14" />
            </button>
          </template>
          {{ sidebarCollapsed ? '展开导航' : '折叠导航' }}
        </NTooltip>
      </div>
      <!-- 侧栏搜索：回车跳转搜索页（原 TODO 死代码已接通） -->
      <div v-if="!sidebarCollapsed" class="sidebar-search">
        <NInput
          v-model:value="searchQuery"
          class="sidebar-search-input"
          size="small"
          placeholder="搜索维基…"
          @keyup.enter="submitSearch"
        >
          <template #prefix>
            <AppIcon name="search" :size="13" />
          </template>
        </NInput>
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
          <span class="nav-icon"><AppIcon :name="item.icon" :size="16" /></span>
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
          <AppIcon
            v-if="i < breadcrumbs.length - 1"
            class="breadcrumb-sep"
            name="chevronRight"
            :size="12"
          />
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
.wiki-sidebar { width: 180px; flex-shrink: 0; background: var(--lme-bg-panel); border-right: 1px solid var(--lme-border); display: flex; flex-direction: column; transition: width var(--lme-dur-base) var(--lme-ease-standard); }
.wiki-sidebar.collapsed { width: 56px; }
.sidebar-header { display: flex; align-items: center; justify-content: space-between; padding: var(--lme-gap-sm) var(--lme-gap-md); border-bottom: 1px solid var(--lme-border); min-height: var(--lme-toolbar-height); }
.wiki-sidebar.collapsed .sidebar-header { justify-content: center; }
.sidebar-title { font-size: var(--lme-font-size-sm); font-weight: var(--lme-font-weight-semibold); color: var(--lme-text-primary); letter-spacing: var(--lme-tracking-caps); }
.sidebar-toggle { display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; flex-shrink: 0; padding: 0; background: none; border: none; border-radius: var(--lme-radius-xs); color: var(--lme-text-muted); cursor: pointer; transition: background var(--lme-dur-fast) var(--lme-ease-standard), color var(--lme-dur-fast) var(--lme-ease-standard); }
.sidebar-toggle:hover { background: var(--lme-bg-hover); color: var(--lme-text-primary); }

.sidebar-search { padding: var(--lme-gap-sm) var(--lme-gap-md) 0; }
.sidebar-search-input { width: 100%; }
.sidebar-search-input :deep(.n-input__input-el) { font-size: var(--lme-font-size-xs); }

.sidebar-nav { list-style: none; margin: 0; padding: var(--lme-gap-sm); flex: 1; overflow-y: auto; }
.sidebar-nav-item { position: relative; display: flex; align-items: center; gap: var(--lme-gap-sm); padding: var(--lme-gap-sm) var(--lme-gap-md); border-radius: var(--lme-radius-md); cursor: pointer; color: var(--lme-text-secondary); transition: background var(--lme-dur-fast) var(--lme-ease-standard), color var(--lme-dur-fast) var(--lme-ease-standard); }
.sidebar-nav-item:hover { background: var(--lme-bg-hover); color: var(--lme-text-primary); }
.sidebar-nav-item.active { background: var(--lme-accent-subtle); color: var(--lme-accent); font-weight: var(--lme-font-weight-semibold); }
.sidebar-nav-item.active::before { content: ''; position: absolute; left: 0; top: 6px; bottom: 6px; width: 3px; border-radius: 2px; background: var(--lme-accent); }
.nav-icon { display: inline-flex; align-items: center; justify-content: center; width: 24px; flex-shrink: 0; }
.nav-label { font-size: var(--lme-font-size-sm); }

/* ── 内容区 ── */
.wiki-content { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-width: 0; }
.wiki-breadcrumb { display: flex; align-items: center; gap: var(--lme-gap-xs); padding: var(--lme-gap-sm) var(--lme-gap-lg); border-bottom: 1px solid var(--lme-border); font-size: var(--lme-font-size-sm); background: var(--lme-bg-panel); }
.breadcrumb-item { display: flex; align-items: center; gap: var(--lme-gap-xs); }
.breadcrumb-link { color: var(--lme-text-secondary); cursor: pointer; }
.breadcrumb-link:hover { color: var(--lme-accent); text-decoration: underline; }
.breadcrumb-link.current { color: var(--lme-text-muted); cursor: default; }
.breadcrumb-sep { color: var(--lme-text-muted); }

.wiki-slot { flex: 1; overflow: auto; }
</style>
