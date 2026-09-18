<script setup lang="ts">
// 搜索与筛选栏（对应 WPF 旧界面的筛选栏）
// v2：紧凑单行工具栏（窄容器自动换行）；props/emits 接口不变（TextView/BankView/StaticView 共用）
// v4（资产工作台库组件化）：手写 input/select/checkbox/button 换成
//   Naive UI 的 NInput / NSelect / NCheckbox / NButton；
//   防抖（250ms）、筛选即发、清空即发、清除筛选回默认值的判据与时序一字未改

import { ref, watch } from 'vue'
import { NButton, NCheckbox, NInput, NSelect } from 'naive-ui'
import type { AssetSearchQuery, AssetSortKind, AssetType } from '@/ipc'

const props = defineProps<{
  modelValue: AssetSearchQuery
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: AssetSearchQuery): void
  (e: 'search', value: AssetSearchQuery): void
}>()

const localQuery = ref<AssetSearchQuery>({ ...props.modelValue })
let debounceTimer: ReturnType<typeof setTimeout> | null = null

/**
 * 外部判据指纹：只取本栏参与编辑的字段。
 * 用途是区分「外部改的判据（catalog.query 被 store 整体替换）」与「本栏用户刚改的」，
 * 避免在选中资源/翻页导致 store 换新对象时再发一次同样的搜索（时序与手写版一致）。
 */
function filterFingerprint(q: AssetSearchQuery): string {
  return JSON.stringify([q.text ?? '', q.type ?? '', q.sort ?? '', q.hasContainerEntry, q.showStaticTables])
}

/** 已同步出去的本栏状态；外部判据与它不同才回灌，防止表单把用户的下一次输入顶掉 */
let syncedFingerprint = filterFingerprint(localQuery.value)

watch(
  () => props.modelValue,
  (v) => {
    const incoming = filterFingerprint(v)
    if (incoming === syncedFingerprint) return
    syncedFingerprint = incoming
    localQuery.value = { ...v }
  },
  { deep: true },
)

function onSearchInput() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => {
    emit('search', { ...localQuery.value })
  }, 250) // 250ms 防抖（与 WPF 旧界面一致）
}

function onFilterChange() {
  syncedFingerprint = filterFingerprint(localQuery.value)
  emit('search', { ...localQuery.value })
}

function clearFilters() {
  localQuery.value = {
    sort: 'Name',
    hasContainerEntry: true,
    showStaticTables: false,
  }
  onFilterChange()
}

const sortOptions: { value: AssetSortKind; label: string }[] = [
  { value: 'Name', label: '按名称' },
  { value: 'SizeDescending', label: '按大小（大→小）' },
  { value: 'SizeAscending', label: '按大小（小→大）' },
  { value: 'Type', label: '按类型' },
  { value: 'ModifiedFirst', label: '修改在前' },
]

const assetTypeOptions: { value: string; label: string }[] = [
  { value: '', label: '全部类型' },
  { value: 'Texture', label: 'Texture' },
  { value: 'Sprite', label: 'Sprite' },
  { value: 'Audio', label: 'Audio' },
  { value: 'Text', label: 'TextAsset' },
  { value: 'Json', label: 'JSON' },
  { value: 'MonoBehaviour', label: 'MonoBehaviour' },
  { value: 'Font', label: 'Font' },
  { value: 'Mesh', label: 'Mesh' },
  { value: 'Animation', label: 'Animation' },
  { value: 'Material', label: 'Material' },
  { value: 'Shader', label: 'Shader' },
  { value: 'Video', label: 'VideoClip' },
]

/**
 * 库下拉的回灌值可能是 null（清空）或 undefined（未匹配到选项），
 * 统一归一成手写版的口径：空值 = 不按类型筛（原 select 的空 option 给的是 undefined）。
 */
function onTypeChange(): void {
  if (!localQuery.value.type) localQuery.value.type = undefined
  onFilterChange()
}

function onContainerEntryChange(value: boolean): void {
  localQuery.value.hasContainerEntry = value
  onFilterChange()
}

function onShowStaticTablesChange(value: boolean): void {
  localQuery.value.showStaticTables = value
  onFilterChange()
}
</script>

<template>
  <div class="search-filters">
    <!-- 搜索框 -->
    <NInput
      v-model:value="localQuery.text"
      class="search-input"
      size="small"
      clearable
      placeholder="搜索资源名称或路径（支持中文）"
      @input="onSearchInput"
      @clear="onFilterChange"
    >
      <template #prefix>
        <span class="search-input-icon">🔍</span>
      </template>
    </NInput>

    <!-- 筛选下拉 -->
    <div class="filter-row">
      <NSelect
        v-model:value="localQuery.type"
        class="filter-select"
        size="small"
        :options="assetTypeOptions"
        title="按类型筛选"
        @update:value="onTypeChange"
      />

      <NSelect
        v-model:value="localQuery.sort"
        class="filter-select"
        size="small"
        :options="sortOptions"
        title="排序方式"
        @update:value="onFilterChange"
      />

      <NCheckbox
        class="filter-checkbox"
        :checked="localQuery.hasContainerEntry !== false"
        @update:checked="onContainerEntryChange"
      >
        仅容器内
      </NCheckbox>

      <NCheckbox
        class="filter-checkbox"
        :checked="localQuery.showStaticTables === true"
        @update:checked="onShowStaticTablesChange"
      >
        显示静态表
      </NCheckbox>

      <NButton class="filter-clear-btn" size="tiny" tertiary @click="clearFilters">清除筛选</NButton>
    </div>
  </div>
</template>

<style scoped>
.search-filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--lme-gap-sm) var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-panel);
}

/* ── 搜索框（库组件 NInput：底色/边框/聚焦环见 theme/naiveTheme.ts 的 Input 覆盖） ── */
.search-input {
  flex: 1;
  min-width: 220px;
}

.search-input-icon {
  font-size: var(--lme-font-size-xs);
  opacity: 0.6;
}

/* ── 筛选行 ── */
.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.filter-select {
  min-width: 132px;
}

.filter-checkbox {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.filter-clear-btn {
  color: var(--lme-text-muted);
}
</style>
