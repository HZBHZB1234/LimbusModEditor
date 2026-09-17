<script setup lang="ts">
// 维基分节分组 Tab：技能 / 被动 / 语音 / 立绘 / 剧情出场 等分组切换
// 纯展示 + 键盘可达（← → Home End）；插槽按 tab.key 具名渲染
import { computed, ref, watch } from 'vue'

interface WikiTab {
  key: string
  label: string
  /** 角标（条目数等），可缺省 */
  badge?: string | number
}

const props = defineProps<{
  /** Tab 列表；空数组时整块不渲染 */
  tabs: WikiTab[]
  /** 当前激活 key（支持 v-model:active）；缺省自动取第一个 */
  active?: string
}>()

const emit = defineEmits<{
  (e: 'update:active', key: string): void
  (e: 'change', key: string): void
}>()

const internalActive = ref<string>(props.active ?? props.tabs[0]?.key ?? '')

watch(
  () => props.active,
  (val) => {
    if (val && val !== internalActive.value) internalActive.value = val
  },
)

watch(
  () => props.tabs,
  (tabs) => {
    // 激活项不在新列表里时回落到第一个（不猜 key）
    if (!tabs.some((t) => t.key === internalActive.value)) {
      internalActive.value = tabs[0]?.key ?? ''
    }
  },
)

const hasTabs = computed(() => props.tabs.length > 0)

function select(key: string) {
  if (key === internalActive.value) return
  internalActive.value = key
  emit('update:active', key)
  emit('change', key)
}

/** 键盘导航：← → 循环切换，Home/End 跳首尾 */
function onKeydown(e: KeyboardEvent, index: number) {
  const count = props.tabs.length
  if (count === 0) return
  let next = index
  if (e.key === 'ArrowLeft') next = (index - 1 + count) % count
  else if (e.key === 'ArrowRight') next = (index + 1) % count
  else if (e.key === 'Home') next = 0
  else if (e.key === 'End') next = count - 1
  else return

  e.preventDefault()
  select(props.tabs[next].key)
  const el = document.getElementById(`wiki-tab-${props.tabs[next].key}`)
  el?.focus()
}
</script>

<template>
  <div v-if="hasTabs" class="wiki-tab-group">
    <div class="wiki-tab-list" role="tablist">
      <button
        v-for="(tab, idx) in tabs"
        :id="`wiki-tab-${tab.key}`"
        :key="tab.key"
        type="button"
        role="tab"
        class="wiki-tab"
        :class="{ active: tab.key === internalActive }"
        :aria-selected="tab.key === internalActive"
        :tabindex="tab.key === internalActive ? 0 : -1"
        @click="select(tab.key)"
        @keydown="onKeydown($event, idx)"
      >
        <span class="wiki-tab-label">{{ tab.label }}</span>
        <span v-if="tab.badge !== undefined" class="wiki-tab-badge">{{ tab.badge }}</span>
      </button>
    </div>

    <div class="wiki-tab-panel" role="tabpanel">
      <slot :name="internalActive" />
    </div>
  </div>
</template>

<style scoped>
.wiki-tab-group {
  margin: var(--lme-gap-xl) 0;
}

.wiki-tab-list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lme-gap-xs);
  border-bottom: 1px solid var(--wiki-tab-border);
}

.wiki-tab {
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: transparent;
  border: 1px solid transparent;
  border-bottom: none;
  border-radius: var(--lme-radius-md) var(--lme-radius-md) 0 0;
  color: var(--wiki-tab-text);
  font-family: inherit;
  font-size: var(--lme-font-size-md);
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.wiki-tab:hover {
  color: var(--wiki-tab-text-hover);
}

.wiki-tab.active {
  background: var(--wiki-tab-active-bg);
  border-color: var(--wiki-tab-active-border);
  color: var(--wiki-tab-active-text);
  font-weight: 600;
}

.wiki-tab-badge {
  padding: 0 var(--lme-gap-xs);
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  border-radius: var(--wiki-chip-radius);
  color: var(--wiki-chip-text);
  font-size: var(--lme-font-size-xs);
  font-weight: 400;
}

.wiki-tab-panel {
  padding-top: var(--lme-gap-md);
}
</style>
