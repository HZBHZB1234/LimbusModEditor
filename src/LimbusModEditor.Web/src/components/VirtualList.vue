<script setup lang="ts" generic="T">
// 虚拟滚动列表：只渲染可视窗口内的行 + 预取窗口
// 性能纪律：不一次性渲染全部数据

import { ref, computed, onMounted, onUnmounted, watch } from 'vue'

const props = withDefaults(
  defineProps<{
    items: T[]
    itemHeight?: number
    /** 可视区域上下各多渲染的行数（预取窗口） */
    overscan?: number
    height?: number
    /** true = 撑满父容器的剩余高度（用在有界的 flex 列里）；false = 用 height 固定像素 */
    grow?: boolean
  }>(),
  {
    itemHeight: 28,
    overscan: 5,
    height: 600,
    grow: false,
  },
)

const emit = defineEmits<{
  (e: 'range-change', start: number, end: number): void
}>()

const scrollTop = ref(0)
const containerRef = ref<HTMLElement | null>(null)

const totalHeight = computed(() => props.items.length * props.itemHeight)

const startIndex = computed(() =>
  Math.max(0, Math.floor(scrollTop.value / props.itemHeight) - props.overscan),
)

const endIndex = computed(() =>
  Math.min(
    props.items.length,
    Math.ceil((scrollTop.value + props.height) / props.itemHeight) + props.overscan,
  ),
)

const visibleItems = computed(() =>
  props.items.slice(startIndex.value, endIndex.value).map((item, i) => ({
    item,
    index: startIndex.value + i,
    top: (startIndex.value + i) * props.itemHeight,
  })),
)

const offsetY = computed(() => startIndex.value * props.itemHeight)

function onScroll(e: Event) {
  const target = e.target as HTMLElement
  scrollTop.value = target.scrollTop
  emit('range-change', startIndex.value, endIndex.value)
}

watch(
  () => props.items.length,
  () => {
    scrollTop.value = 0
    if (containerRef.value) containerRef.value.scrollTop = 0
  },
)

onMounted(() => {
  if (containerRef.value) {
    props.height
  }
})
</script>

<template>
  <div
    ref="containerRef"
    class="virtual-list-container"
    :class="{ grow: grow }"
    :style="grow ? undefined : { height: height + 'px' }"
    @scroll="onScroll"
  >
    <div class="virtual-list-spacer" :style="{ height: totalHeight + 'px' }">
      <div class="virtual-list-content" :style="{ transform: `translateY(${offsetY}px)` }">
        <div
          v-for="entry in visibleItems"
          :key="entry.index"
          class="virtual-list-row"
          :style="{ height: itemHeight + 'px', lineHeight: itemHeight + 'px' }"
        >
          <slot :item="entry.item" :index="entry.index" />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.virtual-list-container {
  overflow-y: auto;
  overflow-x: hidden;
  position: relative;
  flex: 1;
}

.virtual-list-container.grow {
  height: 100%;
  min-height: 0;
}

.virtual-list-spacer {
  position: relative;
  width: 100%;
}

.virtual-list-content {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
}

.virtual-list-row {
  overflow: hidden;
}
</style>
