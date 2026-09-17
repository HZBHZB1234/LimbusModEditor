<script setup lang="ts">
// 维基来源标注徽章：权威 / 置信度 / 可写出处
// 承接已删除的 PresetsView 右栏出处信息（spec §7.3-1 迁移项）
// 全部字段可缺省：推不出来就不渲染，不占位、不编造
import { computed } from 'vue'

const props = defineProps<{
  /** 权威标注（如 auto / user / candidate 或来源系统名） */
  authority?: string
  /** 置信度（原样展示，不做百分比换算等推断） */
  confidence?: number | string
  /** 可写出处（WritableSource），有值即高亮为「可写」 */
  writableSource?: string
  /** 补充说明，作为 title 悬浮提示 */
  detail?: string
}>()

const hasAny = computed(
  () =>
    (props.authority?.trim() ?? '') !== '' ||
    props.confidence !== undefined ||
    (props.writableSource?.trim() ?? '') !== '',
)

const confidenceText = computed(() =>
  props.confidence === undefined ? '' : String(props.confidence),
)
</script>

<template>
  <span v-if="hasAny" class="wiki-source-badge" :title="detail">
    <span v-if="authority" class="wiki-badge">{{ authority }}</span>
    <span v-if="confidenceText" class="wiki-badge">置信度 {{ confidenceText }}</span>
    <span v-if="writableSource" class="wiki-badge writable">可写 · {{ writableSource }}</span>
  </span>
</template>

<style scoped>
.wiki-source-badge {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--lme-gap-xs);
  align-items: center;
}

.wiki-badge {
  padding: 1px var(--lme-gap-sm);
  background: var(--wiki-badge-bg);
  border: 1px solid var(--wiki-badge-border);
  border-radius: var(--wiki-chip-radius);
  color: var(--wiki-badge-text);
  font-size: var(--lme-font-size-xs);
  line-height: 1.6;
  white-space: nowrap;
}

.wiki-badge.writable {
  background: var(--wiki-badge-writable-bg);
  border-color: var(--wiki-badge-writable-text);
  color: var(--wiki-badge-writable-text);
}
</style>
