<script setup lang="ts">
// 维基页面内容编辑器 — 只改内容，不改结构
// 铁律：不新增/删除/重命名/排序 页面、分节、条目、二级页

import { ref, watch } from 'vue'
import type {
  WikiPage,
  WikiSection,
  ResourceBinding,
  ContentEdit,
} from '@/ipc'

// ── Props / Emits ──────────────────────────────────────────
const props = defineProps<{
  page: WikiPage
  editable: boolean
}>()

const emit = defineEmits<{
  (e: 'save', page: WikiPage): void
  (e: 'cancel'): void
}>()

// ── 本地草稿 ───────────────────────────────────────────────
const draft = ref<WikiPage>(structuredClone(props.page))

watch(
  () => props.page,
  (val) => {
    draft.value = structuredClone(val)
  },
  { deep: true },
)

// ── 行内编辑状态 ───────────────────────────────────────────
interface EditingState {
  type: 'section-title' | 'section-content'
  sectionId: string
  field: string
  original: string
}

const editing = ref<EditingState | null>(null)
const editBuffer = ref('')

function startEdit(state: EditingState) {
  if (!props.editable) return
  editing.value = state
  editBuffer.value = state.original
}

function commitEdit() {
  if (!editing.value) return
  const e = editing.value
  const edit: ContentEdit = {
    pageId: draft.value.id,
    sectionId: e.sectionId,
    field: e.field,
    oldValue: e.original,
    newValue: editBuffer.value,
    source: 'user',
    timestamp: Date.now(),
  }
  applyContentEdit(edit)
  editing.value = null
  editBuffer.value = ''
}

function cancelEdit() {
  editing.value = null
  editBuffer.value = ''
}

function applyContentEdit(edit: ContentEdit) {
  const section = draft.value.sections.find((s) => s.id === edit.sectionId)
  if (!section) return
  if (edit.field === 'title') section.title = edit.newValue
  else if (edit.field === 'content') section.content = edit.newValue
}

// ── 资源绑定 ───────────────────────────────────────────────
const bindingUI = ref<Record<string, boolean>>({})
const bindingInput = ref('')

function toggleBindingUI(sectionId: string) {
  bindingUI.value[sectionId] = !bindingUI.value[sectionId]
}

function bindResource(sectionId: string, deepLink: string) {
  if (!deepLink.trim()) return
  const section = draft.value.sections.find((s) => s.id === sectionId)
  if (!section) return
  const binding: ResourceBinding = {
    refKey: deepLink,
    kind: 'resource',
    display: deepLink.split('/').pop() || deepLink,
    deepLink,
  }
  if (!section.bindings) section.bindings = []
  section.bindings.push(binding)
  bindingInput.value = ''
}

function unbindResource(sectionId: string, bindingIndex: number) {
  const section = draft.value.sections.find((s) => s.id === sectionId)
  if (!section || !section.bindings) return
  section.bindings.splice(bindingIndex, 1)
}

// ── Markdown 预览 ──────────────────────────────────────────
const previewSections = ref<Set<string>>(new Set())

function togglePreview(sectionId: string) {
  if (previewSections.value.has(sectionId)) {
    previewSections.value.delete(sectionId)
  } else {
    previewSections.value.add(sectionId)
  }
}

function renderMarkdown(text: string): string {
  if (!text) return ''
  return text
    .replace(/^### (.+)$/gm, '<h3>$1</h3>')
    .replace(/^## (.+)$/gm, '<h2>$1</h2>')
    .replace(/^# (.+)$/gm, '<h1>$1</h1>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/\*(.+?)\*/g, '<em>$1</em>')
    .replace(/`(.+?)`/g, '<code>$1</code>')
    .replace(/\[(.+?)\]\((.+?)\)/g, '<a href="$2">$1</a>')
    .replace(/^- (.+)$/gm, '<li>$1</li>')
    .replace(/\n/g, '<br>')
}

// ── 保存/取消 ──────────────────────────────────────────────
function save() {
  emit('save', draft.value)
}

function cancel() {
  emit('cancel')
}

// ── 来源标签 ───────────────────────────────────────────────
const sourceLabels: Record<string, string> = {
  user: '用户',
  auto: '自动',
  candidate: '候选',
}

function sourceColor(source: string): string {
  const map: Record<string, string> = {
    user: 'var(--lme-accent)',
    auto: 'var(--lme-text-muted)',
    candidate: 'var(--lme-warning)',
  }
  return map[source] || 'var(--lme-text-muted)'
}
</script>

<template>
  <div class="wiki-editor">
    <!-- 页面头部 -->
    <header class="wiki-header">
      <div class="wiki-title-row">
        <h1 class="wiki-title">{{ draft.title }}</h1>
        <span class="wiki-category-tag">{{ draft.category }}</span>
      </div>
      <p v-if="draft.subtitle" class="wiki-subtitle">{{ draft.subtitle }}</p>
    </header>

    <!-- 分节列表 -->
    <div class="wiki-sections">
      <div v-for="section in draft.sections" :key="section.id" class="wiki-section">
        <!-- 分节头部 -->
        <div class="section-header">
          <div class="section-title-area">
            <template v-if="editing?.type === 'section-title' && editing.sectionId === section.id">
              <input v-model="editBuffer" class="inline-edit" @keydown.enter="commitEdit" @keydown.escape="cancelEdit" autofocus />
              <button class="btn-mini" @click="commitEdit">保存</button>
              <button class="btn-mini btn-ghost" @click="cancelEdit">取消</button>
            </template>
            <template v-else>
              <h2 class="section-title" :class="{ editable }" @click="startEdit({ type: 'section-title', sectionId: section.id, field: 'title', original: section.title })">
                {{ section.title }}
              </h2>
            </template>
          </div>
          <div class="section-actions">
            <button class="btn-mini" @click="togglePreview(section.id)">
              {{ previewSections.has(section.id) ? '编辑' : '预览' }}
            </button>
            <button class="btn-mini" @click="toggleBindingUI(section.id)">绑定资源</button>
          </div>
        </div>

        <!-- 分节内容 -->
        <div class="section-content">
          <div v-if="previewSections.has(section.id)" class="markdown-preview" v-html="renderMarkdown(section.content)" />
          <template v-else>
            <template v-if="editing?.type === 'section-content' && editing.sectionId === section.id">
              <textarea v-model="editBuffer" class="content-textarea" rows="8" @keydown.escape="cancelEdit" />
              <div class="edit-actions">
                <button class="btn-mini" @click="commitEdit">保存 (Enter)</button>
                <button class="btn-mini btn-ghost" @click="cancelEdit">取消 (Esc)</button>
              </div>
            </template>
            <template v-else>
              <p class="section-content-text" :class="{ editable }" @click="startEdit({ type: 'section-content', sectionId: section.id, field: 'content', original: section.content })">
                {{ section.content || '（点击编辑内容）' }}
              </p>
            </template>
          </template>
        </div>

        <!-- 资源绑定列表 -->
        <div v-if="section.bindings && section.bindings.length > 0" class="bindings-list">
          <div v-for="(binding, bIdx) in section.bindings" :key="bIdx" class="binding-chip">
            <span class="binding-display">{{ binding.display }}</span>
            <span class="binding-kind">{{ binding.kind }}</span>
            <button class="btn-micro btn-danger" @click="unbindResource(section.id, bIdx)" :disabled="!editable">×</button>
          </div>
        </div>

        <!-- 绑定面板 -->
        <div v-if="bindingUI[section.id]" class="binding-panel">
          <input
            v-model="bindingInput"
            class="binding-input"
            placeholder="输入资源深度链接 (RelationDeepLink)"
            @keydown.enter="bindResource(section.id, bindingInput)"
          />
          <button class="btn-mini" @click="bindResource(section.id, bindingInput)">绑定</button>
        </div>
      </div>
    </div>

    <!-- 操作栏 -->
    <footer class="wiki-actions">
      <button class="btn-primary" @click="save" :disabled="!editable">保存</button>
      <button class="btn-ghost" @click="cancel">取消</button>
    </footer>
  </div>
</template>

<style scoped>
.wiki-editor { display: flex; flex-direction: column; gap: var(--lme-gap-lg); padding: var(--lme-gap-lg); height: 100%; overflow: auto; color: var(--lme-text-primary); }

.wiki-header { border-bottom: 1px solid var(--lme-border); padding-bottom: var(--lme-gap-md); }
.wiki-title-row { display: flex; align-items: center; gap: var(--lme-gap-md); }
.wiki-title { margin: 0; font-size: var(--lme-font-size-xl); font-weight: 700; color: var(--lme-text-primary); }
.wiki-category-tag { padding: 2px 8px; border-radius: var(--lme-radius-sm); background: var(--lme-accent-muted); color: var(--lme-accent-hover); font-size: var(--lme-font-size-xs); font-weight: 600; }
.wiki-subtitle { margin: var(--lme-gap-sm) 0 0; color: var(--lme-text-secondary); font-size: var(--lme-font-size-md); }

.wiki-sections { display: flex; flex-direction: column; gap: var(--lme-gap-lg); }
.wiki-section { border: 1px solid var(--lme-border); border-radius: var(--lme-radius-md); background: var(--lme-bg-panel); overflow: hidden; }
.section-header { display: flex; align-items: center; justify-content: space-between; padding: var(--lme-gap-md); background: var(--lme-bg-elevated); border-bottom: 1px solid var(--lme-border); }
.section-title-area { display: flex; align-items: center; gap: var(--lme-gap-sm); flex: 1; min-width: 0; }
.section-title { margin: 0; font-size: var(--lme-font-size-lg); font-weight: 600; color: var(--lme-text-primary); cursor: default; }
.section-title.editable { cursor: pointer; }
.section-title.editable:hover { color: var(--lme-accent); }
.section-actions { display: flex; gap: var(--lme-gap-xs); }

.section-content { padding: var(--lme-gap-md); }
.section-content-text { margin: 0; font-size: var(--lme-font-size-md); color: var(--lme-text-primary); line-height: 1.7; white-space: pre-wrap; cursor: default; }
.section-content-text.editable { cursor: pointer; }
.section-content-text.editable:hover { background: var(--lme-bg-hover); border-radius: var(--lme-radius-sm); }

.content-textarea { width: 100%; padding: var(--lme-gap-sm); background: var(--lme-bg-input); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-sm); color: var(--lme-text-primary); font-family: var(--lme-font-mono); font-size: var(--lme-font-size-sm); resize: vertical; }
.edit-actions { display: flex; gap: var(--lme-gap-sm); margin-top: var(--lme-gap-sm); }

.inline-edit { flex: 1; padding: var(--lme-gap-xs) var(--lme-gap-sm); background: var(--lme-bg-input); border: 1px solid var(--lme-accent); border-radius: var(--lme-radius-sm); color: var(--lme-text-primary); font-size: var(--lme-font-size-lg); font-weight: 600; }

.markdown-preview { font-size: var(--lme-font-size-md); color: var(--lme-text-primary); line-height: 1.7; }
.markdown-preview :deep(h1) { font-size: var(--lme-font-size-xl); }
.markdown-preview :deep(h2) { font-size: var(--lme-font-size-lg); }
.markdown-preview :deep(h3) { font-size: var(--lme-font-size-md); }
.markdown-preview :deep(code) { background: var(--lme-bg-input); padding: 1px 4px; border-radius: 3px; font-family: var(--lme-font-mono); }

.bindings-list { display: flex; flex-wrap: wrap; gap: var(--lme-gap-xs); padding: 0 var(--lme-gap-md) var(--lme-gap-md); }
.binding-chip { display: flex; align-items: center; gap: var(--lme-gap-xs); padding: 2px 8px; background: var(--lme-bg-elevated); border: 1px solid var(--lme-border); border-radius: 8px; font-size: var(--lme-font-size-xs); }
.binding-display { color: var(--lme-text-primary); }
.binding-kind { color: var(--lme-text-muted); }

.binding-panel { display: flex; gap: var(--lme-gap-sm); padding: 0 var(--lme-gap-md) var(--lme-gap-md); }
.binding-input { flex: 1; padding: var(--lme-gap-xs) var(--lme-gap-sm); background: var(--lme-bg-input); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-sm); color: var(--lme-text-primary); font-size: var(--lme-font-size-sm); }

.wiki-actions { display: flex; gap: var(--lme-gap-md); padding-top: var(--lme-gap-md); border-top: 1px solid var(--lme-border); }

/* 按钮 */
.btn-mini { padding: 3px 10px; background: var(--lme-bg-elevated); border: 1px solid var(--lme-border); border-radius: var(--lme-radius-sm); color: var(--lme-text-secondary); cursor: pointer; font-size: var(--lme-font-size-xs); }
.btn-mini:hover:not(:disabled) { background: var(--lme-bg-hover); color: var(--lme-text-primary); }
.btn-mini:disabled { opacity: 0.4; cursor: not-allowed; }
.btn-ghost { background: transparent; }
.btn-primary { padding: var(--lme-gap-sm) var(--lme-gap-lg); background: var(--lme-accent); border: none; border-radius: var(--lme-radius-sm); color: #fff; cursor: pointer; font-size: var(--lme-font-size-sm); font-weight: 500; }
.btn-primary:disabled { opacity: 0.4; cursor: not-allowed; }
.btn-micro { padding: 1px 4px; background: transparent; border: none; color: var(--lme-text-muted); cursor: pointer; font-size: 12px; }
.btn-micro.btn-danger:hover { color: var(--lme-error); }
</style>
