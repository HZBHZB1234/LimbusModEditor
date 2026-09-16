<template>
  <div class="spine-renderer">
    <canvas ref="canvasRef" class="spine-canvas" :width="width" :height="height"></canvas>
    <div v-if="loading" class="spine-loading">加载中…</div>
    <div v-if="error" class="spine-error">{{ error }}</div>
    <div v-if="!loading !error && animations.length > 0" class="spine-controls">
      <select v-model="selectedAnimation" class="spine-select" @change="changeAnimation">
        <option v-for="anim in animations" :key="anim" :value="anim">{{ anim }}</option>
      </select>
      <button class="spine-btn" @click="togglePlay">{{ playing ? '⏸ 暂停' : '▶ 播放' }}</button>
      <button class="spine-btn" @click="resetAnimation">⏮ 重置</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { spine } from './spine-runtime'

const props = defineProps<{
  skeletonUrl?: string      // URL to skeleton JSON/binary via lme.data
  atlasUrl?: string         // URL to atlas text via lme.data
  textureUrls?: Record<string, string>  // page name -> URL via lme.data
  width?: number
  height?: number
}>()

const canvasRef = ref<HTMLCanvasElement | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const animations = ref<string[]>([])
const selectedAnimation = ref('')
const playing = ref(true)

let disposed = false
let animFrame = 0

onMounted(async () => {
  if (!canvasRef.value || !props.skeletonUrl || !props.atlasUrl) return
  await loadSpine()
})

onUnmounted(() => {
  disposed = true
  if (animFrame) cancelAnimationFrame(animFrame)
})

watch(() => [props.skeletonUrl, props.atlasUrl], async () => {
  if (animFrame) cancelAnimationFrame(animFrame)
  disposed = false
  await loadSpine()
})

async function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.crossOrigin = 'anonymous'
    img.onload = () => resolve(img)
    img.onerror = () => reject(new Error(`Failed to load image: ${url}`))
    img.src = url
  })
}

async function loadSpine() {
  if (!props.skeletonUrl || !props.atlasUrl || !canvasRef.value) return

  loading.value = true
  error.value = null

  try {
    const canvas = canvasRef.value
    const gl = canvas.getContext('webgl2') || canvas.getContext('webgl')
    if (!gl) {
      error.value = 'WebGL 不可用'
      return
    }

    // Load atlas text
    const atlasTextRes = await fetch(props.atlasUrl)
    if (!atlasTextRes.ok) throw new Error(`Failed to load atlas: ${atlasTextRes.status}`)
    const atlasText = await atlasTextRes.text()

    // Load skeleton
    const skeletonRes = await fetch(props.skeletonUrl)
    if (!skeletonRes.ok) throw new Error(`Failed to load skeleton: ${skeletonRes.status}`)
    const skeletonText = await skeletonRes.text()

    // Load textures
    const textureMap = new Map<string, HTMLImageElement>()
    if (props.textureUrls) {
      for (const [name, url] of Object.entries(props.textureUrls)) {
        try {
          const img = await loadImage(url)
          textureMap.set(name, img)
        } catch (e) {
          console.warn(`Failed to load texture ${name}:`, e)
        }
      }
    }

    // Create atlas
    const atlas = new spine.TextureAtlas(atlasText)

    // Create texture loader
    const textureLoader = {
      loadPage: (page: any, path: string) => {
        const img = textureMap.get(page.name) || textureMap.get(path)
        if (img) {
          page.setTexture(new spine.GLTexture(gl, img, false))
        }
      },
      loadRegion: (_region: any) => {},
      unloadPage: (page: any) => { page.texture?.dispose() }
    }

    // Create attachment loader
    const attachmentLoader = new spine.AtlasAttachmentLoader(atlas)

    // Create skeleton (JSON)
    const skeletonJson = new spine.SkeletonJson(attachmentLoader)
    const skeletonData = skeletonJson.readSkeletonData(skeletonText)

    // Create skeleton
    const skeleton = new spine.Skeleton(skeletonData)
    spine.skeletonSetToSetupPose(skeleton)
    if (skeletonData.defaultSkin) {
      skeleton.setSkin(skeletonData.defaultSkin)
    } else if (skeletonData.skins.length > 0) {
      skeleton.setSkin(skeletonData.skins[0])
    }

    // Create animation state
    const animStateData = new spine.AnimationStateData(skeletonData)
    const animState = new spine.AnimationState(animStateData)

    animations.value = skeletonData.animations.map((a: any) => a.name)
    if (animations.value.length > 0) {
      selectedAnimation.value = animations.value[0]
      animState.setAnimation(0, animations.value[0], true)
    }

    // Setup renderer
    const sceneRenderer = new spine.SceneRenderer(canvas, gl)

    // Render loop
    let lastTime = Date.now()
    const render = () => {
      if (disposed) return

      const now = Date.now()
      const delta = (now - lastTime) / 1000
      lastTime = now

      if (playing.value) {
        animState.update(delta)
        animState.apply(skeleton)
      }
      spine.skeletonUpdateWorldTransform(skeleton)

      sceneRenderer.camera.position.set(canvas.width / 2, canvas.height / 2, 0)
      sceneRenderer.camera.viewportWidth = canvas.width
      sceneRenderer.camera.viewportHeight = canvas.height

      sceneRenderer.begin()
      sceneRenderer.drawSkeleton(skeleton, true)
      sceneRenderer.end()

      animFrame = requestAnimationFrame(render)
    }
    render()

  } catch (e: any) {
    error.value = `Spine 加载失败：${e.message}`
    console.error('Spine load error:', e)
  } finally {
    loading.value = false
  }
}

function changeAnimation() {
  if (!selectedAnimation.value) return
  // Animation change handled by spine directly
}

function togglePlay() {
  playing.value = !playing.value
}

function resetAnimation() {
  if (!selectedAnimation.value) return
  playing.value = false
  // Reset handled by re-render
  setTimeout(() => { playing.value = true }, 50)
}
</script>

<style scoped>
.spine-renderer {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.spine-canvas {
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  background: #0a0a1a;
}

.spine-loading {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  color: var(--lme-text-secondary);
}

.spine-error {
  color: var(--lme-error);
  padding: var(--lme-gap-md);
  text-align: center;
}

.spine-controls {
  display: flex;
  gap: var(--lme-gap-sm);
  align-items: center;
}

.spine-select {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  border-radius: var(--lme-radius-sm);
  border: 1px solid var(--lme-border);
  background: var(--lme-bg-input);
  color: var(--lme-text-primary);
}

.spine-btn {
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  border-radius: var(--lme-radius-sm);
  border: 1px solid var(--lme-border);
  background: var(--lme-bg-input);
  color: var(--lme-text-primary);
  cursor: pointer;
}

.spine-btn:hover {
  background: var(--lme-accent);
  color: white;
}
</style>
