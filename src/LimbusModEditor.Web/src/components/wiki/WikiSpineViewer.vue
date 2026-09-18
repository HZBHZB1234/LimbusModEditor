<script setup lang="ts">
// 维基动画预览：包一层 SpineRenderer，负责 URL 装配与外围状态
// 数据源：resource_bindings kind='Spine'（实测仅 134 条，覆盖低 → 有才显示）
// skeleton / atlas / texture 三个地址由宿主页解析（lme.data 虚拟主机），
// 本组件不猜地址、不伪造资源：三者缺失任一即整块不渲染。
import { computed } from 'vue'
import SpineRenderer from '@/components/SpineRenderer.vue'

const props = withDefaults(
  defineProps<{
    /** skeleton（.json / .skel）地址，可缺省 */
    skeletonUrl?: string
    /** atlas 文本地址，可缺省 */
    atlasUrl?: string
    /** atlas 页名 → 纹理地址，可缺省 */
    textureUrls?: Record<string, string>
    /** 区块小标题，可缺省 */
    title?: string
    /** 画布宽/高，可缺省 */
    width?: number
    height?: number
  }>(),
  { width: 420, height: 520 },
)

/** 缺 skeleton 或 atlas 就无法起播 → 不渲染（不占位、不编造） */
const canRender = computed(
  () => !!props.skeletonUrl && !!props.atlasUrl,
)
</script>

<template>
  <section v-if="canRender" class="wiki-spine">
    <h3 v-if="title" class="wiki-spine-title">{{ title }}</h3>
    <div class="wiki-spine-stage">
      <SpineRenderer
        :skeleton-url="skeletonUrl"
        :atlas-url="atlasUrl"
        :texture-urls="textureUrls"
        :width="width"
        :height="height"
      />
    </div>
  </section>
</template>

<style scoped>
.wiki-spine {
  margin: var(--lme-gap-xl) 0;
}

.wiki-spine-title {
  margin: 0 0 var(--lme-gap-sm);
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.wiki-spine-stage {
  display: flex;
  justify-content: center;
  padding: var(--lme-gap-md);
  background: var(--lme-canvas-bg);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}
</style>
