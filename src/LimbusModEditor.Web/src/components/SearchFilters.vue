<script setup lang="ts">
// 搜索与筛选栏（对应 WPF 旧界面的筛选栏）
// v2：紧凑单行工具栏（窄容器自动换行）；props/emits 接口不变（TextView/BankView/StaticView 共用）

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
    <div class="search-box">
      <span class="search-box-icon">🔍</span>
      <input
        v-model="localQuery.text"
        class="search-input"
        type="text"
        placeholder="搜索资源名称或路径（支持中文）"
        @input="onSearchInput"
      />
      <button
        v-if="localQuery.text"
        class="search-box-clear"
        title="清空关键词"
        @click="localQuery.text = ''; onFilterChange()"
      >
        ✕
      </button>
    </div>

    <!-- 筛选下拉 -->
    <div class="filter-row">
      <select v-model="localQuery.type" class="filter-select" title="按类型筛选" @change="onFilterChange">
        <option v-for="opt in assetTypeOptions" :key="opt.value" :value="opt.value || undefined">
          {{ opt.label }}
        </option>
      </select>

      <select v-model="localQuery.sort" class="filter-select" title="排序方式" @change="onFilterChange">
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
  align-items: center;
  flex-wrap: wrap;
  gap: var(--lme-gap-sm) var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-panel);
}

/* ── 搜索框（带内嵌图标与清空钮） ── */
.search-box {
  position: relative;
  flex: 1;
  min-width: 220px;
  display: flex;
  align-items: center;
}

.search-box-icon {
  position: absolute;
  left: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  opacity: 0.6;
  pointer-events: none;
}

.search-input {
  width: 100%;
  padding: 6px var(--lme-gap-2xl);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-full);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard),
    box-shadow var(--lme-dur-fast) var(--lme-ease-standard);
}

.search-input::placeholder {
  color: var(--lme-text-muted);
}

.search-input:focus {
  outline: none;
  border-color: var(--lme-accent);
  box-shadow: var(--lme-shadow-focus);
}

.search-box-clear {
  position: absolute;
  right: var(--lme-gap-xs);
  width: 20px;
  height: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: none;
  border: none;
  border-radius: var(--lme-radius-full);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  cursor: pointer;
}

.search-box-clear:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* ── 筛选行 ── */
.filter-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.filter-select {
  padding: 5px var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  cursor: pointer;
  transition: border-color var(--lme-dur-fast) var(--lme-ease-standard);
}

.filter-select:hover {
  border-color: var(--lme-border-strong);
}

.filter-select:focus {
  outline: none;
  border-color: var(--lme-accent);
}

.filter-checkbox {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  user-select: none;
}

.filter-checkbox input[type='checkbox'] {
  accent-color: var(--lme-accent);
}

.filter-clear-btn {
  padding: 5px var(--lme-gap-md);
  background: none;
  border: none;
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  transition: color var(--lme-dur-fast) var(--lme-ease-standard),
    background var(--lme-dur-fast) var(--lme-ease-standard);
}

.filter-clear-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}
</style>
