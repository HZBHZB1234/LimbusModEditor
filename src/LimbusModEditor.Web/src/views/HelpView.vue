<script setup lang="ts">
// 帮助与教程页面
// 嵌入式中文教程内容 + 外部链接

import { ref } from 'vue'
import { NCard, NCollapse, NCollapseItem, NTag } from 'naive-ui'

// ── 教程章节 ──
interface HelpSection {
  id: string
  title: string
  icon: string
  content: string[]
}

const sections = ref<HelpSection[]>([
  {
    id: 'quickstart',
    title: '快速入门',
    icon: '🚀',
    content: [
      '欢迎使用 LimbusModEditor！本工具用于编辑 Limbus Company 游戏的模组资源。',
      '首次使用请前往「设置」页面配置游戏目录和模组输出目录。',
      '打开项目后，在「资源」页面浏览和搜索游戏资源。',
      '使用「导出」页面将修改后的资源打包为游戏可识别的模组格式。',
    ],
  },
  {
    id: 'workflow',
    title: '工作流程',
    icon: '🔄',
    content: [
      '1. 打开项目：从「项目」页面打开已有的 .lme 项目文件，或新建项目。',
      '2. 浏览资源：在「资源」页面使用搜索和筛选定位目标资源。',
      '3. 编辑资源：双击资源进入编辑模式，支持文本、JSON、图像等类型。',
      '4. 预览修改：编辑后可在预览面板实时查看效果。',
      '5. 导出模组：前往「导出」页面选择目标格式和分组，执行导出。',
      '6. 测试验证：将输出目录的模组文件放入游戏目录进行测试。',
    ],
  },
  {
    id: 'format-boundary',
    title: '格式边界',
    icon: '📐',
    content: [
      'Bank(.bank)：音频银行格式，用于打包游戏音频资源。',
      'Rebank(.rebank)：音频重打包格式，优化 Bank 文件的加载性能。',
      'Carra(.carra)：Carra 专用格式，用于特定资源类型。',
      'Lunartique(.zip)：Lunartique 压缩包格式，用于整体模组分发。',
      'LangBus(.json)：语言总线格式，管理多语言文本资源。',
      'LangPatch(.json)：语言补丁格式，用于覆盖游戏原有文本。',
      'LangPathset(.json)：语言路径集格式，定义文本资源的路径映射。',
      'StaticMod(.staticmod)：静态模组格式，用于静态数据修改。',
      '',
      '导出分组说明：',
      '_fmod：音频资源分组，包含所有音频相关文件。',
      '_data：通用资源分组，包含纹理、模型等资源。',
      '_text：语言文本分组，包含所有本地化文本。',
      '_static：静态数据分组，包含配置和静态表数据。',
    ],
  },
  {
    id: 'faq',
    title: '常见问题',
    icon: '❓',
    content: [
      'Q: 如何确认游戏目录配置正确？',
      'A: 游戏目录应包含 LimbusCompany.exe 和 Data 文件夹。自动检测功能可帮助定位。',
      '',
      'Q: 导出后模组未生效怎么办？',
      'A: 检查输出目录是否正确放入游戏目录的 Mods 文件夹，并确认模组格式与游戏版本兼容。',
      '',
      'Q: 支持哪些资源类型编辑？',
      'A: 当前支持文本、JSON、图像（纹理/精灵）、音频等类型的编辑。Unity 字段编辑正在开发中。',
      '',
      'Q: 如何批量替换资源？',
      'A: 在资源列表中使用筛选功能定位目标资源，然后使用批量操作功能进行替换。',
      '',
      'Q: 项目文件损坏了怎么办？',
      'A: 项目文件（.lme）本质上是 JSON 格式，可以尝试手动修复或从备份恢复。',
    ],
  },
])

// ── 外部链接 ──
interface ExternalLink {
  label: string
  url: string
  description: string
}

const externalLinks = ref<ExternalLink[]>([
  {
    label: '使用文档',
    url: 'docs/USAGE.md',
    description: '完整的 IPC 契约与使用说明',
  },
  {
    label: '格式规范',
    url: 'docs/FORMATS.md',
    description: '导出格式详细规范',
  },
  {
    label: '项目仓库',
    url: 'https://github.com/LimbusModEditor',
    description: '源代码与 Issue 追踪',
  },
])

function openLink(url: string) {
  // 暂未实现：打开外部链接
  window.open(url, '_blank')
}

/** 目录锚点跳转：保持原生 a[href="#id"] 语义 */
function scrollToSection(id: string) {
  const el = document.getElementById(id)
  if (el) el.scrollIntoView({ behavior: 'smooth' })
}
</script>

<template>
  <div class="help-view">
    <div class="help-container">
      <header class="help-header">
        <h2 class="help-title">帮助与教程</h2>
        <p class="help-subtitle">LimbusModEditor 使用指南</p>
      </header>

      <!-- 目录导航 -->
      <nav class="help-nav">
        <a
          v-for="section in sections"
          :key="section.id"
          :href="'#' + section.id"
          class="nav-link"
          @click.prevent="scrollToSection(section.id)"
        >
          {{ section.icon }} {{ section.title }}
        </a>
      </nav>

      <!-- 教程章节：NCollapse 折叠文档式排版（锚点 id 挂在卡片上，深链可用） -->
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
                <h3 class="section-title">
                  <span class="section-icon">{{ section.icon }}</span>
                  {{ section.title }}
                </h3>
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
      </div>

      <!-- 外部链接 -->
      <NCard class="help-section external-section" size="small" title="🔗 外部链接">
        <div class="external-list">
          <a
            v-for="link in externalLinks"
            :key="link.url"
            :href="link.url"
            class="external-link"
            @click.prevent="openLink(link.url)"
          >
            <span class="link-head">
              <span class="link-label">{{ link.label }}</span>
              <NTag class="link-tag" size="small" :bordered="false">{{ link.description }}</NTag>
            </span>
            <span class="link-url lme-mono">{{ link.url }}</span>
          </a>
        </div>
      </NCard>

      <!-- 页脚 -->
      <footer class="help-footer">
        <p>LimbusModEditor · 版本 0.1.0</p>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.help-view {
  height: 100%;
  overflow-y: auto;
  background: var(--lme-bg-base);
}

.help-container {
  max-width: 800px;
  margin: 0 auto;
  padding: var(--lme-gap-xl);
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xl);
}

/* ── 头部 ── */
.help-header {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
  padding-bottom: var(--lme-gap-lg);
  border-bottom: 1px solid var(--lme-border);
}

.help-title {
  margin: 0;
  font-size: var(--lme-font-size-xl);
  color: var(--lme-text-primary);
}

.help-subtitle {
  margin: 0;
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-muted);
}

/* ── 导航 ── */
.help-nav {
  display: flex;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
  padding: var(--lme-gap-md);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
}

.nav-link {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-secondary);
  text-decoration: none;
  font-size: var(--lme-font-size-sm);
  transition: all 0.15s;
}

.nav-link:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* ── 内容区 ── */
.help-content {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
}

/* 面板外观由 NCard 承担，此处只保留锚点滚动留白 */
.help-section {
  scroll-margin-top: var(--lme-gap-lg);
}

.section-title {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-primary);
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.section-icon {
  font-size: var(--lme-font-size-lg);
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
  line-height: 1.6;
}

.paragraph-empty {
  height: var(--lme-gap-md);
}

/* ── 外部链接 ── */
.external-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
}

.external-link {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  text-decoration: none;
  transition: all 0.15s;
}

.external-link:hover {
  border-color: var(--lme-accent);
  background: var(--lme-bg-hover);
}

.link-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-sm);
}

.link-label {
  font-size: var(--lme-font-size-md);
  color: var(--lme-accent);
  font-weight: 500;
}

.link-tag {
  flex-shrink: 0;
}

.link-url {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

/* ── 页脚 ── */
.help-footer {
  padding: var(--lme-gap-lg) 0;
  border-top: 1px solid var(--lme-border);
  text-align: center;
}

.help-footer p {
  margin: 0;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}
</style>
