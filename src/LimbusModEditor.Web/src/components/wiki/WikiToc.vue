<script setup lang="ts">
// 维基左侧编号目录：编号 + 锚点 + 吸顶 + 回到顶部
// 纯展示组件：点击只发事件，滚动行为由宿主页实现
import { computed } from 'vue'
import type { TocItem } from '@/ipc/types'

const props = defineProps<{
  /** 目录项（后端 toc 或前端按分节生成） */
  items: TocItem[]
  /** 当前高亮项 id（滚动联动），可缺省 */
  activeId?: string
  /** 是否显示「回到顶部」链接 */
  showBackToTop?: boolean
}>()

const emit = defineEmits<{
  /** 点击目录项，参数为 TocItem.id */
  (e: 'select', id: string): void
  /** 点击「回到顶部」 */
  (e: 'back-top'): void
}>()

// 扁平化嵌套目录并生成维基式编号（1 / 1.1 / 2 …）
interface FlatItem {
  id: string
  title: string
  level: number
  number: string
}

const flatItems = computed<FlatItem[]>(() => {
  const out: FlatItem[] = []
  const counters: number[] = []
  const walk = (list: TocItem[], depth: number) => {
    list.forEach((item, i) => {
      counters[depth] = i + 1
      counters.length = depth + 1
      out.push({
        id: item.id,
        title: item.title,
        level: depth,
        number: counters.join('.'),
      })
      if (item.children && item.children.length > 0) walk(item.children, depth + 1)
    })
  }
  walk(props.items, 0)
  return out
})
</script>

<template>
  <nav class="wiki-toc" aria-label="页面目录">
    <ol class="wiki-toc-list">
      <li
        v-for="item in flatItems"
        :key="item.id"
        class="wiki-toc-item"
        :class="[`level-${item.level}`, { active: item.id === activeId }]"
      >
        <a
          class="wiki-toc-link"
          :href="`#${item.id}`"
          @click.prevent="emit('select', item.id)"
        >
          <span class="wiki-toc-number">{{ item.number }}</span>
          <span class="wiki-toc-title">{{ item.title }}</span>
        </a>
      </li>
    </ol>
    <a
      v-if="showBackToTop"
      class="wiki-toc-back-top"
      href="#"
      @click.prevent="emit('back-top')"
    >回到顶部</a>
  </nav>
</template>

<style scoped>
.wiki-toc {
  position: sticky;
  top: 0;
  z-index: var(--lme-z-sticky);
  align-self: start;
  width: var(--wiki-toc-width);
  padding: var(--lme-gap-lg) var(--lme-gap-sm);
  max-height: calc(100vh - 32px);
  overflow-y: auto;
}

.wiki-toc-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.wiki-toc-item.level-1 {
  padding-left: var(--lme-gap-md);
}

.wiki-toc-item.level-2 {
  padding-left: var(--lme-gap-xl);
}

.wiki-toc-link {
  display: flex;
  gap: var(--lme-gap-xs);
  padding: 2px var(--lme-gap-xs);
  border-radius: var(--lme-radius-sm);
  text-decoration: none;
  color: var(--wiki-toc-text);
  font-size: var(--lme-font-size-sm);
  line-height: 1.6;
  transition: color 0.15s;
}

.wiki-toc-link:hover {
  color: var(--wiki-toc-text-hover);
}

.wiki-toc-item.active .wiki-toc-link {
  color: var(--wiki-toc-active);
  font-weight: 600;
}

.wiki-toc-number {
  flex-shrink: 0;
}

.wiki-toc-title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wiki-toc-back-top {
  display: inline-block;
  margin-top: var(--lme-gap-md);
  padding: 2px var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--wiki-toc-active);
  text-decoration: none;
}

.wiki-toc-back-top:hover {
  color: var(--lme-accent-hover);
}
</style>
