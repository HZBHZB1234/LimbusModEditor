<script setup lang="ts">
/**
 * 全局命令面板（Ctrl+K / Ctrl+Shift+P）。
 *
 * 统一入口：跨工作台与维基分类的跳转，外加若干即时操作（切换主题、打开设置）。
 * 键盘：↑↓ 选择、Enter 执行、Esc 关闭；输入按「标签 / 关键词」模糊匹配并高亮命中片段。
 * 不发任何 IPC。
 */
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { NModal, NInput, type InputInst } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import type { IconName } from '@/components/icons'
import { useAppearanceStore } from '@/stores/appearance'

interface CommandItem {
  key: string
  label: string
  icon: IconName
  group: string
  keywords?: string
  /** 路由跳转 */
  route?: string
  /** 即时动作（与 route 二选一） */
  action?: () => void
  /** 右侧提示（快捷键等） */
  hint?: string
}

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const router = useRouter()
const appearance = useAppearanceStore()

const items = computed<CommandItem[]>(() => [
  // ── 工作台 ──
  { key: 'assets', label: '资源工作台', icon: 'assets', group: '工作台', route: '/assets', keywords: 'asset 资源 浏览 预览 替换 图片 贴图', hint: 'Ctrl 1' },
  { key: 'bank', label: '音频工作台', icon: 'audio', group: '工作台', route: '/bank', keywords: 'audio 音频 bank 音效 试听 播放', hint: 'Ctrl 2' },
  { key: 'text', label: '文本工作台', icon: 'text', group: '工作台', route: '/text', keywords: 'text 文本 翻译 语言 lang 汉化', hint: 'Ctrl 3' },
  { key: 'static', label: '静态数据工作台', icon: 'staticData', group: '工作台', route: '/static', keywords: 'static 静态 数据表 表格 数值', hint: 'Ctrl 4' },
  { key: 'export', label: '导出工作台', icon: 'export', group: '工作台', route: '/export', keywords: 'export 导出 打包 模组 carra', hint: 'Ctrl 5' },
  { key: 'project', label: '项目管理', icon: 'project', group: '工作台', route: '/project', keywords: 'project 项目 新建 打开 保存 最近', hint: 'Ctrl 6' },
  { key: 'settings', label: '设置', icon: 'settings', group: '工作台', route: '/settings', keywords: 'settings 设置 偏好 路径 外观 主题 配色', hint: 'Ctrl ,' },
  { key: 'help', label: '帮助与快捷键', icon: 'help', group: '工作台', route: '/help', keywords: 'help 帮助 文档 快捷键 指南' },

  // ── 维基 ──
  { key: 'wiki-home', label: '维基首页', icon: 'wiki', group: '维基', route: '/wiki', keywords: 'wiki 维基 首页 概览' },
  { key: 'wiki-search', label: '维基搜索', icon: 'search', group: '维基', route: '/wiki/search', keywords: 'wiki 搜索 查找' },
  { key: 'wiki-persona', label: '维基 · 人格', icon: 'wikiPersona', group: '维基', route: '/wiki/category/persona', keywords: 'persona 人格 罪人' },
  { key: 'wiki-enemy', label: '维基 · 敌方单位', icon: 'wikiEnemy', group: '维基', route: '/wiki/category/enemy', keywords: 'enemy 敌人 怪物' },
  { key: 'wiki-abnormality', label: '维基 · 异想体', icon: 'wikiAbnormality', group: '维基', route: '/wiki/category/abnormality', keywords: 'abnormality 异想体' },
  { key: 'wiki-ego', label: '维基 · E.G.O 装备', icon: 'wikiEgo', group: '维基', route: '/wiki/category/ego', keywords: 'ego 装备 武器' },
  { key: 'wiki-ego-gift', label: '维基 · E.G.O 饰品', icon: 'wikiEgoGift', group: '维基', route: '/wiki/category/ego_gift', keywords: 'ego gift 饰品 礼物' },
  { key: 'wiki-announcer', label: '维基 · 播报员', icon: 'wikiAnnouncer', group: '维基', route: '/wiki/category/announcer', keywords: 'announcer 播报员 语音' },
  { key: 'wiki-story', label: '维基 · 剧情', icon: 'wikiStory', group: '维基', route: '/wiki/category/story', keywords: 'story 剧情 故事 章节' },
  { key: 'wiki-stage', label: '维基 · 关卡', icon: 'wikiStage', group: '维基', route: '/wiki/category/stage', keywords: 'stage 关卡 副本 战斗' },
  { key: 'wiki-item', label: '维基 · 物品', icon: 'wikiItem', group: '维基', route: '/wiki/category/item', keywords: 'item 物品 道具' },
  { key: 'wiki-mechanism', label: '维基 · 机制', icon: 'wikiMechanism', group: '维基', route: '/wiki/category/mechanism', keywords: 'mechanism 机制 规则' },
  { key: 'wiki-keyword', label: '维基 · 关键词', icon: 'wikiKeyword', group: '维基', route: '/wiki/category/keyword', keywords: 'keyword 关键词 术语' },
  { key: 'wiki-spine', label: '维基 · Spine 总览', icon: 'wikiSpine', group: '维基', route: '/wiki/spine', keywords: 'spine 骨骼 动画 挂点' },

  // ── 即时操作 ──
  {
    key: 'act-theme',
    label: appearance.isDark ? '切换到浅色主题' : '切换到深色主题',
    icon: appearance.isDark ? 'sun' : 'moon',
    group: '操作',
    keywords: 'theme 主题 明暗 深色 浅色 dark light',
    action: () => appearance.toggleMode(),
  },
  {
    key: 'act-project',
    label: '打开项目管理',
    icon: 'folderOpen',
    group: '操作',
    keywords: 'open 打开 项目',
    route: '/project',
  },
])

const query = ref('')
const activeIndex = ref(0)
const inputRef = ref<InputInst | null>(null)

const visible = computed({
  get: () => props.open,
  set: (value: boolean) => emit('update:open', value),
})

const filtered = computed<CommandItem[]>(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return items.value
  return items.value.filter(
    (it) =>
      it.label.toLowerCase().includes(q) ||
      it.group.toLowerCase().includes(q) ||
      (it.keywords ?? '').toLowerCase().includes(q),
  )
})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    query.value = ''
    activeIndex.value = 0
  },
)

watch(query, () => {
  activeIndex.value = 0
})

function onAfterEnter() {
  void nextTick(() => inputRef.value?.focus())
}

function move(delta: number) {
  const n = filtered.value.length
  if (n === 0) return
  activeIndex.value = (activeIndex.value + delta + n) % n
  void nextTick(() => {
    document.querySelector('.palette-option.active')?.scrollIntoView({ block: 'nearest' })
  })
}

function run(item: CommandItem) {
  visible.value = false
  if (item.action) item.action()
  else if (item.route) void router.push(item.route)
}

function onEnter() {
  const item = filtered.value[activeIndex.value]
  if (item) run(item)
}

function onKeydown(e: KeyboardEvent) {
  if (!props.open) return
  if (e.key === 'ArrowDown') {
    e.preventDefault()
    move(1)
  } else if (e.key === 'ArrowUp') {
    e.preventDefault()
    move(-1)
  } else if (e.key === 'Enter') {
    e.preventDefault()
    onEnter()
  }
}

/** 分组（保持 items 原始顺序：工作台 → 维基 → 操作） */
const groups = computed(() => {
  const seen = new Map<string, CommandItem[]>()
  for (const it of filtered.value) {
    if (!seen.has(it.group)) seen.set(it.group, [])
    seen.get(it.group)!.push(it)
  }
  return Array.from(seen.entries()).map(([name, list]) => ({ name, list }))
})

const emptyResults = computed(() => query.value.trim() !== '' && filtered.value.length === 0)

/**
 * 把命中片段高亮成两段（命中 / 未命中）。
 * 只按标签匹配；无命中或没输入时返回整段。
 */
function highlight(label: string): { text: string; hit: boolean }[] {
  const q = query.value.trim().toLowerCase()
  if (!q) return [{ text: label, hit: false }]
  const idx = label.toLowerCase().indexOf(q)
  if (idx < 0) return [{ text: label, hit: false }]
  return [
    { text: label.slice(0, idx), hit: false },
    { text: label.slice(idx, idx + q.length), hit: true },
    { text: label.slice(idx + q.length), hit: false },
  ].filter((s) => s.text.length > 0)
}
</script>

<template>
  <NModal
    v-model:show="visible"
    :closable="false"
    :auto-focus="false"
    style="width: min(600px, 92vw)"
    @after-enter="onAfterEnter"
  >
    <div class="palette" role="dialog" aria-label="命令面板">
      <div class="palette-input-row">
        <NInput
          ref="inputRef"
          v-model:value="query"
          size="large"
          placeholder="搜索工作台、维基分类或操作…"
          @keydown="onKeydown"
        >
          <template #prefix>
            <AppIcon name="search" :size="16" />
          </template>
        </NInput>
      </div>

      <div class="palette-list">
        <div v-for="g in groups" :key="g.name" class="palette-group">
          <div class="palette-group-title lme-caps">{{ g.name }}</div>
          <button
            v-for="item in g.list"
            :key="item.key"
            class="palette-option"
            :class="{ active: filtered[activeIndex]?.key === item.key }"
            :data-key="item.key"
            @click="run(item)"
            @mousemove="activeIndex = filtered.findIndex((f) => f.key === item.key)"
          >
            <span class="palette-option-icon">
              <AppIcon :name="item.icon" :size="16" />
            </span>
            <span class="palette-option-label">
              <template v-for="(seg, i) in highlight(item.label)" :key="i">
                <mark v-if="seg.hit" class="palette-hit">{{ seg.text }}</mark>
                <template v-else>{{ seg.text }}</template>
              </template>
            </span>
            <span v-if="item.hint" class="palette-option-hint lme-mono">{{ item.hint }}</span>
            <AppIcon v-else name="arrowRight" :size="14" class="palette-option-go" />
          </button>
        </div>

        <div v-if="emptyResults" class="palette-empty">
          <AppIcon name="search" :size="26" :stroke="1.5" class="palette-empty-icon" />
          <span class="palette-empty-title">没有匹配的条目</span>
          <span class="palette-empty-hint">试试「资源」「导出」「人格」，或直接输入关键词</span>
        </div>
      </div>

      <div class="palette-footer">
        <span class="pf-item"><kbd class="lme-kbd">↑</kbd><kbd class="lme-kbd">↓</kbd> 选择</span>
        <span class="pf-item"><kbd class="lme-kbd">Enter</kbd> 打开</span>
        <span class="pf-item"><kbd class="lme-kbd">Esc</kbd> 关闭</span>
        <span class="pf-spacer" />
        <span class="pf-item pf-count">{{ filtered.length }} 项</span>
      </div>
    </div>
  </NModal>
</template>

<style scoped>
.palette {
  display: flex;
  flex-direction: column;
  max-height: 64vh;
  background: var(--lme-palette-bg);
  border: 1px solid var(--lme-palette-border);
  border-radius: var(--lme-radius-xl);
  box-shadow: var(--lme-shadow-xl);
  overflow: hidden;
}

.palette-input-row {
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.palette-list {
  flex: 1;
  overflow-y: auto;
  padding: var(--lme-gap-sm);
}

.palette-group + .palette-group {
  margin-top: var(--lme-gap-sm);
  padding-top: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border-subtle);
}

.palette-group-title {
  padding: 0 var(--lme-gap-sm) var(--lme-gap-xs);
}

.palette-option {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  width: 100%;
  padding: 7px var(--lme-gap-md);
  background: none;
  border: 1px solid transparent;
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-md);
  font-family: inherit;
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.palette-option:hover {
  background: var(--lme-palette-option-hover);
}

.palette-option.active {
  background: var(--lme-palette-option-active);
  border-color: var(--lme-accent-border);
}

.palette-option-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.palette-option.active .palette-option-icon {
  color: var(--lme-accent);
}

.palette-option-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.palette-hit {
  background: none;
  color: var(--lme-accent);
  font-weight: var(--lme-font-weight-semibold);
}

.palette-option-hint {
  flex-shrink: 0;
  padding: 1px 6px;
  border: 1px solid var(--lme-cmdbar-kbd-border);
  border-radius: var(--lme-radius-xs);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-2xs);
}

.palette-option-go {
  flex-shrink: 0;
  color: var(--lme-text-disabled);
  opacity: 0;
  transition: opacity var(--lme-dur-fast) var(--lme-ease-standard);
}

.palette-option.active .palette-option-go,
.palette-option:hover .palette-option-go {
  opacity: 1;
}

/* ── 空态 ── */
.palette-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-2xl) 0;
  text-align: center;
}

.palette-empty-icon {
  color: var(--lme-text-disabled);
  margin-bottom: var(--lme-gap-xs);
}

.palette-empty-title {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.palette-empty-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 页脚 ── */
.palette-footer {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-lg);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-top: 1px solid var(--lme-border);
  background: var(--lme-bg-panel);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

.pf-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.pf-spacer {
  flex: 1;
}

.pf-count {
  font-family: var(--lme-font-mono);
}
</style>
