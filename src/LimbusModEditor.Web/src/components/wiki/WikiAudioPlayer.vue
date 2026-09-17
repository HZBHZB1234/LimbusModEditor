<script setup lang="ts">
// 维基音频预览：语音/音效列表 + 单条播放 + 时长 + 关联台词
// 数据源：resource_bindings kind='Audio'（含 duration_sec）
// 纯展示 + 本地播放控制；二进制走 lme.data 虚拟主机，禁止 base64
import { computed, nextTick, onUnmounted, ref } from 'vue'
import type { ResourceBinding } from '@/ipc'

const props = withDefaults(
  defineProps<{
    /** 音频绑定列表；空数组时整块不渲染 */
    items: ResourceBinding[]
    /**
     * 由宿主页把 binding 解析成可播放地址（lme.data 虚拟主机）。
     * resource_bindings.deep_link 实测是原始容器路径、不是 URL，
     * 因此本组件不自行拼接地址：返回 null 的条目不可点击、不占位、不报错。
     */
    urlFor?: (item: ResourceBinding) => string | null
    /** 区块小标题，可缺省（缺省不渲染标题行） */
    title?: string
    /** 列表最大高度（px），超出滚动；可缺省 */
    maxHeight?: number
  }>(),
  { maxHeight: 320 },
)

const audioRef = ref<HTMLAudioElement | null>(null)
const currentUrl = ref<string | null>(null)
const playingKey = ref<string | null>(null)
const failedKey = ref<string | null>(null)

interface AudioRow {
  key: string
  label: string
  caption: string
  duration: string
  url: string | null
}

/** 时长（秒）→ m:ss；无值返回空串，不编造 */
function formatDuration(sec?: number): string {
  if (sec === undefined || sec === null || !isFinite(sec) || sec < 0) return ''
  const total = Math.round(sec)
  const m = Math.floor(total / 60)
  const s = total % 60
  return `${m}:${String(s).padStart(2, '0')}`
}

/** 显示名：display → refKey 末段 → refKey 本身 */
function displayOf(item: ResourceBinding): string {
  if (item.display && item.display.trim() !== '') return item.display
  const parts = item.refKey.split(/[\\/]/)
  const last = parts[parts.length - 1]
  return last !== '' ? last : item.refKey
}

const rows = computed<AudioRow[]>(() =>
  props.items.map((item) => ({
    key: item.refKey,
    label: displayOf(item),
    caption: item.previewText ?? '',
    duration: formatDuration(item.durationSec),
    url: props.urlFor ? props.urlFor(item) : null,
  })),
)

const hasRows = computed(() => rows.value.length > 0)

async function toggle(row: AudioRow) {
  if (!row.url) return
  const el = audioRef.value
  if (!el) return

  // 同一条：再点即暂停
  if (playingKey.value === row.key) {
    el.pause()
    playingKey.value = null
    return
  }

  // 切换时停上一条（同时只允许一个在播）
  el.pause()
  failedKey.value = null
  currentUrl.value = row.url
  playingKey.value = row.key
  await nextTick()
  el.load()
  try {
    await el.play()
  } catch {
    // 播放被宿主/浏览器拒绝：回落为未播放态，不抛错
    playingKey.value = null
  }
}

function onEnded() {
  playingKey.value = null
}

function onError() {
  failedKey.value = playingKey.value
  playingKey.value = null
}

onUnmounted(() => {
  audioRef.value?.pause()
})
</script>

<template>
  <section v-if="hasRows" class="wiki-audio">
    <h3 v-if="title" class="wiki-audio-title">{{ title }}</h3>

    <ul class="wiki-audio-list" :style="{ maxHeight: maxHeight ? `${maxHeight}px` : undefined }">
      <li
        v-for="row in rows"
        :key="row.key"
        class="wiki-audio-row"
        :class="{
          playing: row.key === playingKey,
          disabled: !row.url,
          failed: row.key === failedKey,
        }"
        @click="toggle(row)"
      >
        <span class="wiki-audio-icon" aria-hidden="true">
          {{ row.key === playingKey ? '⏸' : '▶' }}
        </span>
        <span class="wiki-audio-main">
          <span class="wiki-audio-label">{{ row.label }}</span>
          <span v-if="row.caption" class="wiki-audio-caption">{{ row.caption }}</span>
        </span>
        <span v-if="row.key === failedKey" class="wiki-audio-state">播放失败</span>
        <span v-else-if="!row.url" class="wiki-audio-state">无可用地址</span>
        <span v-else-if="row.duration" class="wiki-audio-duration">{{ row.duration }}</span>
      </li>
    </ul>

    <audio
      ref="audioRef"
      class="wiki-audio-element"
      :src="currentUrl ?? undefined"
      preload="none"
      @ended="onEnded"
      @error="onError"
    />
  </section>
</template>

<style scoped>
.wiki-audio {
  margin: var(--lme-gap-xl) 0;
}

.wiki-audio-title {
  margin: 0 0 var(--lme-gap-sm);
  font-size: var(--lme-font-size-lg);
  font-weight: 600;
  color: var(--wiki-section-title);
}

.wiki-audio-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  border: 1px solid var(--wiki-tab-border);
  border-radius: var(--lme-radius-md);
}

.wiki-audio-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  height: var(--wiki-audio-row-height);
  padding: 0 var(--lme-gap-md);
  border-bottom: 1px solid var(--wiki-tab-border);
  cursor: pointer;
  transition: background 0.15s;
}

.wiki-audio-row:last-child {
  border-bottom: none;
}

.wiki-audio-row:hover:not(.disabled) {
  background: var(--wiki-audio-row-hover);
}

.wiki-audio-row.playing {
  background: var(--wiki-audio-playing-bg);
}

.wiki-audio-row.disabled {
  cursor: default;
  color: var(--lme-text-disabled);
}

.wiki-audio-row.failed {
  color: var(--lme-error);
}

.wiki-audio-icon {
  flex-shrink: 0;
  width: 18px;
  text-align: center;
  color: var(--wiki-accent);
}

.wiki-audio-row.disabled .wiki-audio-icon {
  color: var(--lme-text-disabled);
}

.wiki-audio-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.3;
}

.wiki-audio-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wiki-audio-caption {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wiki-audio-duration,
.wiki-audio-state {
  flex-shrink: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  font-variant-numeric: tabular-nums;
}

.wiki-audio-element {
  display: none;
}
</style>
