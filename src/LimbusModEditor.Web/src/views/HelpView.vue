<script setup lang="ts">
// 帮助与教程页面
// 左侧吸顶目录 + 右侧文档式内容；含快捷键表与常见问题。
// 2026-09-18 UI 重构：去 emoji（图标走 AppIcon）、加 PageHeader、
// 文档结构由「一列折叠卡」改为「目录 + 正文」双栏，并补上快捷键一节。
// 纯静态内容，不发任何 IPC。

import { computed, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NCollapse, NCollapseItem, NTag } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import type { IconName } from '@/components/icons'

interface HelpSection {
  id: string
  title: string
  icon: IconName
  /** 段落（空串表示留白） */
  content: string[]
}

const sections: HelpSection[] = [
  {
    id: 'quickstart',
    title: '快速入门',
    icon: 'rocket',
    content: [
      '本工具用于编辑 Limbus Company 的模组资源：浏览游戏里的贴图、文本、音频与数据表，把它们替换成自己的内容，再打包成游戏能加载的模组。',
      '三步走：①「项目」页新建一个模组项目 → ②「设置」页确认游戏目录（首次可点自动探测）→ ③「项目」页点「重新扫描游戏资源」建立索引。',
      '之后在「资源 / 音频 / 文本 / 静态数据」四个工作台里改东西，最后到「导出」页打包。',
      '任何时候按 Ctrl+K 可以呼出命令面板，直接跳到任意工作台或维基分类。',
    ],
  },
  {
    id: 'workflow',
    title: '完整工作流',
    icon: 'activity',
    content: [
      '1. 启动与恢复：程序启动时会自动恢复上次的项目并后台重扫索引；没有项目时，「项目」页是起点。',
      '2. 建立索引：扫描只读取缓存、不复制文件；首次较慢，之后靠增量索引，热启动只需几秒。',
      '3. 找资源：「资源」页支持按名称/路径/中文搜索，并可按类型、大小、修改状态筛选；「仅容器内」默认开启，用来隐藏引擎内部的技术性对象。',
      '4. 改资源：选中一条资源后，右侧可以预览；贴图可「替换…」，文本/JSON 可在内置编辑器里改，音频可直接试听。',
      '5. 看改动：改过的资源在列表里会显示「已修改」状态；「导出」页的「生成导出方案」会列出这次到底会写出哪些文件。',
      '6. 打包：在「导出」页执行导出，产物默认落在模组目录（%APPDATA%\\LimbusCompanyMods）。',
    ],
  },
  {
    id: 'shortcuts',
    title: '快捷键',
    icon: 'keyboard',
    content: [
      'Ctrl+K 或 Ctrl+Shift+P：打开命令面板，搜索并跳转到任意工作台、维基分类或执行操作。',
      'Ctrl+1 ~ Ctrl+7：依次切换到 资源 / 音频 / 文本 / 静态数据 / 导出 / 项目 / 维基。',
      'Ctrl+,：打开设置页。',
      'Alt+← / Alt+→：在访问历史里后退 / 前进。',
      '命令面板内：↑↓ 选择、Enter 打开、Esc 关闭。',
    ],
  },
  {
    id: 'format-boundary',
    title: '格式边界',
    icon: 'layers',
    content: [
      'Bank（.bank）：音频银行格式，用于打包游戏音频资源。',
      'Rebank（.rebank）：音频重打包格式，优化 Bank 文件的加载性能。',
      'Carra / Carra2（.carra）：模组分发格式，导出时的默认产物。',
      'Lunartique（.zip）：压缩包格式，用于整体模组分发。',
      'LangPatch（.json）：语言补丁，用 RFC6902 描述对游戏文本的覆盖。',
      'StaticMod（.staticmod）：静态数据修改包。',
      '',
      '导出分组：_fmod（音频）/ _data（通用资源）/ _text（语言文本）/ _static（静态数据）。',
    ],
  },
  {
    id: 'faq',
    title: '常见问题',
    icon: 'help',
    content: [
      '问：怎么确认游戏目录配置正确？',
      '答：游戏目录里应有 LimbusCompany.exe 与 Data 文件夹；「设置」页有自动探测按钮，也可以手动指定。',
      '',
      '问：扫描报「未找到 Unity 缓存目录」怎么办？',
      '答：先在「设置」里指定 Unity 缓存目录，或先启动一次游戏生成缓存；默认位置是 %AppData%\\LocalLow\\Unity\\ProjectMoon_LimbusCompany。',
      '',
      '问：导出后模组没生效？',
      '答：确认产物放进了游戏的模组目录，并检查模组格式与当前游戏版本是否匹配；也可以在导出前先看「导出方案」里列出的写前校验结论。',
      '',
      '问：能编辑哪些资源类型？',
      '答：文本/JSON 可内置编辑；Texture2D 贴图支持预览与替换（RGB24 / RGBA32 / BGRA32 / DXT1 / DXT5）；Sprite 元数据与序列化字段可改；音频支持 FSB→WAV 试听与替换。',
      '',
      '问：项目文件损坏了怎么办？',
      '答：.lmeproj 本质是 JSON，可以尝试手动修复；建议改大动作前先「保存项目」并备份一份。',
    ],
  },
]

// ── 相关文档（本地文档不通过 window.open 打开，改为只展示路径）──
interface DocLink {
  label: string
  path: string
  description: string
  icon: IconName
}

const docLinks: DocLink[] = [
  { label: '使用手册', path: 'docs/USAGE.md', description: '工作流、快捷键与排障', icon: 'library' },
  { label: '前端设计体系', path: 'docs/UI-DESIGN-SYSTEM.md', description: '设计令牌与组件规范', icon: 'palette' },
  { label: 'IPC 契约', path: 'docs/WEB-IPC-CONTRACT.md', description: '前端与宿主的接口约定', icon: 'link' },
  { label: '当前状态', path: 'docs/STATUS.md', description: '验证基线、已知限制与待办', icon: 'activity' },
]

const activeSection = ref(sections[0].id)
const scrollRoot = ref<HTMLElement | null>(null)

/** 滚动时高亮目录项（用 IntersectionObserver 之外的手写阈值，避免依赖容器语义） */
function onScroll() {
  const root = scrollRoot.value
  if (!root) return
  const top = root.getBoundingClientRect().top
  let current = sections[0].id
  for (const s of sections) {
    const el = document.getElementById(s.id)
    if (!el) continue
    // 区块顶部越过滚动容器上方 24px 即认为进入该节
    if (el.getBoundingClientRect().top - top <= 24) current = s.id
  }
  activeSection.value = current
}

function scrollToSection(id: string) {
  const el = document.getElementById(id)
  if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' })
  activeSection.value = id
}

onMounted(() => scrollRoot.value?.addEventListener('scroll', onScroll, { passive: true }))
onUnmounted(() => scrollRoot.value?.removeEventListener('scroll', onScroll))

const version = computed(() => '0.1.0')
</script>

<template>
  <div class="help-view">
    <PageHeader
      icon="help"
      title="帮助与教程"
      description="使用指南、完整工作流、快捷键与常见问题"
      hint="按 Ctrl+K 可以随时呼出命令面板，直接跳到任意工作台；点左侧目录可快速定位本页章节"
      hint-key="help"
    />

    <div ref="scrollRoot" class="help-scroll">
      <div class="help-layout">
        <!-- ── 左侧：吸顶目录 ── -->
        <nav class="help-toc" aria-label="帮助目录">
          <div class="toc-title lme-caps">目录</div>
          <button
            v-for="section in sections"
            :key="section.id"
            class="toc-item"
            :class="{ active: activeSection === section.id }"
            @click="scrollToSection(section.id)"
          >
            <AppIcon :name="section.icon" :size="14" />
            <span>{{ section.title }}</span>
          </button>

          <div class="toc-title lme-caps toc-title-gap">相关文档</div>
          <div v-for="doc in docLinks" :key="doc.path" class="toc-doc">
            <div class="toc-doc-head">
              <AppIcon :name="doc.icon" :size="13" />
              <span class="toc-doc-label">{{ doc.label }}</span>
            </div>
            <code class="toc-doc-path">{{ doc.path }}</code>
            <span class="toc-doc-desc">{{ doc.description }}</span>
          </div>
        </nav>

        <!-- ── 右侧：正文 ── -->
        <div class="help-content">
          <NCard
            v-for="section in sections"
            :id="section.id"
            :key="section.id"
            class="help-section"
            size="small"
          >
            <NCollapse :default-expanded-names="[section.id]" :trigger-areas="['main', 'arrow']">
              <NCollapseItem :name="section.id">
                <template #header>
                  <h2 class="section-title">
                    <span class="section-icon"><AppIcon :name="section.icon" :size="16" /></span>
                    {{ section.title }}
                  </h2>
                </template>
                <div class="section-body">
                  <p
                    v-for="(paragraph, i) in section.content"
                    :key="i"
                    class="section-paragraph"
                    :class="{ 'paragraph-empty': paragraph === '' }"
                  >
                    {{ paragraph || '\u00A0' }}
                  </p>
                </div>
              </NCollapseItem>
            </NCollapse>
          </NCard>

          <!-- 关于 -->
          <NCard class="help-section about-section" size="small" title="关于">
            <div class="about-body">
              <div class="about-row">
                <span class="about-label">程序</span>
                <span class="about-value">Limbus Mod Editor</span>
              </div>
              <div class="about-row">
                <span class="about-label">版本</span>
                <span class="about-value lme-mono">{{ version }}</span>
              </div>
              <div class="about-row">
                <span class="about-label">技术栈</span>
                <span class="about-value">
                  <NTag size="small" :bordered="false">WebView2</NTag>
                  <NTag size="small" :bordered="false">Vue 3</NTag>
                  <NTag size="small" :bordered="false">Naive UI</NTag>
                </span>
              </div>
              <p class="about-note">
                游戏数据始终只读：所有编辑都记录在项目里，只有执行导出才会写出文件。
              </p>
            </div>
          </NCard>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.help-view {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--lme-bg-base);
}

.help-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.help-layout {
  display: grid;
  grid-template-columns: 200px minmax(0, 1fr);
  gap: var(--lme-gap-xl);
  max-width: 1100px;
  margin: 0 auto;
  padding: var(--lme-gap-xl);
  align-items: start;
}

/* ── 目录 ── */
.help-toc {
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.toc-title {
  padding: 0 var(--lme-gap-sm) var(--lme-gap-xs);
}

.toc-title-gap {
  margin-top: var(--lme-gap-lg);
}

.toc-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  width: 100%;
  padding: 6px var(--lme-gap-sm);
  border: none;
  border-left: 2px solid transparent;
  border-radius: 0 var(--lme-radius-md) var(--lme-radius-md) 0;
  background: none;
  color: var(--lme-text-secondary);
  font-family: inherit;
  font-size: var(--lme-font-size-sm);
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.toc-item:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.toc-item.active {
  background: var(--lme-accent-subtle);
  border-left-color: var(--lme-accent);
  color: var(--lme-accent);
  font-weight: var(--lme-font-weight-medium);
}

/* ── 目录里的文档条目 ── */
.toc-doc {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--lme-gap-sm);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border-subtle);
}

.toc-doc + .toc-doc {
  margin-top: var(--lme-gap-xs);
}

.toc-doc-head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--lme-text-secondary);
}

.toc-doc-label {
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-medium);
}

.toc-doc-path {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-accent);
  word-break: break-all;
}

.toc-doc-desc {
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-text-muted);
  line-height: var(--lme-line-height-normal);
}

/* ── 正文 ── */
.help-content {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  min-width: 0;
}

.help-section {
  scroll-margin-top: var(--lme-gap-md);
}

.section-title {
  margin: 0;
  font-size: var(--lme-font-size-md);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.section-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: var(--lme-radius-md);
  background: var(--lme-accent-subtle);
  color: var(--lme-accent);
}

.section-body {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.section-paragraph {
  margin: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  line-height: var(--lme-line-height-relaxed);
}

.paragraph-empty {
  height: var(--lme-gap-xs);
}

/* ── 关于 ── */
.about-body {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.about-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
}

.about-label {
  width: 64px;
  flex-shrink: 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.about-value {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
}

.about-note {
  margin: var(--lme-gap-xs) 0 0;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  line-height: var(--lme-line-height-relaxed);
}
</style>
