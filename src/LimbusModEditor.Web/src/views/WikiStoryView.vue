<script setup lang="ts">
/**
 * WikiStoryView — 剧情专用视图（category === 'story'，实测 765 页）
 *
 * 对照 docs/WIKI-PREVIEW-UPGRADE-SPEC.md §4.2：
 *   章节键标题 → 章节导航（上一章 / 下一章）→ 对话流（说话人 + 台词，区分旁白）→ 媒体
 * 剧情页**不用右栏信息框**（本地无剧情数值面板来源），改为单栏 + 宽对话区。
 *
 * 台词来源：分节 content（实测 story:1D101 的章节正文为真台词）。
 * 纯展示 + 纯文本渲染（不解析 HTML，杜绝 XSS）。
 */

import { computed, ref, watch } from 'vue'
import type { WikiPage, WikiSection, ResourceBinding, GalleryImage } from '@/ipc/types'
import WikiAudioPlayer from '@/components/wiki/WikiAudioPlayer.vue'
import WikiGallery from '@/components/wiki/WikiGallery.vue'
import WikiToc from '@/components/wiki/WikiToc.vue'
import WikiChipList from '@/components/wiki/WikiChipList.vue'
import WikiNoticeBox from '@/components/wiki/WikiNoticeBox.vue'
import { resolveMediaUrl } from '@/components/wiki/mediaUrl'

const props = defineProps<{
  page: WikiPage
}>()

/** 视为「正文章节」的分节：标题含「章」但不含「导航」 */
const chapters = computed<WikiSection[]>(() => {
  const list = props.page.sections ?? []
  const hit = list.filter((s) => s.title.includes('章') && !s.title.includes('导航'))
  if (hit.length > 0) return hit
  // 退一步：排除概览/导航类分节，其余都当正文
  return list.filter((s) => !['概览', '概述', '简介'].includes(s.title) && !s.title.includes('导航'))
})

const activeIndex = ref(0)

watch(chapters, (list) => {
  if (activeIndex.value > list.length - 1) activeIndex.value = 0
})

const current = computed<WikiSection | null>(() => chapters.value[activeIndex.value] ?? null)

const hasPrev = computed(() => activeIndex.value > 0)
const hasNext = computed(() => activeIndex.value < chapters.value.length - 1)

function goPrev() {
  if (hasPrev.value) activeIndex.value -= 1
}

function goNext() {
  if (hasNext.value) activeIndex.value += 1
}

// ── 对话流解析：说话人：台词 → 角色对白；无冒号 → 旁白 ──
interface DialogueLine {
  kind: 'speech' | 'narration'
  speaker: string
  text: string
}

const SPEECH_PATTERN = /^([^：:]{1,24})[：:]\s*(.+)$/

const lines = computed<DialogueLine[]>(() => {
  const content = current.value?.content ?? ''
  return content
    .split(/\r?\n/)
    .map((raw) => raw.trim())
    .filter((raw) => raw !== '')
    .map((raw) => {
      const m = raw.match(SPEECH_PATTERN)
      if (m) return { kind: 'speech' as const, speaker: m[1].trim(), text: m[2].trim() }
      return { kind: 'narration' as const, speaker: '', text: raw }
    })
})

// ── 媒体（按 binding.kind 派发，无绑定不渲染）──
function bindingsOf(section: WikiSection | null): ResourceBinding[] {
  return section?.bindings ?? []
}

const audioItems = computed<ResourceBinding[]>(() =>
  bindingsOf(current.value).filter((b) => b.kind === 'Audio'),
)

const galleryImages = computed<GalleryImage[]>(() =>
  bindingsOf(current.value)
    .filter((b) => b.kind === 'Image')
    .flatMap<GalleryImage>((b) => {
      const url = resolveMediaUrl(b)
      return url ? [{ url, caption: b.display }] : []
    }),
)

const toc = computed(() =>
  chapters.value.map((s) => ({ id: s.id, title: s.title, level: 1 })),
)

function jumpTo(id: string) {
  const idx = chapters.value.findIndex((s) => s.id === id)
  if (idx >= 0) activeIndex.value = idx
}

const noticeText = computed(() =>
  chapters.value.length === 0 ? '该剧情页当前没有可展示的章节正文。' : '',
)

const tags = computed<string[]>(() => props.page.tags ?? [])
</script>

<template>
  <div class="wiki-story-view">
    <!-- 标题区 -->
    <header class="story-header">
      <h1 class="story-title">{{ page.title }}</h1>
      <div class="story-meta">
        <span class="story-badge">剧情</span>
        <span v-if="chapters.length > 0" class="story-progress">
          第 {{ activeIndex + 1 }} / {{ chapters.length }} 章
        </span>
        <WikiChipList v-if="tags.length > 0" :items="tags" />
      </div>
    </header>

    <WikiNoticeBox v-if="noticeText" :text="noticeText" icon="ℹ️" />

    <div class="story-body">
      <!-- 章节目录 -->
      <WikiToc v-if="toc.length > 0" class="story-toc" :items="toc" @select="jumpTo" />

      <main class="story-main">
        <!-- 章节导航 -->
        <nav v-if="chapters.length > 0" class="chapter-nav">
          <button class="nav-btn" :disabled="!hasPrev" @click="goPrev">← 上一章</button>
          <span class="chapter-name">{{ current?.title }}</span>
          <button class="nav-btn" :disabled="!hasNext" @click="goNext">下一章 →</button>
        </nav>

        <!-- 对话流 -->
        <section v-if="lines.length > 0" class="dialogue">
          <div
            v-for="(line, i) in lines"
            :key="i"
            class="dialogue-line"
            :class="line.kind"
          >
            <span v-if="line.kind === 'speech'" class="dialogue-speaker">{{ line.speaker }}</span>
            <span v-else class="dialogue-speaker narration">旁白</span>
            <span class="dialogue-text">{{ line.text }}</span>
          </div>
        </section>
        <p v-else-if="current" class="dialogue-empty">该章节暂无可解析的对白文本。</p>

        <!-- 媒体 -->
        <WikiAudioPlayer
          v-if="audioItems.length > 0"
          :items="audioItems"
          :url-for="resolveMediaUrl"
          title="章节语音"
        />
        <WikiGallery v-if="galleryImages.length > 0" :images="galleryImages" title="章节图集" />
      </main>
    </div>
  </div>
</template>

<style scoped>
.wiki-story-view {
  padding: var(--lme-gap-lg) var(--lme-gap-xl);
  max-width: 1100px;
}

.story-header {
  margin-bottom: var(--lme-gap-lg);
}

.story-title {
  margin: 0;
  font-size: var(--wiki-title-size);
  line-height: var(--wiki-title-line);
  font-weight: 700;
  color: var(--wiki-title);
}

.story-meta {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  margin-top: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.story-badge {
  padding: 2px 10px;
  border-radius: var(--wiki-chip-radius);
  font-size: var(--lme-font-size-xs);
  font-weight: 600;
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  color: var(--wiki-chip-text);
}

.story-progress {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.story-body {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: var(--lme-gap-xl);
  align-items: start;
}

.story-toc {
  grid-column: 1;
}

.story-main {
  grid-column: 2;
  min-width: 0;
}

@media (max-width: 1024px) {
  .story-body {
    grid-template-columns: minmax(0, 1fr);
  }
  .story-toc,
  .story-main {
    grid-column: 1;
  }
}

/* ── 章节导航 ── */
.chapter-nav {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.nav-btn {
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  font-family: inherit;
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition: all 0.15s;
}

.nav-btn:hover:not(:disabled) {
  border-color: var(--wiki-accent);
  color: var(--wiki-accent);
}

.nav-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.chapter-name {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--wiki-section-title);
}

/* ── 对话流 ── */
.dialogue {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  margin: var(--lme-gap-lg) 0;
}

.dialogue-line {
  display: flex;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-left: 3px solid var(--wiki-accent);
  background: var(--lme-bg-panel);
  border-radius: 0 var(--lme-radius-md) var(--lme-radius-md) 0;
}

.dialogue-line.narration {
  border-left-color: var(--lme-border-strong);
  background: transparent;
}

.dialogue-speaker {
  flex-shrink: 0;
  min-width: 96px;
  font-size: var(--lme-font-size-sm);
  font-weight: 700;
  color: var(--wiki-accent);
}

.dialogue-speaker.narration {
  color: var(--lme-text-muted);
  font-weight: 500;
}

.dialogue-text {
  font-size: var(--lme-font-size-md);
  line-height: 1.8;
  color: var(--wiki-body-text);
  white-space: pre-wrap;
}

.dialogue-line.narration .dialogue-text {
  color: var(--lme-text-secondary);
  font-style: italic;
}

.dialogue-empty {
  color: var(--lme-text-muted);
}
</style>
