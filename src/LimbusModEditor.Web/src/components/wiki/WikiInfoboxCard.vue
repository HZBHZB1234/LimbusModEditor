<script setup lang="ts">
// 维基信息框卡片（右栏）：黄底标题栏 + 中英文名 + 标签胶囊 + 立绘 + 字段行
// 纯展示组件；字段缺失时不渲染对应区块（不占位、不编造）
import { computed } from 'vue'
import type { InfoboxField } from '@/ipc/types'
import WikiChipList from './WikiChipList.vue'

const props = defineProps<{
  /** 主名（中文） */
  title: string
  /** 次名（原文/英文/副题），可缺省 */
  subtitle?: string
  /** 立绘/封面地址（lme.data 虚拟主机），可缺省 */
  imageUrl?: string
  /** 信息框字段行，可缺省；空值行自动过滤 */
  fields?: InfoboxField[]
  /** 标签胶囊，可缺省 */
  tags?: string[]
}>()

// 过滤掉无值字段：推不出来就不渲染
const rows = computed(() =>
  (props.fields ?? []).filter((f) => (f.value ?? '').trim() !== '' && f.type !== 'image'),
)
</script>

<template>
  <aside class="wiki-infobox">
    <div class="wiki-infobox-header">
      <img v-if="imageUrl" class="wiki-infobox-header-icon" :src="imageUrl" alt="" />
      <div class="wiki-infobox-header-text">
        <div class="wiki-infobox-title">{{ title }}</div>
        <div v-if="subtitle" class="wiki-infobox-subtitle">{{ subtitle }}</div>
      </div>
    </div>

    <div v-if="imageUrl" class="wiki-infobox-portrait">
      <img :src="imageUrl" :alt="title" loading="lazy" decoding="async" />
    </div>

    <WikiChipList v-if="tags && tags.length > 0" :items="tags" class="wiki-infobox-tags" />

    <table v-if="rows.length > 0" class="wiki-infobox-fields">
      <tbody>
        <tr v-for="field in rows" :key="field.label + field.value">
          <th class="wiki-infobox-label" scope="row">{{ field.label }}</th>
          <td class="wiki-infobox-value">{{ field.value }}</td>
        </tr>
      </tbody>
    </table>
  </aside>
</template>

<style scoped>
.wiki-infobox {
  width: var(--wiki-infobox-width);
  background: var(--wiki-infobox-bg);
  border: 1px solid var(--wiki-infobox-border);
  border-radius: var(--lme-radius-lg);
  overflow: hidden;
  font-size: var(--lme-font-size-md);
}

.wiki-infobox-header {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--wiki-infobox-header-bg);
  color: var(--wiki-infobox-header-text);
}

.wiki-infobox-header-icon {
  width: 32px;
  height: 32px;
  object-fit: contain;
}

.wiki-infobox-header-text {
  min-width: 0;
}

.wiki-infobox-title {
  font-size: var(--lme-font-size-lg);
  font-weight: 700;
  line-height: 1.3;
}

.wiki-infobox-subtitle {
  font-size: var(--lme-font-size-sm);
  color: var(--wiki-infobox-subtitle-text);
}

.wiki-infobox-portrait {
  background: var(--wiki-gallery-bg);
  border-bottom: 1px solid var(--wiki-infobox-border);
}

.wiki-infobox-portrait img {
  display: block;
  width: 100%;
  height: auto;
  max-height: 360px;
  object-fit: contain;
}

.wiki-infobox-tags {
  padding: var(--lme-gap-sm) var(--lme-gap-md) 0;
}

.wiki-infobox-fields {
  width: 100%;
  border-collapse: collapse;
}

.wiki-infobox-label {
  width: 34%;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  text-align: left;
  vertical-align: top;
  font-weight: 600;
  color: var(--wiki-infobox-label-text);
  border-top: 1px solid var(--wiki-infobox-border);
  white-space: nowrap;
}

.wiki-infobox-value {
  padding: var(--lme-gap-sm) var(--lme-gap-md) var(--lme-gap-sm) 0;
  vertical-align: top;
  color: var(--wiki-infobox-value-text);
  word-break: break-word;
}
</style>
