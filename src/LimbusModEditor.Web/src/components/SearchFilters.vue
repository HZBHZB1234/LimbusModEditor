<script setup lang="ts">
// 搜索与筛选栏（对应 WPF 旧界面的筛选栏）

import { ref, watch } from 'vue'
import type { AssetSearchQuery, AssetSortKind } from '@/ipc'

const props = defineProps<{
  modelValue: AssetSearchQuery
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: AssetSearchQuery): void
  (e: 'search', value: AssetSearchQuery): void
}>()

const localQuery = ref<AssetSearchQuery>({ ...props.modelValue })
let debounceTimer: ReturnType<typeof setTimeout> | null = null

watch(
  () => props.modelValue,
  (v) => {
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
  emit('search', { ...localQuery.value })
}

function clearFilters() {
  localQuery.value = {
    sort: 'Name',
    hasContainerEntry: true,
    showStaticTables: false,
  }
  emit('search', { ...localQuery.value })
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
</script>

<template>
  <div class="search-filters">
    <!-- 搜索框 -->
    <div class="filter-row">
      <input
        v-model="localQuery.text"
        class="search-input"
        type="text"
        placeholder="搜索资源名称或路径（支持中文）"
        @input="onSearchInput"
      />
    </div>

    <!-- 筛选下拉 -->
    <div class="filter-row">
      <label class="filter-label">类型</label>
      <select v-model="localQuery.type" class="filter-select" @change="onFilterChange">
        <option v-for="opt in assetTypeOptions" :key="opt.value" :value="opt.value || undefined">
          {{ opt.label }}
        </option>
      </select>

      <label class="filter-label">排序</label>
      <select v-model="localQuery.sort" class="filter-select" @change="onFilterChange">
        <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
          {{ opt.label }}
        </option>
      </select>

      <label class="filter-checkbox">
        <input
          type="checkbox"
          :checked="localQuery.hasContainerEntry !== false"
          @change="localQuery.hasContainerEntry = ($event.target as HTMLInputElement).checked; onFilterChange()"
        />
        仅容器内
      </label>

      <label class="filter-checkbox">
        <input
          type="checkbox"
          :checked="localQuery.showStaticTables === true"
          @change="localQuery.showStaticTables = ($event.target as HTMLInputElement).checked; onFilterChange()"
        />
        显示静态表
      </label>

      <button class="filter-clear-btn" @click="clearFilters">清除筛选</button>
    </div>
  </div>
</template>

<style scoped>
.search-filters {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
}

.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.search-input {
  flex: 1;
  min-width: 200px;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-md);
  font-family: var(--lme-font-family);
}

.search-input:focus {
  outline: none;
  border-color: var(--lme-accent);
}

.filter-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.filter-select {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
}

.filter-checkbox {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
}

.filter-checkbox input[type='checkbox'] {
  accent-color: var(--lme-accent);
}

.filter-clear-btn {
  padding: var(--lme-gap-xs) var(--lme-gap-md);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
}

.filter-clear-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}
</style>
