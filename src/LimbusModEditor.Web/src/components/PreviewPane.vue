<script setup lang="ts">
// 预览面板：图像按尺寸解码 / 文本 / 元数据 / 未知类型不空白
// 二进制走 lme.data 虚拟主机，禁止 base64
// 大图按尺寸解码 + 并发闸门 + 代际守卫

import { computed, ref, watch } from 'vue'
import { usePreviewStore } from '@/stores/preview'
import type { AssetRecord, AssetPreviewResult } from '@/ipc'

const props = defineProps<{
  asset: AssetRecord | null
}>()

const previewStore = usePreviewStore()

// 代际守卫：切换选中时释放不再需要的解码结果
watch(
  () => props.asset?.assetId,
  (newId) => {
    if (newId) {
      previewStore.loadPreview(newId)
    } else {
      previewStore.clearPreview()
    }
  },
  { immediate: true },
)

// 解码后图片 URL（走虚拟主机）
const imageUrl = computed(() => {
  if (!previewStore.state.result?.binaryUrl) return null
  return previewStore.state.result.binaryUrl
})

// 是否大图（> 1MB 需要按尺寸解码）
const isLargeImage = computed(() => {
  return (props.asset?.size ?? 0) > 1024 * 1024
})

const previewKindLabel = computed(() => {
  const kind = previewStore.state.result?.kind
  if (!kind) return ''
  const labels: Record<string, string> = {
    Image: '图像',
    SpriteComposite: 'Sprite 合成',
    Audio: '音频',
    Text: '文本',
    JsonFields: 'JSON 字段',
    Hex: '十六进制',
    Material: '材质',
    Shader: '着色器',
    Video: '视频',
    Atlas: '图集',
    Summary: '摘要',
    None: '无法预览',
  }
  return labels[kind] ?? kind
})

// 图像缩放模式
const imageFit = ref<'contain' | 'original'>('contain')
</script>

<template>
  <div class="preview-pane">
    <!-- 资产基本信息 -->
    <div class="preview-header" v-if="asset">
      <div class="preview-asset-name" :title="asset.logicalPath">
        {{ asset.logicalPath.split('/').pop() || asset.logicalPath }}
      </div>
      <div class="preview-asset-meta">
        <span class="meta-tag type-tag">{{ asset.type }}</span>
        <span class="meta-tag state-tag" :class="'state-' + asset.editState">
          {{ asset.editState }}
        </span>
        <span class="meta-tag size-tag">{{ (asset.size / 1024).toFixed(1) }} KB</span>
      </div>
    </div>

    <!-- 预览内容 -->
    <div class="preview-body">
      <!-- 加载中 -->
      <div v-if="previewStore.state.loading" class="preview-loading">
        <span class="loading-spinner">⏳</span>
        <span>加载中…</span>
      </div>

      <!-- 错误 -->
      <div v-else-if="previewStore.state.error" class="preview-error">
        <span>⚠️</span>
        <span>{{ previewStore.state.error }}</span>
      </div>

      <!-- 图像预览 -->
      <div v-else-if="imageUrl && previewStore.state.result?.kind === 'Image'" class="preview-image-container">
        <div class="preview-image-toolbar">
          <button @click="imageFit = imageFit === 'contain' ? 'original' : 'contain'">
            {{ imageFit === 'contain' ? '实际大小' : '适应窗口' }}
          </button>
          <span v-if="isLargeImage" class="large-badge">大图（按尺寸解码）</span>
        </div>
        <div class="preview-image-wrapper" :class="{ 'fit-original': imageFit === 'original' }">
          <img :src="imageUrl" class="preview-image" />
        </div>
      </div>

      <!-- 文本预览 -->
      <div v-else-if="previewStore.state.result?.kind === 'Text'" class="preview-text">
        <pre class="preview-text-content">{{ previewStore.state.result.rows.find(r => r.label === '内容')?.value ?? '(空)' }}</pre>
      </div>

      <!-- 元数据预览 -->
      <div v-else-if="previewStore.state.result && previewStore.state.result.rows.length > 0" class="preview-metadata">
        <div class="preview-kind-label">{{ previewKindLabel }}</div>
        <dl class="metadata-list">
          <template v-for="row in previewStore.state.result.rows" :key="row.label">
            <dt>{{ row.label }}</dt>
            <dd class="lme-mono">{{ row.value }}</dd>
          </template>
        </dl>
      </div>

      <!-- 未知类型不空白 -->
      <div v-else class="preview-empty">
        <span class="empty-icon">📋</span>
        <span>该资源类型暂无可视化预览</span>
        <div v-if="asset" class="empty-fallback lme-mono">
          <div>路径: {{ asset.logicalPath }}</div>
          <div>类型: {{ asset.type }}</div>
          <div>大小: {{ asset.size }} 字节</div>
        </div>
      </div>
    </div>

    <!-- 性能指标 -->
    <div v-if="previewStore.state.lastDecodeMs > 0" class="preview-perf lme-mono">
      <span>解码耗时: {{ previewStore.state.lastDecodeMs.toFixed(1) }} ms</span>
    </div>
  </div>
</template>

<style scoped>
.preview-pane {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: var(--lme-gap-md);
  gap: var(--lme-gap-md);
  overflow: hidden;
}

.preview-header {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.preview-asset-name {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.preview-asset-meta {
  display: flex;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.meta-tag {
  padding: 2px 6px;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
}

.type-tag {
  color: var(--lme-accent);
}

.state-Unchanged {
  color: var(--lme-text-muted);
}
.state-Modified {
  color: var(--lme-state-modified);
}
.state-Added {
  color: var(--lme-state-added);
}
.state-Deleted {
  color: var(--lme-state-deleted);
}

.size-tag {
  color: var(--lme-text-muted);
}

.preview-body {
  flex: 1;
  overflow: auto;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.preview-loading,
.preview-error,
.preview-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  flex: 1;
  color: var(--lme-text-muted);
}

.preview-error {
  color: var(--lme-error);
}

.preview-image-container {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  flex: 1;
  min-height: 0;
}

.preview-image-toolbar {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.preview-image-toolbar button {
  padding: 2px 8px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
}

.large-badge {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-warning);
}

.preview-image-wrapper {
  flex: 1;
  overflow: auto;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
  min-height: 0;
}

.preview-image-wrapper.fit-original {
  justify-content: flex-start;
  align-items: flex-start;
}

.preview-image {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
}

.preview-text {
  flex: 1;
  overflow: auto;
}

.preview-text-content {
  margin: 0;
  padding: var(--lme-gap-sm);
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--lme-text-primary);
}

.preview-metadata {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.preview-kind-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  padding-bottom: var(--lme-gap-xs);
  border-bottom: 1px solid var(--lme-border);
}

.metadata-list {
  margin: 0;
  display: grid;
  grid-template-columns: auto 1fr;
  gap: var(--lme-gap-xs) var(--lme-gap-md);
}

.metadata-list dt {
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

.metadata-list dd {
  margin: 0;
  color: var(--lme-text-primary);
  font-size: var(--lme-font-size-sm);
  word-break: break-all;
}

.empty-icon {
  font-size: 32px;
}

.empty-fallback {
  margin-top: var(--lme-gap-md);
  padding: var(--lme-gap-sm);
  background: var(--lme-bg-input);
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.preview-perf {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding-top: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border);
}
</style>
