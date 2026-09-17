<script setup lang="ts">
// 设置页面
// 共享目录配置 + 项目元数据 + UI 状态持久化
// 无需打开项目即可使用（仅共享配置）

import { ref, onMounted } from 'vue'
import { ipc } from '@/ipc'
import { useUiStateStore } from '@/stores/uiState'

const uiState = useUiStateStore()

// ── 目录配置 ──
interface DirectoryConfig {
  key: string
  label: string
  path: string
  placeholder: string
}

const directories = ref<DirectoryConfig[]>([
  { key: 'gameDir', label: '游戏目录', path: '', placeholder: '选择游戏根目录' },
  { key: 'cacheDir', label: '缓存目录', path: '', placeholder: '选择缓存目录' },
  { key: 'modDir', label: '模组目录', path: '', placeholder: '选择模组输出目录' },
  { key: 'fmodDir', label: 'FMOD 目录', path: '', placeholder: '选择 FMOD 项目目录' },
])

// ── 项目元数据 ──
const projectMeta = ref({
  modName: '',
  author: '',
  description: '',
})

// ── UI 状态 ──
const columnWidth = ref(uiState.previewColumnWidth)

// ── 状态 ──
const saveStatus = ref<'idle' | 'saving' | 'saved' | 'error'>('idle')
const errorMessage = ref('')

/**
 * 前端目录键 → 共享配置键。
 * config.read / config.write 只认 gameDirectory / unityCacheDirectory / modDirectory /
 * fmodLibraryDirectory / lastProjectFile 这五个键（SharedConfigKeyValue）。
 */
const CONFIG_KEYS: Record<string, string> = {
  gameDir: 'gameDirectory',
  cacheDir: 'unityCacheDirectory',
  modDir: 'modDirectory',
  fmodDir: 'fmodLibraryDirectory',
}

// ── 方法 ──
async function browseDirectory(key: string) {
  const dir = directories.value.find((d) => d.key === key)
  if (!dir) return
  try {
    const result = await ipc.request<{ path: string }>('dialog.folderPick', {
      title: dir.label,
      startPath: dir.path || undefined,
    })
    if (result.path) {
      dir.path = result.path
    }
  } catch {
    // 暂未实现：文件夹选择取消或失败
  }
}

async function autoConfigure() {
  try {
    const result = await ipc.request<Record<string, string>>('config.autoDetect', {})
    for (const dir of directories.value) {
      if (result[dir.key]) {
        dir.path = result[dir.key]
      }
    }
  } catch {
    // 暂未实现：自动检测失败
  }
}

function clampColumnWidth() {
  columnWidth.value = Math.max(260, Math.min(2000, columnWidth.value))
  uiState.setPreviewColumnWidth(columnWidth.value)
}

async function saveSettings() {
  saveStatus.value = 'saving'
  errorMessage.value = ''
  try {
    // 契约方法 config.write：一次一个 { key, value }，逐个目录写回
    for (const dir of directories.value) {
      const key = CONFIG_KEYS[dir.key]
      if (!key) continue
      await ipc.request('config.write', { key, value: dir.path })
    }
    // 列宽走 uiState.write（契约 §2.7）
    await ipc.request('uiState.write', { pageKey: 'settings', columnWidth: columnWidth.value })
    saveStatus.value = 'saved'
  } catch (e: unknown) {
    saveStatus.value = 'error'
    errorMessage.value = e instanceof Error ? e.message : String(e)
  }
}

function cancelSettings() {
  // 重置为默认值
  columnWidth.value = 360
  uiState.setPreviewColumnWidth(360)
}

// ── 初始化 ──
onMounted(async () => {
  try {
    // 契约方法 config.read：一次一个 { key }，逐个目录读取
    for (const dir of directories.value) {
      const key = CONFIG_KEYS[dir.key]
      if (!key) continue
      const result = await ipc.request<{ value?: string }>('config.read', { key })
      if (result.value) {
        dir.path = result.value
      }
    }
    // 列宽走 uiState.read（契约 §2.7）
    const state = await ipc.request<{ pageKey: string; columnWidth: number }>('uiState.read', {
      pageKey: 'settings',
    })
    if (state.columnWidth) {
      columnWidth.value = state.columnWidth
    }
    // projectMeta（模组名/作者/说明）后端暂无 config 键，不读取、不编造
  } catch {
    // 暂未实现：配置加载失败时使用默认值
  }
})
</script>

<template>
  <div class="settings-view">
    <div class="settings-container">
      <h2 class="settings-title">设置</h2>

      <!-- 共享目录配置 -->
      <section class="settings-section">
        <h3 class="section-title">共享目录配置</h3>
        <p class="section-desc">配置游戏与工具目录，无需打开项目即可使用。</p>

        <div class="directory-list">
          <div
            v-for="dir in directories"
            :key="dir.key"
            class="directory-item"
          >
            <label class="directory-label">{{ dir.label }}</label>
            <div class="directory-row">
              <input
                type="text"
                class="directory-path lme-mono"
                :value="dir.path"
                :placeholder="dir.placeholder"
                readonly
              />
              <button
                class="btn btn-browse"
                @click="browseDirectory(dir.key)"
              >
                📂 浏览
              </button>
            </div>
          </div>
        </div>

        <div class="section-actions">
          <button class="btn btn-primary" @click="autoConfigure">
            🔍 自动检测
          </button>
        </div>
      </section>

      <!-- 项目元数据 -->
      <section class="settings-section">
        <h3 class="section-title">项目元数据</h3>
        <p class="section-desc">当前项目的模组信息（保存项目时写入）。</p>

        <div class="meta-list">
          <div class="meta-item">
            <label class="meta-label">模组名称</label>
            <input
              type="text"
              class="meta-input"
              v-model="projectMeta.modName"
              placeholder="输入模组名称"
            />
          </div>
          <div class="meta-item">
            <label class="meta-label">作者</label>
            <input
              type="text"
              class="meta-input"
              v-model="projectMeta.author"
              placeholder="输入作者名称"
            />
          </div>
          <div class="meta-item meta-full">
            <label class="meta-label">描述</label>
            <textarea
              class="meta-textarea"
              v-model="projectMeta.description"
              placeholder="输入模组描述"
              rows="3"
            />
          </div>
        </div>
      </section>

      <!-- UI 状态持久化 -->
      <section class="settings-section">
        <h3 class="section-title">界面状态</h3>
        <p class="section-desc">列宽等界面偏好会自动保存（钳制范围 260–2000）。</p>

        <div class="meta-list">
          <div class="meta-item">
            <label class="meta-label">预览列宽度</label>
            <div class="width-row">
              <input
                type="range"
                class="width-slider"
                v-model.number="columnWidth"
                min="260"
                max="2000"
                step="10"
                @change="clampColumnWidth"
              />
              <span class="width-value lme-mono">{{ columnWidth }}px</span>
            </div>
          </div>
        </div>
      </section>

      <!-- 第三方许可 -->
      <section class="settings-section">
        <h3 class="section-title">第三方许可</h3>
        <p class="section-desc">本工具使用了以下第三方库。</p>

        <div class="license-list">
          <div class="license-item">
            <div class="license-header">
              <span class="license-name">Spine Runtimes (spine-webgl@4.0.26)</span>
              <span class="license-type">运行时</span>
            </div>
            <p class="license-desc">
              Spine 骨骼动画渲染库。随包分发，但每位用户须自行持有有效的 Spine Editor 许可证。
            </p>
            <p class="license-copyright">Copyright (c) 2013-2025, Esoteric Software LLC</p>
          </div>
        </div>
      </section>

      <!-- 操作按钮 -->
      <div class="settings-actions">
        <button
          class="btn btn-primary"
          :disabled="saveStatus === 'saving'"
          @click="saveSettings"
        >
          {{ saveStatus === 'saving' ? '保存中…' : '保存设置' }}
        </button>
        <button class="btn btn-secondary" @click="cancelSettings">
          取消
        </button>
        <span v-if="saveStatus === 'saved'" class="save-status saved">
          ✓ 已保存
        </span>
        <span v-if="saveStatus === 'error'" class="save-status error">
          ⚠️ {{ errorMessage }}
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.settings-view {
  height: 100%;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
  background: var(--lme-bg-base);
}

.settings-container {
  max-width: 720px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

.settings-title {
  margin: 0;
  font-size: var(--lme-font-size-xl);
  color: var(--lme-text-primary);
}

/* ── 分区 ── */
.settings-section {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.section-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-primary);
}

.section-desc {
  margin: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

/* ── 目录列表 ── */
.directory-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.directory-item {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.directory-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  font-weight: 500;
}

.directory-row {
  display: flex;
  gap: var(--lme-gap-sm);
}

.directory-path {
  flex: 1;
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  min-width: 0;
}

.directory-path::placeholder {
  color: var(--lme-text-disabled);
}

/* ── 元数据列表 ── */
.meta-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.meta-item {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.meta-full {
  grid-column: 1 / -1;
}

.meta-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  font-weight: 500;
}

.meta-input,
.meta-textarea {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  font-family: var(--lme-font-family);
  resize: vertical;
}

.meta-input:focus,
.meta-textarea:focus {
  outline: none;
  border-color: var(--lme-accent);
}

/* ── 宽度滑块 ── */
.width-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
}

.width-slider {
  flex: 1;
  accent-color: var(--lme-accent);
}

.width-value {
  min-width: 60px;
  text-align: right;
  color: var(--lme-text-secondary);
}

/* ── 按钮 ── */
.btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.btn-primary {
  background: var(--lme-accent);
  border-color: var(--lme-accent);
  color: var(--lme-text-primary);
}

.btn-primary:hover {
  background: var(--lme-accent-hover);
}

.btn-primary:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-secondary {
  background: var(--lme-bg-elevated);
  color: var(--lme-text-secondary);
}

.btn-secondary:hover {
  background: var(--lme-bg-hover);
}

.btn-browse {
  background: var(--lme-bg-elevated);
  color: var(--lme-text-secondary);
  white-space: nowrap;
}

.btn-browse:hover {
  background: var(--lme-bg-hover);
}

.section-actions {
  display: flex;
  gap: var(--lme-gap-sm);
}

/* ── 底部操作 ── */
.settings-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-lg) 0;
}

.save-status {
  font-size: var(--lme-font-size-sm);
}

.save-status.saved {
  color: var(--lme-success);
}

.save-status.error {
  color: var(--lme-error);
}

/* ── 第三方许可 ── */
.license-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.license-item {
  padding: var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
}

.license-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: var(--lme-gap-sm);
}

.license-name {
  font-weight: 600;
  color: var(--lme-text-primary);
}

.license-type {
  padding: 2px 8px;
  background: var(--lme-accent);
  color: white;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
}

.license-desc {
  margin: 0 0 var(--lme-gap-sm) 0;
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
  line-height: 1.5;
}

.license-copyright {
  margin: 0;
  color: var(--lme-text-tertiary);
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-family-mono);
}
</style>
