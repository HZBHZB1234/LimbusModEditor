<script setup lang="ts">
// 维基标签胶囊列表：类别/派系/皮肤等胶囊
// 纯展示；items 为空时不渲染
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    /** 胶囊文本列表；空数组时不渲染 */
    items: string[]
    /** 当前激活项（可选中形态），可缺省 */
    active?: string
    /** 是否可点击（发 select 事件） */
    clickable?: boolean
  }>(),
  { clickable: false },
)

const emit = defineEmits<{
  /** 点击胶囊，参数为该胶囊文本 */
  (e: 'select', value: string): void
}>()

const visible = computed(() => props.items.filter((t) => t.trim() !== ''))

function onClick(value: string) {
  if (props.clickable) emit('select', value)
}
</script>

<template>
  <div v-if="visible.length > 0" class="wiki-chip-list">
    <component
      :is="clickable ? 'button' : 'span'"
      v-for="chip in visible"
      :key="chip"
      class="wiki-chip"
      :class="{ active: chip === active, clickable }"
      :type="clickable ? 'button' : undefined"
      @click="onClick(chip)"
    >
      {{ chip }}
    </component>
  </div>
</template>

<style scoped>
.wiki-chip-list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lme-gap-sm);
}

.wiki-chip {
  display: inline-flex;
  align-items: center;
  padding: 2px var(--lme-gap-md);
  border-radius: var(--wiki-chip-radius);
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  color: var(--wiki-chip-text);
  font-size: var(--lme-font-size-sm);
  line-height: 1.6;
  white-space: nowrap;
}

.wiki-chip.clickable {
  cursor: pointer;
  font-family: inherit;
  transition: background 0.15s, color 0.15s;
}

.wiki-chip.clickable:hover {
  background: var(--wiki-chip-active-bg);
  color: var(--wiki-chip-active-text);
}

.wiki-chip.active {
  background: var(--wiki-chip-active-bg);
  border-color: var(--wiki-chip-active-bg);
  color: var(--wiki-chip-active-text);
  font-weight: 600;
}

button.wiki-chip {
  appearance: none;
  outline: none;
}
</style>
