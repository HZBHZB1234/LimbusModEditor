<script setup lang="ts">
// 设置页面
// 共享目录配置 + 外观 + 项目元数据 + UI 状态持久化 + 第三方许可
// 无需打开项目即可使用（仅共享配置与外观）
//
// 说明：外观偏好（明暗 / 强调色）走 localStorage，不进 IPC —— 后端 config.read/write
// 只接受 5 个路径类白名单键（见 CONFIG_KEYS 注释）。因此它不参与「保存设置」。

import { computed, onMounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NSlider,
  NTag,
  NTooltip,
  useMessage,
} from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import { ipc } from '@/ipc'
import { useAppearanceStore } from '@/stores/appearance'
import { useStatusStore } from '@/stores/status'
import { useUiStateStore } from '@/stores/uiState'
import { ACCENT_OPTIONS, THEME_OPTIONS } from '@/theme/preferences'

const uiState = useUiStateStore()
const status = useStatusStore()
const appearance = useAppearanceStore()

/** 轻量反馈（App.vue 的 NMessageProvider 已在位） */
const message = useMessage()

// ── 目录配置 ──
interface DirectoryConfig {
  key: string
  label: string
  /** 这项目录是干什么用的 —— 悬停提示与副标题共用 */
  purpose: string
  path: string
  placeholder: string
}

const directories = ref<DirectoryConfig[]>([
  {
    key: 'gameDir',
    label: '游戏目录',
    purpose: '游戏根目录，需包含 LimbusCompany.exe 与 LimbusCompany_Data',
    path: '',
    placeholder: '选择或粘贴游戏根目录路径',
  },
  {
    key: 'cacheDir',
    label: '缓存目录',
    purpose: 'Unity 缓存目录，用于解析资源包与贴图',
    path: '',
    placeholder: '选择或粘贴缓存目录路径',
  },
  {
    key: 'modDir',
    label: '模组目录',
    purpose: '模组输出目录，导出与保存模组时写入此处',
    path: '',
    placeholder: '选择或粘贴模组输出目录路径',
  },
  {
    key: 'fmodDir',
    label: 'FMOD 目录',
    purpose: 'FMOD 项目目录，编辑音频时需要',
    path: '',
    placeholder: '选择或粘贴 FMOD 项目目录路径',
  },
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
const loading = ref(true)
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

/** 已配置的目录数，用于页头概览 */
const configuredCount = computed(() => directories.value.filter((d) => d.path.trim()).length)

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
      status.notify('info', `已选择${dir.label}：${result.path}`)
    }
  } catch {
    // 取消选择属正常操作；后端尚未实现时提示改为手动输入
    status.notify('warning', `未能打开文件夹选择器，可直接在输入框粘贴${dir.label}路径`)
  }
}

async function autoConfigure() {
  try {
    // 返回字段是**共享配置键名**（gameDirectory / unityCacheDirectory / modDirectory /
    // fmodLibraryDirectory，camelCase），不是前端的 dir.key —— 这里必须经 CONFIG_KEYS
    // 映射一次；直接用 dir.key 取会永远取不到，探测结果一个都填不进来。
    const result = await ipc.request<Record<string, string | null | undefined>>(
      'config.autoDetect',
      {},
    )
    let hit = 0
    for (const dir of directories.value) {
      const key = CONFIG_KEYS[dir.key]
      const value = key ? result?.[key] : undefined
      if (typeof value === 'string' && value.length > 0) {
        dir.path = value
        hit += 1
      }
    }
    if (hit > 0) {
      status.notify('success', `自动检测到 ${hit} 个目录，核对无误后点「保存设置」`)
      message.success(`自动检测到 ${hit} 个目录`)
    } else {
      status.notify('warning', '未能自动检测到目录，请手动指定游戏目录')
      message.warning('未能自动检测到目录')
    }
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : String(e)
    status.notify('error', `自动检测失败：${msg}`)
    message.error(`自动检测失败：${msg}`)
  }
}

/** 列宽变更：钳制 260–2000（与 UiStateService 一致）后写回 store */
function clampColumnWidth(value: number) {
  columnWidth.value = Math.max(260, Math.min(2000, value))
  uiState.setPreviewColumnWidth(columnWidth.value)
}

async function saveSettings() {
  saveStatus.value = 'saving'
  errorMessage.value = ''
  try {
    await status.track(
      'settings.save',
      '正在保存设置',
      async () => {
        // 契约方法 config.write：一次一个 { key, value }，逐个目录写回
        for (const dir of directories.value) {
          const key = CONFIG_KEYS[dir.key]
          if (!key) continue
          await ipc.request('config.write', { key, value: dir.path })
        }
        // 列宽走 uiState.write（契约 §2.7）
        await ipc.request('uiState.write', { pageKey: 'settings', columnWidth: columnWidth.value })
      },
      { successText: '设置已保存', errorPrefix: '保存设置' },
    )
    saveStatus.value = 'saved'
    message.success('设置已保存')
  } catch (e: unknown) {
    saveStatus.value = 'error'
    errorMessage.value = e instanceof Error ? e.message : String(e)
    message.error(`保存设置失败：${errorMessage.value}`)
  }
}

function cancelSettings() {
  // 重置为默认值
  columnWidth.value = 360
  uiState.setPreviewColumnWidth(360)
  status.notify('info', '已恢复默认列宽（尚未保存）')
}

/** 外观为纯前端偏好，立即生效且自动持久化，无需走「保存设置」 */
function resetAppearance() {
  appearance.reset()
  status.notify('info', '已恢复默认外观：深色 + 琥珀')
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
    // 配置加载失败时保留默认值，页头会提示「配置已加载」以外的状态
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="settings-view">
    <PageHeader
      icon="settings"
      title="设置"
      description="配置游戏与工具目录、外观偏好与界面状态。无需打开项目即可修改。"
      hint="首次使用请先配置「游戏目录」——先点『自动检测』让它自己找，找不到再手动指定或粘贴路径。目录改完记得点页面底部的『保存设置』；外观是即时生效的，不用保存。"
      hint-key="settings"
    >
      <template #meta>
        <NTag size="small" :bordered="false" :type="loading ? 'info' : 'success'">
          {{ loading ? '正在读取配置' : `已配置 ${configuredCount}/${directories.length} 个目录` }}
        </NTag>
      </template>
    </PageHeader>

    <div class="settings-body">
      <div class="settings-container">
        <!-- 共享目录配置 -->
        <NCard class="settings-section" size="small">
          <template #header>
            <span class="section-head">
              <span class="section-head-icon"><AppIcon name="folderOpen" :size="14" /></span>
              共享目录配置
            </span>
          </template>

          <p class="section-desc">
            配置游戏与工具目录，无需打开项目即可使用。路径可手动粘贴，也可用右侧按钮浏览选择。
          </p>

          <NForm class="directory-list" label-placement="top" size="small" :show-feedback="false">
            <NFormItem
              v-for="dir in directories"
              :key="dir.key"
              class="directory-item"
              :label="dir.label"
            >
              <div class="directory-row">
                <NInput
                  class="directory-path"
                  size="small"
                  :value="dir.path"
                  :placeholder="dir.placeholder"
                  :loading="loading"
                  @update:value="(v: string) => (dir.path = v)"
                />
                <NTooltip trigger="hover" placement="top-end">
                  <template #trigger>
                    <NButton class="btn btn-browse" size="small" @click="browseDirectory(dir.key)">
                      <template #icon><AppIcon name="folderOpen" :size="14" /></template>
                      浏览
                    </NButton>
                  </template>
                  {{ dir.purpose }}
                </NTooltip>
              </div>
              <p class="directory-purpose">{{ dir.purpose }}</p>
            </NFormItem>
          </NForm>

          <div class="section-actions">
            <NButton class="btn btn-primary" type="primary" size="small" @click="autoConfigure">
              <template #icon><AppIcon name="scanSearch" :size="14" /></template>
              自动检测
            </NButton>
            <span class="section-hint">让工具自动扫描常见安装位置，找不到再手动指定。</span>
          </div>
        </NCard>

        <!-- 外观 -->
        <NCard class="settings-section" size="small">
          <template #header>
            <span class="section-head">
              <span class="section-head-icon"><AppIcon name="palette" :size="14" /></span>
              外观
            </span>
          </template>

          <p class="section-desc">
            主题明暗与强调色。偏好保存在本机，不写入项目文件，重启后仍然生效。
          </p>

          <div class="appearance-group">
            <div class="appearance-label">主题</div>
            <div class="option-grid">
              <button
                v-for="opt in THEME_OPTIONS"
                :key="opt.id"
                class="option-card"
                :class="{ active: appearance.mode === opt.id }"
                :title="opt.description"
                @click="appearance.setMode(opt.id)"
              >
                <AppIcon :name="opt.id === 'dark' ? 'moon' : 'sun'" :size="16" class="option-icon" />
                <span class="option-text">
                  <span class="option-name">{{ opt.label }}</span>
                  <span class="option-desc">{{ opt.description }}</span>
                </span>
                <AppIcon
                  v-if="appearance.mode === opt.id"
                  name="check"
                  :size="14"
                  class="option-check"
                />
              </button>
            </div>
          </div>

          <div class="appearance-group">
            <div class="appearance-label">强调色</div>
            <div class="option-grid">
              <button
                v-for="opt in ACCENT_OPTIONS"
                :key="opt.id"
                class="option-card"
                :class="{ active: appearance.accent === opt.id }"
                :data-accent="opt.id"
                :title="opt.label + ' —— ' + opt.description"
                @click="appearance.setAccent(opt.id)"
              >
                <span class="accent-dot" />
                <span class="option-text">
                  <span class="option-name">{{ opt.label }}</span>
                  <span class="option-desc">{{ opt.description }}</span>
                </span>
                <AppIcon
                  v-if="appearance.accent === opt.id"
                  name="check"
                  :size="14"
                  class="option-check"
                />
              </button>
            </div>
          </div>

          <div class="section-actions">
            <NButton class="btn btn-secondary" size="small" @click="resetAppearance">
              <template #icon><AppIcon name="refresh" :size="14" /></template>
              恢复默认外观
            </NButton>
            <span class="section-hint">默认：深色 + 琥珀。</span>
          </div>
        </NCard>

        <!-- 项目元数据 -->
        <NCard class="settings-section" size="small">
          <template #header>
            <span class="section-head">
              <span class="section-head-icon"><AppIcon name="tag" :size="14" /></span>
              项目元数据
            </span>
          </template>

          <p class="section-desc">当前项目的模组信息。</p>

          <div class="section-note">
            <AppIcon name="warning" :size="13" class="section-note-icon" />
            <span>
              后端目前没有对应的配置键，这里的填写只在本次会话内保留，<strong>不会</strong>写入项目文件。项目名请在「项目管理」页新建项目时指定。
            </span>
          </div>

          <NForm class="meta-list" label-placement="top" size="small" :show-feedback="false">
            <NFormItem class="meta-item" label="模组名称">
              <NInput
                class="meta-input"
                v-model:value="projectMeta.modName"
                size="small"
                placeholder="输入模组名称"
              />
            </NFormItem>
            <NFormItem class="meta-item" label="作者">
              <NInput
                class="meta-input"
                v-model:value="projectMeta.author"
                size="small"
                placeholder="输入作者名称"
              />
            </NFormItem>
            <NFormItem class="meta-item" label="描述">
              <NInput
                class="meta-textarea"
                v-model:value="projectMeta.description"
                type="textarea"
                :rows="3"
                :resizable="false"
                placeholder="输入模组描述"
              />
            </NFormItem>
          </NForm>
        </NCard>

        <!-- UI 状态持久化 -->
        <NCard class="settings-section" size="small">
          <template #header>
            <span class="section-head">
              <span class="section-head-icon"><AppIcon name="sliders" :size="14" /></span>
              界面状态
            </span>
          </template>

          <p class="section-desc">
            预览面板的列宽偏好，点「保存设置」时随目录一起写入（钳制范围 260–2000）。
          </p>

          <div class="meta-list">
            <div class="meta-item width-item">
              <span class="meta-label">预览列宽度</span>
              <div class="width-row">
                <NSlider
                  class="width-slider"
                  :value="columnWidth"
                  :min="260"
                  :max="2000"
                  :step="10"
                  :tooltip="false"
                  @update:value="clampColumnWidth"
                />
                <span class="width-value lme-mono">{{ columnWidth }}px</span>
              </div>
            </div>
          </div>
        </NCard>

        <!-- 第三方许可 -->
        <NCard class="settings-section" size="small">
          <template #header>
            <span class="section-head">
              <span class="section-head-icon"><AppIcon name="info" :size="14" /></span>
              第三方许可
            </span>
          </template>

          <p class="section-desc">本工具使用了以下第三方库。</p>

          <div class="license-list">
            <div class="license-item">
              <div class="license-header">
                <span class="license-name">Spine Runtimes (spine-webgl@4.0.26)</span>
                <NTag class="license-type" size="small" :bordered="false">运行时</NTag>
              </div>
              <p class="license-desc">
                Spine 骨骼动画渲染库。随包分发，但每位用户须自行持有有效的 Spine Editor 许可证。
              </p>
              <p class="license-copyright">Copyright (c) 2013-2025, Esoteric Software LLC</p>
            </div>
          </div>
        </NCard>

        <!-- 操作按钮 -->
        <div class="settings-actions">
          <NButton
            class="btn btn-primary"
            type="primary"
            size="small"
            :loading="saveStatus === 'saving'"
            :disabled="saveStatus === 'saving' || loading"
            @click="saveSettings"
          >
            <template #icon><AppIcon name="save" :size="14" /></template>
            {{ saveStatus === 'saving' ? '保存中…' : '保存设置' }}
          </NButton>
          <NButton
            class="btn btn-secondary"
            size="small"
            :disabled="saveStatus === 'saving'"
            @click="cancelSettings"
          >
            <template #icon><AppIcon name="undo" :size="14" /></template>
            取消
          </NButton>
          <span v-if="saveStatus === 'saved'" class="save-status saved">
            <AppIcon name="check" :size="13" />
            已保存
          </span>
        </div>

        <NAlert
          v-if="saveStatus === 'error'"
          class="error-banner"
          type="error"
          :closable="false"
          title="保存设置失败"
        >
          {{ errorMessage }}
        </NAlert>
      </div>
    </div>
  </div>
</template>

<style scoped>
.settings-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: var(--lme-bg-base);
}

.settings-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
}

.settings-container {
  max-width: 720px;
  margin: 0 auto;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

/* ── 分区（NCard 容器：标题/内边距由卡片给定，此处只留纵向间距） ── */
.settings-section {
  display: block;
}

.section-head {
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.section-head-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: var(--lme-radius-sm);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
}

.section-desc {
  margin: 0 0 var(--lme-gap-md) 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.section-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.section-note {
  display: flex;
  align-items: flex-start;
  gap: var(--lme-gap-sm);
  margin-bottom: var(--lme-gap-md);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border: 1px solid var(--lme-warning-border);
  border-radius: var(--lme-radius-sm);
  background: var(--lme-warning-subtle);
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
  color: var(--lme-text-secondary);
}

.section-note-icon {
  flex-shrink: 0;
  margin-top: 1px;
  color: var(--lme-warning);
}

.section-note strong {
  color: var(--lme-text-primary);
}

/* ── 目录列表 ── */
.directory-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.directory-row {
  display: flex;
  gap: var(--lme-gap-sm);
  width: 100%;
}

.directory-path {
  flex: 1;
  min-width: 0;
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
}

.directory-purpose {
  margin: var(--lme-gap-2xs) 0 0 0;
  font-size: var(--lme-font-size-xs);
  line-height: var(--lme-line-height-normal);
  color: var(--lme-text-muted);
}

/* ── 外观 ── */
.appearance-group + .appearance-group {
  margin-top: var(--lme-gap-lg);
}

.appearance-label {
  margin-bottom: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-medium);
  letter-spacing: 0.04em;
  color: var(--lme-text-muted);
}

.option-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
  gap: var(--lme-gap-sm);
}

.option-card {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-input);
  color: var(--lme-text-secondary);
  font-family: inherit;
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    border-color var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.option-card:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* 选中态用色块自己的强调色（option-card 带 data-accent） */
.option-card.active {
  border-color: var(--lme-accent-border);
  background: var(--lme-accent-subtle);
  color: var(--lme-text-primary);
}

.option-icon {
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.option-card.active .option-icon {
  color: var(--lme-accent);
}

/* 色块自身的 --lme-accent 由 data-accent 决定，不写字面量色值 */
.accent-dot {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-full);
  background: var(--lme-accent);
  box-shadow: inset 0 0 0 1px var(--lme-bg-base);
}

.option-text {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
}

.option-name {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-medium);
  color: var(--lme-text-primary);
}

.option-desc {
  font-size: var(--lme-font-size-2xs);
  line-height: var(--lme-line-height-tight);
  color: var(--lme-text-muted);
}

.option-check {
  margin-left: auto;
  flex-shrink: 0;
  color: var(--lme-accent);
}

/* ── 元数据列表 ── */
.meta-list {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.meta-item {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.width-item {
  width: 100%;
}

.meta-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  font-weight: 500;
}

.meta-input,
.meta-textarea {
  width: 100%;
  font-size: var(--lme-font-size-sm);
}

/* ── 宽度滑块 ── */
.width-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
}

.width-slider {
  flex: 1;
  min-width: 0;
  max-width: 260px;
}

.width-value {
  min-width: 60px;
  text-align: right;
  color: var(--lme-text-secondary);
}

/* ── 按钮：外观由 NButton 主题承担，类名保留供回归定位 ── */
.btn-browse {
  white-space: nowrap;
  flex-shrink: 0;
}

.section-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-md);
  flex-wrap: wrap;
}

/* ── 底部操作 ── */
.settings-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-lg) 0;
}

.save-status {
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-2xs);
  font-size: var(--lme-font-size-sm);
}

.save-status.saved {
  color: var(--lme-success);
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
  flex-shrink: 0;
}

.license-desc {
  margin: 0 0 var(--lme-gap-sm) 0;
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
  line-height: 1.5;
}

.license-copyright {
  margin: 0;
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-xs);
  font-family: var(--lme-font-mono);
}
</style>
