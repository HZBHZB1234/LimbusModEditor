<script setup lang="ts">
// 维基画廊：立绘/图标/图集缩略图网格 + 点击放大（lightbox）
// 数据源：resource_bindings kind='Image'；地址走 lme.data 虚拟主机，禁止 base64
// 纯展示；空数组不渲染；图裂显示占位态但不伪造图片
// ui-redesign r6：×/‹/› 等字符换 AppIcon（lucide）。
import { computed, onUnmounted, ref, watch } from 'vue'
import type { GalleryImage } from '@/ipc'
import AppIcon from '@/components/AppIcon.vue'

const props = withDefaults(
  defineProps<{
    /** 图片列表；空数组时整块不渲染 */
    images: GalleryImage[]
    /** 区块小标题，可缺省 */
    title?: string
    /** 缩略图网格列宽下限，缺省取 --wiki-gallery-thumb-min */
    thumbMin?: string
  }>(),
  { thumbMin: 'var(--wiki-gallery-thumb-min)' },
)

/** 点某张图的「去编辑」：把该图的 editTo 交给调用方跳转（组件自己不碰路由） */
const emit = defineEmits<{ (e: 'edit', target: string): void }>()

/** 当前放大索引；null 表示关闭 */
const openIndex = ref<number | null>(null)
/** 加载失败的图（按 url 记录），显示占位态 */
const broken = ref<Set<string>>(new Set())

const hasImages = computed(() => props.images.length > 0)
const isOpen = computed(() => openIndex.value !== null)
const current = computed<GalleryImage | null>(() =>
  openIndex.value === null ? null : props.images[openIndex.value] ?? null,
)

function markBroken(url: string) {
  const next = new Set(broken.value)
  next.add(url)
  broken.value = next
}

function isBroken(url: string): boolean {
  return broken.value.has(url)
}

function open(index: number) {
  openIndex.value = index
}

function close() {
  openIndex.value = null
}

function step(delta: number) {
  if (openIndex.value === null || props.images.length === 0) return
  const next = (openIndex.value + delta + props.images.length) % props.images.length
  openIndex.value = next
}

/** 键盘：Esc 关闭，←/→ 切换；仅在打开时监听 */
function onKeydown(e: KeyboardEvent) {
  if (openIndex.value === null) return
  if (e.key === 'Escape') {
    close()
  } else if (e.key === 'ArrowLeft') {
    step(-1)
  } else if (e.key === 'ArrowRight') {
    step(1)
  }
}

watch(isOpen, (open) => {
  if (open) window.addEventListener('keydown', onKeydown)
  else window.removeEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <section v-if="hasImages" class="wiki-gallery">
    <h3 v-if="title" class="wiki-gallery-title">{{ title }}</h3>

    <div
      class="wiki-gallery-grid"
      :style="{ gridTemplateColumns: `repeat(auto-fill, minmax(${thumbMin}, 1fr))` }"
    >
      <button
        v-for="(img, idx) in images"
        :key="img.url + idx"
        type="button"
        class="wiki-gallery-item"
        :class="{ broken: isBroken(img.url) }"
        :title="img.caption ?? img.url"
        @click="open(idx)"
      >
        <img
          v-if="!isBroken(img.url)"
          :src="img.url"
          :alt="img.caption ?? ''"
          loading="lazy"
          decoding="async"
          @error="markBroken(img.url)"
        />
        <span v-else class="wiki-gallery-broken">图片不可用</span>
        <span v-if="img.caption" class="wiki-gallery-caption">{{ img.caption }}</span>
        <!-- 只有拿到容器路径（editTo）才显示，不猜跳转目标 -->
        <span
          v-if="img.editTo"
          class="wiki-gallery-edit"
          title="到资源工作台定位这条容器路径"
          @click.stop="emit('edit', img.editTo)"
        >
          去编辑
        </span>
      </button>
    </div>

    <!-- 放大遮罩：遮罩层 --lme-z-overlay，图片面板 --lme-z-modal -->
    <div v-if="isOpen" class="wiki-lightbox-overlay" @click="close">
      <div class="wiki-lightbox-panel" @click.stop>
        <button class="wiki-lightbox-close" type="button" aria-label="关闭" @click="close">
          <AppIcon name="close" :size="15" />
        </button>
        <button
          v-if="images.length > 1"
          class="wiki-lightbox-nav prev"
          type="button"
          aria-label="上一张"
          @click="step(-1)"
        >
          <AppIcon name="chevronLeft" :size="18" />
        </button>

        <figure class="wiki-lightbox-figure">
          <img
            v-if="current && !isBroken(current.url)"
            :src="current.url"
            :alt="current.caption ?? ''"
            @error="markBroken(current.url)"
          />
          <span v-else class="wiki-lightbox-broken">图片不可用</span>
          <figcaption v-if="current?.caption" class="wiki-lightbox-caption">
            {{ current.caption }}
            <span v-if="current.credit" class="wiki-lightbox-credit">{{ current.credit }}</span>
          </figcaption>
        </figure>

        <button
          v-if="images.length > 1"
          class="wiki-lightbox-nav next"
          type="button"
          aria-label="下一张"
          @click="step(1)"
        >
          <AppIcon name="chevronRight" :size="18" />
        </button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.wiki-gallery {
  margin: var(--lme-gap-xl) 0;
}

.wiki-gallery-title {
  margin: 0 0 var(--lme-gap-sm);
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.wiki-gallery-grid {
  display: grid;
  gap: var(--lme-gap-md);
}

.wiki-gallery-item {
  position: relative;
  display: flex;
  flex-direction: column;
  padding: 0;
  overflow: hidden;
  background: var(--wiki-gallery-bg);
  border: 1px solid var(--wiki-tab-border);
  border-radius: var(--wiki-media-radius);
  cursor: pointer;
  transition: border-color 0.15s, transform 0.15s;
}

.wiki-gallery-item:hover {
  border-color: var(--lme-accent);
  transform: translateY(-2px);
}

.wiki-gallery-item img {
  display: block;
  width: 100%;
  height: var(--wiki-gallery-thumb-max);
  object-fit: cover;
  background: var(--wiki-media-broken-bg);
}

.wiki-gallery-broken {
  display: flex;
  align-items: center;
  justify-content: center;
  height: var(--wiki-gallery-thumb-max);
  background: var(--wiki-media-broken-bg);
  color: var(--wiki-media-broken-text);
  font-size: var(--lme-font-size-xs);
}

.wiki-gallery-caption {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wiki-gallery-edit {
  padding: 0 var(--lme-gap-sm) var(--lme-gap-xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-accent);
  text-align: left;
}

.wiki-gallery-edit:hover {
  text-decoration: underline;
}

/* ── 放大遮罩 ── */
.wiki-lightbox-overlay {
  position: fixed;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--lme-overlay-bg);
  z-index: var(--lme-z-overlay);
}

.wiki-lightbox-panel {
  position: relative;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  max-width: 90vw;
  max-height: 90vh;
  padding: var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border: 1px solid var(--wiki-tab-border);
  border-radius: var(--lme-radius-lg);
  z-index: var(--lme-z-modal);
}

.wiki-lightbox-close {
  position: absolute;
  top: var(--lme-gap-sm);
  right: var(--lme-gap-sm);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  background: transparent;
  border: 1px solid var(--wiki-tab-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
}

.wiki-lightbox-close:hover {
  color: var(--lme-text-primary);
  background: var(--lme-bg-hover);
}

.wiki-lightbox-nav {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  width: 32px;
  height: 48px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--wiki-tab-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
}

.wiki-lightbox-nav:hover {
  color: var(--lme-accent);
  border-color: var(--lme-accent);
}

.wiki-lightbox-figure {
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  align-items: center;
  min-width: 0;
}

.wiki-lightbox-figure img {
  display: block;
  max-width: 100%;
  max-height: 76vh;
  object-fit: contain;
  background: var(--wiki-media-broken-bg);
  border-radius: var(--wiki-media-radius);
}

.wiki-lightbox-broken {
  display: flex;
  align-items: center;
  justify-content: center;
  min-width: 240px;
  min-height: 180px;
  background: var(--wiki-media-broken-bg);
  color: var(--wiki-media-broken-text);
}

.wiki-lightbox-caption {
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: center;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.wiki-lightbox-credit {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}
</style>
