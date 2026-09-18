<script setup lang="ts">
// 预览面板：图像按尺寸解码 / 文本 / 音频 / 元数据 / 未知类型不空白
// 二进制走 lme.data 虚拟主机，禁止 base64
// 大图按尺寸解码 + 并发闸门 + 代际守卫
// ui-redesign r6：emoji 换 AppIcon；空/错误三态换 StateBlock（带下一步建议）；画布底色走 --lme-canvas-bg

import { computed, ref, watch } from 'vue'
import { NButton } from 'naive-ui'
import { usePreviewStore } from '@/stores/preview'
import AppIcon from '@/components/AppIcon.vue'
import StateBlock from '@/components/StateBlock.vue'
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

// lme.data 虚拟主机上的资源 URL：图像解码结果 / 音频原始二进制共用同一字段
const binaryUrl = computed(() => previewStore.state.result?.binaryUrl ?? null)

const rows = computed(() => previewStore.state.result?.rows ?? [])

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

// 文本预览正文（后端把文本放在 label 为「内容」的行里）
const textContent = computed(() => {
  if (previewStore.state.result?.kind !== 'Text') return ''
  return rows.value.find((r) => r.label === '内容')?.value ?? ''
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
      <!-- 未选中资源 -->
      <StateBlock
        v-if="!asset"
        class="preview-empty fill"
        state="empty"
        icon="image"
        title="尚未选中资源"
        description="在左侧列表点一条资源即可预览"
      />

      <!-- 加载中 -->
      <StateBlock
        v-else-if="previewStore.state.loading"
        class="preview-loading fill"
        state="loading"
        title="正在解码预览…"
      />

      <!-- 错误 -->
      <StateBlock
        v-else-if="previewStore.state.error"
        class="preview-error fill"
        state="error"
        :title="previewStore.state.error"
        description="换一条资源再试；若持续失败，检查该资源是否已被替换过"
      />

      <!-- 图像预览 -->
      <div
        v-else-if="previewStore.state.result?.kind === 'Image' && binaryUrl"
        class="preview-image-container"
      >
        <div class="preview-image-toolbar">
          <NButton size="tiny" @click="imageFit = imageFit === 'contain' ? 'original' : 'contain'">
            <AppIcon name="maximize" :size="13" />
            {{ imageFit === 'contain' ? '实际大小' : '适应窗口' }}
          </NButton>
          <span v-if="isLargeImage" class="large-badge">
            <AppIcon name="info" :size="12" />
            大图（按尺寸解码）
          </span>
        </div>
        <div class="preview-image-wrapper" :class="{ 'fit-original': imageFit === 'original' }">
          <img :src="binaryUrl" class="preview-image" />
        </div>
      </div>

      <!-- 图像无内容 -->
      <StateBlock
        v-else-if="previewStore.state.result?.kind === 'Image'"
        class="preview-empty fill"
        state="empty"
        icon="image"
        title="图像未能解码"
        description="换一条资源再试；大图按尺寸解码较慢，稍等后再选中一次"
      />

      <!-- 音频预览 -->
      <div v-else-if="previewStore.state.result?.kind === 'Audio'" class="preview-audio">
        <audio v-if="binaryUrl" controls :src="binaryUrl" class="audio-player" />
        <StateBlock
          v-else
          class="preview-empty"
          state="empty"
          icon="audio"
          title="音频未就绪"
          description="这条音频暂不能内联播放，可查看下方元数据了解编码与时长"
        />
        <dl v-if="rows.length > 0" class="metadata-list">
          <template v-for="row in rows" :key="row.label">
            <dt>{{ row.label }}</dt>
            <dd class="lme-mono">{{ row.value }}</dd>
          </template>
        </dl>
      </div>

      <!-- 文本预览 -->
      <div v-else-if="previewStore.state.result?.kind === 'Text' && textContent.trim()" class="preview-text">
        <pre class="preview-text-content">{{ textContent }}</pre>
      </div>

      <!-- 文本无内容 -->
      <StateBlock
        v-else-if="previewStore.state.result?.kind === 'Text'"
        class="preview-empty fill"
        state="empty"
        icon="text"
        title="文本内容为空"
        description="换一条资源，或查看右侧元数据里的字段"
      />

      <!-- 元数据预览 -->
      <div v-else-if="rows.length > 0" class="preview-metadata">
        <div class="preview-kind-label">{{ previewKindLabel }}</div>
        <dl class="metadata-list">
          <template v-for="row in rows" :key="row.label">
            <dt>{{ row.label }}</dt>
            <dd class="lme-mono">{{ row.value }}</dd>
          </template>
        </dl>
      </div>

      <!-- 未知类型不空白 -->
      <StateBlock
        v-else
        class="preview-empty fill"
        state="empty"
        icon="info"
        title="该资源类型暂不支持预览"
        description="这个类型暂不支持预览，可查看右侧元数据"
      >
        <div v-if="asset" class="empty-fallback lme-mono">
          <div>路径: {{ asset.logicalPath }}</div>
          <div>类型: {{ asset.type }}</div>
          <div>大小: {{ asset.size }} 字节</div>
        </div>
      </StateBlock>
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
  font-weight: var(--lme-font-weight-semibold);
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

/* 三态（加载 / 空 / 错误）统一由 StateBlock 呈现，这里只让它撑满预览区 */
.preview-loading,
.preview-error,
.preview-empty {
  min-height: 0;
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

.large-badge {
  display: inline-flex;
  align-items: center;
  gap: var(--lme-gap-2xs);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-warning);
}

/* 图像 / 音频画布底：明暗主题都保持深色，避免浅底看不清资源 */
.preview-image-wrapper {
  flex: 1;
  overflow: auto;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--lme-canvas-bg);
  border: 1px solid var(--lme-border);
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

.preview-audio {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.audio-player {
  width: 100%;
  background: var(--lme-canvas-bg);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
}

.preview-text {
  flex: 1;
  overflow: auto;
}

.preview-text-content {
  margin: 0;
  padding: var(--lme-gap-sm);
  background: var(--lme-bg-inset);
  border-radius: var(--lme-radius-sm);
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

.empty-fallback {
  margin-top: var(--lme-gap-md);
  padding: var(--lme-gap-sm);
  background: var(--lme-bg-inset);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-2xs);
  text-align: left;
}

.preview-perf {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  padding-top: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border);
}
</style>
