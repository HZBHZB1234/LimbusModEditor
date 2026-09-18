<script setup lang="ts">
// 全局命令面板（Ctrl+K）：跨工作台/维基的导航入口
// v3：改由 Naive UI 的 NModal + NInput 实现（焦点/Esc/遮罩由库接管），
//     键盘导航（↑↓ / Enter）与条目清单沿用原实现；不发任何 IPC
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { NModal, NInput, type InputInst } from 'naive-ui'

interface CommandItem {
  key: string
  label: string
  icon: string
  route: string
  group: '工作台' | '维基'
  keywords?: string
}

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
}>()

const router = useRouter()

const items: CommandItem[] = [
  { key: 'assets', label: '资源工作台', icon: '📦', route: '/assets', group: '工作台', keywords: 'asset 资源 浏览 预览 替换' },
  { key: 'bank', label: '音频工作台', icon: '🎵', route: '/bank', group: '工作台', keywords: 'audio 音频 bank 播放' },
  { key: 'text', label: '文本工作台', icon: '📝', route: '/text', group: '工作台', keywords: 'text 文本 翻译 语言' },
  { key: 'static', label: '静态数据工作台', icon: '📊', route: '/static', group: '工作台', keywords: 'static 静态 数据表 表格' },
  { key: 'export', label: '导出工作台', icon: '📤', route: '/export', group: '工作台', keywords: 'export 导出 模组 打包' },
  { key: 'project', label: '项目管理', icon: '📁', route: '/project', group: '工作台', keywords: 'project 项目 新建 打开' },
  { key: 'settings', label: '设置', icon: '⚙️', route: '/settings', group: '工作台', keywords: 'settings 设置 偏好' },
  { key: 'help', label: '帮助', icon: '❓', route: '/help', group: '工作台', keywords: 'help 帮助 文档 快捷键' },
  { key: 'wiki-home', label: '维基首页', icon: '📖', route: '/wiki', group: '维基', keywords: 'wiki 维基 首页' },
  { key: 'wiki-persona', label: '维基 · 人格', icon: '🎭', route: '/wiki/category/persona', group: '维基', keywords: 'persona 人格 identité' },
  { key: 'wiki-enemy', label: '维基 · 敌方单位', icon: '👹', route: '/wiki/category/enemy', group: '维基', keywords: 'enemy 敌人 怪物' },
  { key: 'wiki-abnormality', label: '维基 · 异想体', icon: '🌀', route: '/wiki/category/abnormality', group: '维基', keywords: 'abnormality 异想体' },
  { key: 'wiki-ego', label: '维基 · E.G.O 装备', icon: '⚔️', route: '/wiki/category/ego', group: '维基', keywords: 'ego 装备 武器' },
  { key: 'wiki-ego-gift', label: '维基 · E.G.O 饰品', icon: '💍', route: '/wiki/category/ego_gift', group: '维基', keywords: 'ego gift 饰品 礼物' },
  { key: 'wiki-announcer', label: '维基 · 播报员', icon: '📢', route: '/wiki/category/announcer', group: '维基', keywords: 'announcer 播报员' },
  { key: 'wiki-story', label: '维基 · 剧情', icon: '📕', route: '/wiki/category/story', group: '维基', keywords: 'story 剧情 故事' },
  { key: 'wiki-stage', label: '维基 · 关卡', icon: '🗺️', route: '/wiki/category/stage', group: '维基', keywords: 'stage 关卡 副本' },
  { key: 'wiki-item', label: '维基 · 物品', icon: '🎒', route: '/wiki/category/item', group: '维基', keywords: 'item 物品 道具' },
  { key: 'wiki-mechanism', label: '维基 · 机制', icon: '⚙️', route: '/wiki/category/mechanism', group: '维基', keywords: 'mechanism 机制 规则' },
  { key: 'wiki-keyword', label: '维基 · 关键词', icon: '🔑', route: '/wiki/category/keyword', group: '维基', keywords: 'keyword 关键词 术语' },
]

const query = ref('')
const activeIndex = ref(0)
const inputRef = ref<InputInst | null>(null)

const visible = computed({
  get: () => props.open,
  set: (value: boolean) => emit('update:open', value),
})

const filtered = computed<CommandItem[]>(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return items
  return items.filter(
    (it) =>
      it.label.toLowerCase().includes(q) ||
      (it.keywords ?? '').toLowerCase().includes(q),
  )
})

// 分组后的扁平顺序（工作台在前，维基在后）——键盘 ↑↓ 走扁平序
const flatResults = computed(() => filtered.value)

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
  const n = flatResults.value.length
  if (n === 0) return
  activeIndex.value = (activeIndex.value + delta + n) % n
  void nextTick(() => {
    document
      .querySelector('.palette-option.active')
      ?.scrollIntoView({ block: 'nearest' })
  })
}

function run(item: CommandItem) {
  visible.value = false
  void router.push(item.route)
}

function onEnter() {
  const item = flatResults.value[activeIndex.value]
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

// 分组标题（保持 items 原始顺序：工作台 → 维基）
const groups = computed(() => {
  const seen = new Map<string, CommandItem[]>()
  for (const it of flatResults.value) {
    if (!seen.has(it.group)) seen.set(it.group, [])
    seen.get(it.group)!.push(it)
  }
  return Array.from(seen.entries()).map(([name, list]) => ({ name, list }))
})

const emptyResults = computed(
  () => query.value.trim() !== '' && flatResults.value.length === 0,
)
</script>

<template>
  <NModal
    v-model:show="visible"
    :closable="false"
    :auto-focus="false"
    style="width: min(560px, 92vw)"
    @after-enter="onAfterEnter"
  >
    <div class="palette" role="dialog" aria-label="命令面板">
      <div class="palette-input-row">
        <NInput
          ref="inputRef"
          v-model:value="query"
          size="large"
          placeholder="搜索或跳转：工作台、维基分类…"
          @keydown="onKeydown"
        >
          <template #prefix>
            <span class="palette-search-icon">🔍</span>
          </template>
        </NInput>
      </div>

      <div class="palette-list">
        <div v-for="g in groups" :key="g.name" class="palette-group">
          <div class="palette-group-title">{{ g.name }}</div>
          <button
            v-for="item in g.list"
            :key="item.key"
            class="palette-option"
            :class="{ active: flatResults[activeIndex]?.key === item.key }"
            :data-key="item.key"
            @click="run(item)"
            @mousemove="activeIndex = flatResults.findIndex((f) => f.key === item.key)"
          >
            <span class="palette-option-icon">{{ item.icon }}</span>
            <span class="palette-option-label">{{ item.label }}</span>
            <span class="palette-option-hint">跳转 ↵</span>
          </button>
        </div>

        <div v-if="emptyResults" class="palette-empty">
          <span class="palette-empty-icon">🗂</span>
          <span>没有匹配的条目，换个关键词试试</span>
        </div>
      </div>

      <div class="palette-footer">
        <span><kbd class="palette-kbd">↑</kbd><kbd class="palette-kbd">↓</kbd> 选择</span>
        <span><kbd class="palette-kbd">↵</kbd> 打开</span>
        <span><kbd class="palette-kbd">Esc</kbd> 关闭</span>
      </div>
    </div>
  </NModal>
</template>

<style scoped>
.palette {
  display: flex;
  flex-direction: column;
  max-height: 60vh;
  background: var(--lme-palette-bg);
  border: 1px solid var(--lme-palette-border);
  border-radius: var(--lme-radius-lg);
  overflow: hidden;
}

.palette-input-row {
  padding: var(--lme-gap-md) var(--lme-gap-md) var(--lme-gap-sm);
  border-bottom: 1px solid var(--lme-border);
}

.palette-search-icon {
  font-size: var(--lme-font-size-md);
  opacity: 0.7;
}

.palette-list {
  flex: 1;
  overflow-y: auto;
  padding: var(--lme-gap-sm);
}

.palette-group + .palette-group {
  margin-top: var(--lme-gap-sm);
}

.palette-group-title {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-semibold);
  letter-spacing: var(--lme-tracking-caps);
  color: var(--lme-palette-group-text);
}

.palette-option {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  width: 100%;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: none;
  border: none;
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-md);
  font-family: var(--lme-font-family);
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.palette-option:hover,
.palette-option.active {
  background: var(--lme-palette-option-hover);
}

.palette-option.active {
  background: var(--lme-palette-option-active);
}

.palette-option-icon {
  width: 22px;
  text-align: center;
}

.palette-option-label {
  flex: 1;
}

.palette-option-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  opacity: 0;
  transition: opacity var(--lme-dur-fast) var(--lme-ease-standard);
}

.palette-option.active .palette-option-hint,
.palette-option:hover .palette-option-hint {
  opacity: 1;
}

.palette-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-2xl) 0;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

.palette-empty-icon {
  font-size: 28px;
}

.palette-footer {
  display: flex;
  gap: var(--lme-gap-lg);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-top: 1px solid var(--lme-border);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
}

.palette-kbd {
  padding: 1px 6px;
  background: var(--lme-cmdbar-kbd-bg);
  border: 1px solid var(--lme-cmdbar-kbd-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-cmdbar-kbd-text);
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-mono);
}
</style>
