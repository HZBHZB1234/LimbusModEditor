<script setup lang="ts">
/**
 * WikiEntityPage — 通用实体页面
 * 使用 WikiShell 布局外壳，渲染信息框、目录、分节、画廊、相关页面与标签
 */

import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import type {
  WikiPage,
  Infobox,
  WikiSection,
  TocItem,
  GalleryImage,
  WikiRelatedPage,
  WikiPageCategory,
  BreadcrumbItem,
} from '@/ipc/types'
import { WikiPageCategoryLabels } from '@/ipc/types'

// ═══════════════ 路由 ═══════════════

const route = useRoute()
const router = useRouter()

// ═══════════════ 页面数据 ═══════════════

const page = ref<WikiPage | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

async function loadPage() {
  const pageId = route.params.id as string
  if (!pageId) {
    error.value = '无效的页面标识'
    return
  }
  loading.value = true
  error.value = null
  try {
    const result = await ipc.request<WikiPage>('wiki.getPage', { pageId })
    // 后端历史上曾返回 { page: WikiPageDto } 信封，这里兼容两种形态
    const unwrapped = (result as { page?: WikiPage } | null)?.page ?? result
    if (!unwrapped || !unwrapped.id) {
      page.value = null
      error.value = '页面不存在'
      return
    }
    page.value = unwrapped
  } catch (e) {
    error.value = '加载页面失败，请检查网络连接或稍后重试'
    page.value = null
  } finally {
    loading.value = false
  }
}

onMounted(loadPage)
watch(() => route.params.id, loadPage)

// ═══════════════ 计算属性 ═══════════════

const infobox = computed<Infobox | undefined>(() => page.value?.infobox)
const sections = computed<WikiSection[]>(() => page.value?.sections ?? [])

/** 自动从分节生成目录 */
const toc = computed<TocItem[]>(() => {
  if (page.value?.toc?.length) return page.value.toc
  return sections.value.map((s) => ({
    id: s.id,
    title: s.title,
    level: 1,
  }))
})

const gallery = computed<GalleryImage[]>(() => page.value?.gallery ?? [])
const relatedPages = computed<WikiRelatedPage[]>(() => page.value?.relatedPages ?? [])
const tags = computed<string[]>(() => page.value?.tags ?? [])

const categoryLabel = computed(() => {
  if (!page.value?.category) return ''
  return WikiPageCategoryLabels[page.value.category] ?? page.value.category
})

/** 面包屑 */
const breadcrumbs = computed<BreadcrumbItem[]>(() => {
  const crumbs: BreadcrumbItem[] = [
    { label: '维基首页', route: '/wiki' },
  ]
  if (page.value) {
    crumbs.push({
      label: categoryLabel.value,
      route: `/wiki/category/${page.value.category}`,
    })
    crumbs.push({
      label: page.value.title,
      route: `/wiki/page/${page.value.id}`,
    })
  }
  return crumbs
})

// ═══════════════ 分节折叠 ═══════════════

const collapsedSections = ref<Set<string>>(new Set())

function toggleSection(sectionId: string) {
  if (collapsedSections.value.has(sectionId)) {
    collapsedSections.value.delete(sectionId)
  } else {
    collapsedSections.value.add(sectionId)
  }
  // 触发响应式更新
  collapsedSections.value = new Set(collapsedSections.value)
}

function isSectionCollapsed(section: WikiSection): boolean {
  if (!section.collapsible) return false
  return collapsedSections.value.has(section.id)
}

function scrollToSection(id: string) {
  const el = document.getElementById(id)
  if (el) {
    el.scrollIntoView({ behavior: 'smooth' })
    // 确保目标分节处于展开状态
    collapsedSections.value.delete(id)
    collapsedSections.value = new Set(collapsedSections.value)
  }
}

// ═══════════════ 编辑模式 ═══════════════

const editingSection = ref<string | null>(null)
const editBuffer = ref('')

function startEdit(section: WikiSection) {
  editingSection.value = section.id
  editBuffer.value = section.content
}

function cancelEdit() {
  editingSection.value = null
  editBuffer.value = ''
}

function saveEdit(section: WikiSection) {
  if (!page.value) return
  // 通过 IPC 提交内容编辑
  ipc.request('wiki.saveContent', {
    pageId: page.value.id,
    sectionId: section.id,
    content: editBuffer.value,
  }).then(() => {
    // 乐观更新本地数据
    section.content = editBuffer.value
    editingSection.value = null
    editBuffer.value = ''
  }).catch(() => {
    // 失败时保持编辑状态
    error.value = '保存失败，请重试'
  })
}

// ═══════════════ 导航 ═══════════════

function handleNavigate(targetRoute: string) {
  router.push(targetRoute)
}

function handleSearch(query: string) {
  router.push({ path: '/wiki/search', query: { q: query } })
}

function navigateToRelated(related: WikiRelatedPage) {
  router.push(related.url)
}

// ═══════════════ 信息框字段渲染 ═══════════════

function formatFieldValue(field: { value: string; type: string }): string {
  if (field.type === 'boolean') {
    return field.value === 'true' ? '是' : '否'
  }
  return field.value
}
</script>

<template>
  <WikiShell
    :current-page="page?.id ?? ''"
    :breadcrumbs="breadcrumbs"
    @navigate="handleNavigate"
    @search="handleSearch"
  >
    <div class="wiki-entity-page">
      <!-- 加载状态 -->
      <div v-if="loading" class="page-state">
        <div class="state-spinner">⏳</div>
        <span>加载页面内容…</span>
      </div>

      <!-- 错误状态 -->
      <div v-else-if="error" class="page-state page-error">
        <div class="state-icon">⚠️</div>
        <span>{{ error }}</span>
        <button class="retry-btn" @click="loadPage">重试</button>
      </div>

      <!-- 页面内容 -->
      <template v-else-if="page">
        <!-- 标题区 -->
        <header class="page-header">
          <div class="header-left">
            <h1 class="page-title">{{ page.title }}</h1>
            <p v-if="page.subtitle" class="page-subtitle">{{ page.subtitle }}</p>
            <div class="page-meta">
              <span v-if="categoryLabel" class="meta-category" :class="`cat-${page.category}`">
                {{ categoryLabel }}
              </span>
              <span v-if="page.lastModified" class="meta-date">
                最后修改：{{ page.lastModified }}
              </span>
            </div>
          </div>
        </header>

        <div class="page-body">
          <!-- 左侧：信息框 + TOC -->
          <aside class="page-sidebar">
            <!-- 信息框 -->
            <div v-if="infobox" class="infobox">
              <div v-if="infobox.imageUrl" class="infobox-image">
                <img :src="infobox.imageUrl" :alt="infobox.title" loading="lazy" />
              </div>
              <div v-if="infobox.title" class="infobox-caption">{{ infobox.title }}</div>
              <table class="infobox-table">
                <tbody>
                  <tr
                    v-for="field in infobox.fields"
                    :key="field.label"
                    class="infobox-row"
                  >
                    <th class="infobox-label">{{ field.label }}</th>
                    <td class="infobox-value">
                      <span v-if="field.type === 'tags'" class="infobox-tags">
                        <span v-for="tag in field.value.split(',')" :key="tag.trim()" class="infobox-tag">
                          {{ tag.trim() }}
                        </span>
                      </span>
                      <span v-else>{{ formatFieldValue(field) }}</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>

            <!-- 目录 -->
            <nav v-if="toc.length > 0" class="toc">
              <div class="toc-title">目录</div>
              <ul class="toc-list">
                <li
                  v-for="item in toc"
                  :key="item.id"
                  class="toc-item"
                  :style="{ paddingLeft: (item.level - 1) * 14 + 'px' }"
                >
                  <a @click="scrollToSection(item.id)">{{ item.title }}</a>
                </li>
              </ul>
            </nav>

            <!-- 相关页面 -->
            <div v-if="relatedPages.length > 0" class="related-pages">
              <div class="related-title">相关页面</div>
              <ul class="related-list">
                <li
                  v-for="related in relatedPages"
                  :key="related.id"
                  class="related-item"
                  @click="navigateToRelated(related)"
                >
                  <span class="related-item-title">{{ related.title }}</span>
                  <span class="related-item-cat">{{ WikiPageCategoryLabels[related.category] }}</span>
                </li>
              </ul>
            </div>
          </aside>

          <!-- 右侧：分节内容 -->
          <main class="page-main">
            <!-- 分节 -->
            <section
              v-for="section in sections"
              :key="section.id"
              :id="section.id"
              class="wiki-section"
              :class="{ collapsed: isSectionCollapsed(section) }"
            >
              <h2 class="section-title">
                <span class="section-title-text">{{ section.title }}</span>
                <span class="section-actions">
                  <button
                    v-if="section.collapsible"
                    class="action-btn"
                    :title="isSectionCollapsed(section) ? '展开' : '折叠'"
                    @click="toggleSection(section.id)"
                  >
                    {{ isSectionCollapsed(section) ? '展开' : '折叠' }}
                  </button>
                  <button
                    v-if="section.editable"
                    class="action-btn"
                    title="编辑"
                    @click="startEdit(section)"
                  >
                    编辑
                  </button>
                </span>
              </h2>

              <!-- 阅读模式 -->
              <div
                v-if="editingSection !== section.id"
                class="section-content"
                v-html="section.content"
              />

              <!-- 编辑模式 -->
              <div v-else class="section-editor">
                <textarea
                  v-model="editBuffer"
                  class="editor-textarea"
                  rows="12"
                />
                <div class="editor-actions">
                  <button class="editor-save" @click="saveEdit(section)">保存</button>
                  <button class="editor-cancel" @click="cancelEdit">取消</button>
                </div>
              </div>
            </section>

            <!-- 画廊 -->
            <section v-if="gallery.length > 0" class="wiki-section">
              <h2 class="section-title">
                <span class="section-title-text">画廊</span>
              </h2>
              <div class="gallery-grid">
                <figure
                  v-for="(img, i) in gallery"
                  :key="i"
                  class="gallery-item"
                >
                  <img :src="img.url" :alt="img.caption || ''" loading="lazy" />
                  <figcaption v-if="img.caption" class="gallery-caption">
                    {{ img.caption }}
                    <span v-if="img.credit" class="gallery-credit">— {{ img.credit }}</span>
                  </figcaption>
                </figure>
              </div>
            </section>

            <!-- 分类标签 -->
            <div v-if="tags.length > 0" class="page-tags">
              <span class="tags-label">标签：</span>
              <span
                v-for="tag in tags"
                :key="tag"
                class="tag"
              >
                {{ tag }}
              </span>
            </div>
          </main>
        </div>
      </template>
    </div>
  </WikiShell>
</template>

<style scoped>
/* ═══════════════ 根容器 ═══════════════ */
.wiki-entity-page {
  padding: var(--lme-gap-lg);
  max-width: 1200px;
}

/* ═══════════════ 状态 ═══════════════ */
.page-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-xl) 0;
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-muted);
}

.page-error {
  color: var(--lme-error);
}

.state-spinner {
  font-size: 32px;
  animation: spin 2s linear infinite;
}

.state-icon {
  font-size: 32px;
}

.retry-btn {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--lme-accent);
  border: none;
  border-radius: var(--lme-radius-md);
  color: #fff;
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition: background 0.15s;
}

.retry-btn:hover {
  background: var(--lme-accent-hover);
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* ═══════════════ 标题区 ═══════════════ */
.page-header {
  margin-bottom: var(--lme-gap-lg);
  border-bottom: 1px solid var(--lme-border);
  padding-bottom: var(--lme-gap-md);
}

.page-title {
  font-size: var(--lme-font-size-xl);
  font-weight: 600;
  color: var(--lme-text-primary);
  margin: 0;
  line-height: 1.3;
}

.page-subtitle {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  margin-top: var(--lme-gap-xs);
  margin-bottom: 0;
}

.page-meta {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-md);
  margin-top: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.meta-category {
  padding: 2px 10px;
  border-radius: 10px;
  font-size: var(--lme-font-size-xs);
  font-weight: 500;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  color: var(--lme-text-secondary);
}

.meta-date {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ═══════════════ 主体布局 ═══════════════ */
.page-body {
  display: flex;
  gap: var(--lme-gap-xl);
  align-items: flex-start;
}

.page-sidebar {
  width: 280px;
  flex-shrink: 0;
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
}

.page-main {
  flex: 1;
  min-width: 0;
}

/* ═══════════════ 信息框 ═══════════════ */
.infobox {
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  overflow: hidden;
}

.infobox-image {
  width: 100%;
  aspect-ratio: 1;
  background: var(--lme-bg-input);
}

.infobox-image img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.infobox-caption {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-primary);
  text-align: center;
  border-bottom: 1px solid var(--lme-border);
}

.infobox-table {
  width: 100%;
  border-collapse: collapse;
}

.infobox-row {
  border-bottom: 1px solid var(--lme-border);
}

.infobox-row:last-child {
  border-bottom: none;
}

.infobox-label {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  text-align: left;
  width: 35%;
  vertical-align: top;
  font-weight: 500;
}

.infobox-value {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  vertical-align: top;
}

.infobox-tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lme-gap-xs);
}

.infobox-tag {
  padding: 1px 6px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: 8px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

/* ═══════════════ 目录 ═══════════════ */
.toc {
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  padding: var(--lme-gap-md);
}

.toc-title {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
  margin-bottom: var(--lme-gap-sm);
}

.toc-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.toc-item {
  padding: 2px 0;
}

.toc-item a {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-accent);
  cursor: pointer;
  text-decoration: none;
  transition: color 0.12s;
}

.toc-item a:hover {
  color: var(--lme-accent-hover);
  text-decoration: underline;
}

/* ═══════════════ 相关页面 ═══════════════ */
.related-pages {
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  padding: var(--lme-gap-md);
}

.related-title {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
  margin-bottom: var(--lme-gap-sm);
}

.related-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.related-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--lme-gap-sm);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  transition: background 0.12s;
}

.related-item:hover {
  background: var(--lme-bg-hover);
}

.related-item-title {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-accent);
}

.related-item-cat {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ═══════════════ 分节 ═══════════════ */
.wiki-section {
  margin-bottom: var(--lme-gap-lg);
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  overflow: hidden;
}

.section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 0;
  padding: var(--lme-gap-md) var(--lme-gap-lg);
  font-size: var(--lme-font-size-lg);
  font-weight: 600;
  color: var(--lme-text-primary);
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-elevated);
}

.section-title-text {
  flex: 1;
}

.section-actions {
  display: flex;
  gap: var(--lme-gap-xs);
}

.action-btn {
  padding: 2px 10px;
  background: var(--lme-bg-panel);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--lme-text-muted);
  cursor: pointer;
  font-size: var(--lme-font-size-xs);
  transition: all 0.15s;
}

.action-btn:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
  border-color: var(--lme-border-strong);
}

/* 折叠状态 */
.wiki-section.collapsed .section-content {
  display: none;
}

/* 分节内容 */
.section-content {
  padding: var(--lme-gap-lg);
  font-size: var(--lme-font-size-md);
  color: var(--lme-text-primary);
  line-height: 1.75;
}

.section-content :deep(h3) {
  font-size: var(--lme-font-size-lg);
  margin-top: var(--lme-gap-lg);
  margin-bottom: var(--lme-gap-sm);
}

.section-content :deep(p) {
  margin: 0 0 var(--lme-gap-md);
}

.section-content :deep(ul),
.section-content :deep(ol) {
  padding-left: var(--lme-gap-lg);
  margin-bottom: var(--lme-gap-md);
}

.section-content :deep(a) {
  color: var(--lme-accent);
  text-decoration: none;
}

.section-content :deep(a:hover) {
  text-decoration: underline;
}

.section-content :deep(table) {
  width: 100%;
  border-collapse: collapse;
  margin-bottom: var(--lme-gap-md);
}

.section-content :deep(th),
.section-content :deep(td) {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  border: 1px solid var(--lme-border);
  text-align: left;
}

.section-content :deep(th) {
  background: var(--lme-bg-elevated);
  font-weight: 600;
}

/* 编辑器 */
.section-editor {
  padding: var(--lme-gap-md);
}

.editor-textarea {
  width: 100%;
  min-height: 200px;
  padding: var(--lme-gap-md);
  background: var(--lme-bg-input);
  border: 1px solid var(--lme-accent);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-primary);
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  line-height: 1.6;
  resize: vertical;
  outline: none;
}

.editor-actions {
  display: flex;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-md);
}

.editor-save {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--lme-success);
  border: none;
  border-radius: var(--lme-radius-md);
  color: #fff;
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition: opacity 0.15s;
}

.editor-save:hover {
  opacity: 0.85;
}

.editor-cancel {
  padding: var(--lme-gap-sm) var(--lme-gap-lg);
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  color: var(--lme-text-secondary);
  font-size: var(--lme-font-size-sm);
  cursor: pointer;
  transition: all 0.15s;
}

.editor-cancel:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* ═══════════════ 画廊 ═══════════════ */
.gallery-grid {
  padding: var(--lme-gap-lg);
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: var(--lme-gap-md);
}

.gallery-item {
  margin: 0;
  border-radius: var(--lme-radius-md);
  overflow: hidden;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  transition: transform 0.15s, border-color 0.15s;
}

.gallery-item:hover {
  transform: translateY(-2px);
  border-color: var(--lme-accent);
}

.gallery-item img {
  width: 100%;
  aspect-ratio: 1;
  object-fit: cover;
  display: block;
}

.gallery-caption {
  padding: var(--lme-gap-sm) var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  text-align: center;
}

.gallery-credit {
  color: var(--lme-text-disabled);
}

/* ═══════════════ 标签 ═══════════════ */
.page-tags {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
  margin-top: var(--lme-gap-xl);
  padding-top: var(--lme-gap-lg);
  border-top: 1px solid var(--lme-border);
}

.tags-label {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  margin-right: var(--lme-gap-xs);
}

.tag {
  padding: 3px 10px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: 10px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
  transition: all 0.12s;
}

.tag:hover {
  border-color: var(--lme-accent);
  color: var(--lme-accent);
}
</style>
