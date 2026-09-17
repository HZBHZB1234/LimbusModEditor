<script setup lang="ts">
/**
 * WikiEntityPage — 通用实体页面（人格 / E.G.O / 饰品 / 敌人 / 异想体 / 播报员）
 *
 * 布局对照 docs/WIKI-PREVIEW-UPGRADE-SPEC.md §4.1 与 docs/img/wiki-personality-page.png：
 *   左栏 WikiToc（编号 + 吸顶） | 主区（引言 → 分节 Tab → 媒体） | 右栏 WikiInfoboxCard
 * 剧情类（category === 'story'）走 WikiStoryView 专用布局（见模板分派处说明）。
 */

import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import WikiToc from '@/components/wiki/WikiToc.vue'
import WikiInfoboxCard from '@/components/wiki/WikiInfoboxCard.vue'
import WikiQuoteBlock from '@/components/wiki/WikiQuoteBlock.vue'
import WikiNoticeBox from '@/components/wiki/WikiNoticeBox.vue'
import WikiChipList from '@/components/wiki/WikiChipList.vue'
import WikiTabGroup from '@/components/wiki/WikiTabGroup.vue'
import WikiAudioPlayer from '@/components/wiki/WikiAudioPlayer.vue'
import WikiGallery from '@/components/wiki/WikiGallery.vue'
import WikiSpineViewer from '@/components/wiki/WikiSpineViewer.vue'
import WikiSourceBadge from '@/components/wiki/WikiSourceBadge.vue'
import { resolveMediaUrl, resolveAudioUrl, resolveSpineAssets } from '@/components/wiki/mediaUrl'
import { type SpineAssets } from '@/components/wiki/mediaUrl'
import WikiStoryView from '@/views/WikiStoryView.vue'
import type {
  WikiPage,
  InfoboxRow,
  InfoboxField,
  WikiSection,
  WikiEntry,
  TocItem,
  GalleryImage,
  WikiRelatedPage,
  ResourceBinding,
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
  } catch {
    error.value = '加载页面失败，请检查网络连接或稍后重试'
    page.value = null
  } finally {
    loading.value = false
  }
}

onMounted(loadPage)
watch(() => route.params.id, loadPage)

// ═══════════════ 基础计算属性 ═══════════════

const infobox = computed<InfoboxRow[] | undefined>(() => page.value?.infobox)
/** 封面：后端解析不出真实地址时为 null —— 一律不显示占位图 */
const coverUrl = computed<string | null>(() => page.value?.cover ?? null)

/**
 * 信息框卡片：有信息框行或只要解析出了封面才渲染。
 * IPC 下发的信息框是 [{label,value}] 行数组（WikiInfoboxRowDto），卡片要的
 * 是字段行——就地适配成只读文本字段，不改后端契约（wiki-export-ipc/wiki-shot
 * 夹具管线都吃这个数组）。
 */
const infoboxCard = computed<{
  title: string
  subtitle?: string
  imageUrl?: string
  fields: InfoboxField[]
} | null>(() => {
  const rows = infobox.value
  const cover = coverUrl.value
  if ((!rows || rows.length === 0) && !cover) return null
  const title = page.value?.title ?? ''
  if (!title) return null
  return {
    title,
    subtitle: page.value?.subtitle || undefined,
    imageUrl: cover || undefined,
    fields: (rows ?? []).map((row) => ({
      label: row.label,
      value: row.value,
      type: 'text',
      editable: false,
    })),
  }
})
const gallery = computed<GalleryImage[]>(() => page.value?.gallery ?? [])
const relatedPages = computed<WikiRelatedPage[]>(() => page.value?.relatedPages ?? [])
const tags = computed<string[]>(() => page.value?.tags ?? [])

const categoryLabel = computed(() => {
  if (!page.value?.category) return ''
  return WikiPageCategoryLabels[page.value.category] ?? page.value.category
})

const isStory = computed(() => page.value?.category === 'story')

const breadcrumbs = computed<BreadcrumbItem[]>(() => {
  const crumbs: BreadcrumbItem[] = [{ label: '维基首页', route: '/wiki' }]
  if (page.value) {
    crumbs.push({
      label: categoryLabel.value,
      route: `/wiki/category/${page.value.category}`,
    })
    crumbs.push({ label: page.value.title, route: `/wiki/page/${page.value.id}` })
  }
  return crumbs
})

// ═══════════════ 分节：引言 + 语义分组 ═══════════════

/** 引言分节（维基页首引用块）：命中则作为页首引言展示，不再重复出现在分节流 */
const LEAD_KEYWORDS = ['概览', '概述', '简介', '介绍', '摘要']

const leadSection = computed<WikiSection | null>(() => {
  const list = page.value?.sections ?? []
  return list.find((s) => LEAD_KEYWORDS.some((k) => s.title.includes(k))) ?? null
})

/** 进入分节流（Tab / 平铺）的分节 = 全部分节 - 引言分节 */
const bodySections = computed<WikiSection[]>(() => {
  const list = page.value?.sections ?? []
  const leadId = leadSection.value?.id
  return leadId ? list.filter((s) => s.id !== leadId) : list
})

interface SectionGroup {
  key: string
  label: string
  sections: WikiSection[]
}

/**
 * 分组规则：仅按**分节标题关键词**推导，不硬编码与页面无关的分组。
 * 推导不出（全部落入 other）时走原顺序平铺，不建 Tab。
 */
const GROUP_RULES: Array<{ key: string; label: string; keywords: string[] }> = [
  { key: 'overview', label: '概览', keywords: ['概览', '概述', '简介', '介绍', '摘要', '基本信息'] },
  { key: 'data', label: '数据', keywords: ['数据', '属性', '数值', '状态', '面板', '能力'] },
  { key: 'skill', label: '技能与被动', keywords: ['技能', '被动', '战斗', '硬币', '抗性'] },
  { key: 'voice', label: '语音', keywords: ['语音', '声音', '台词', '配音', 'voice', 'audio'] },
  { key: 'story', label: '剧情', keywords: ['剧情', '故事', '章节', '对白'] },
  { key: 'art', label: '立绘与图集', keywords: ['立绘', '图集', '画廊', '插画', '形象', 'cg'] },
]

function groupKeyFor(title: string): string {
  const lower = title.toLowerCase()
  const hit = GROUP_RULES.find((r) => r.keywords.some((k) => lower.includes(k.toLowerCase())))
  return hit ? hit.key : 'other'
}

const sectionGroups = computed<SectionGroup[]>(() => {
  const buckets = new Map<string, WikiSection[]>()
  for (const s of bodySections.value) {
    const key = groupKeyFor(s.title)
    const list = buckets.get(key) ?? []
    list.push(s)
    buckets.set(key, list)
  }
  const groups: SectionGroup[] = GROUP_RULES.filter((r) => buckets.has(r.key)).map((r) => ({
    key: r.key,
    label: r.label,
    sections: buckets.get(r.key) ?? [],
  }))
  const others = buckets.get('other') ?? []
  if (others.length > 0) groups.push({ key: 'other', label: '其它资源', sections: others })
  return groups
})

/** 只有推导出了语义分组才用 Tab；否则平铺 */
const useTabs = computed(() => sectionGroups.value.some((g) => g.key !== 'other'))

const tabs = computed(() =>
  sectionGroups.value.map((g) => ({ key: g.key, label: g.label, badge: g.sections.length })),
)

const activeTab = ref('')

watch(
  sectionGroups,
  (groups) => {
    if (!groups.some((g) => g.key === activeTab.value)) {
      activeTab.value = groups[0]?.key ?? ''
    }
  },
  { immediate: true },
)

// ═══════════════ 目录 ═══════════════

const toc = computed<TocItem[]>(() => {
  if (page.value?.toc?.length) return page.value.toc
  return (page.value?.sections ?? []).map((s) => ({ id: s.id, title: s.title, level: 1 }))
})

// ═══════════════ 分节条目（后端 section.entries[]）═══════════════

/** 标题与正文皆空则不渲染（不占位、不编造） */
function entriesOf(section: WikiSection): WikiEntry[] {
  return (section.entries ?? []).filter(
    (e) => (e.title ?? '').trim() !== '' || (e.body ?? '').trim() !== '',
  )
}

/** 条目来源标注：authority / confidence / sourceDetail 全缺则不渲染 */
function entrySourceOf(entry: WikiEntry) {
  const has =
    (entry.authority?.trim() ?? '') !== '' ||
    entry.confidence !== undefined ||
    (entry.sourceDetail?.trim() ?? '') !== ''
  return has
    ? {
        authority: entry.authority,
        confidence: entry.confidence,
        detail: entry.sourceDetail,
      }
    : null
}

// ═══════════════ 媒体派发 ═══════════════

function bindingsOf(section: WikiSection): ResourceBinding[] {
  return section.bindings ?? []
}

function audioOf(section: WikiSection): ResourceBinding[] {
  return bindingsOf(section).filter((b) => b.kind === 'Audio')
}

/** Image 绑定 → GalleryImage；拿不到地址的直接丢弃（不渲染空图） */
function galleryOf(section: WikiSection): GalleryImage[] {
  return bindingsOf(section)
    .filter((b) => b.kind === 'Image')
    .flatMap<GalleryImage>((b) => {
      const url = resolveMediaUrl(b)
      return url ? [{ url, caption: b.display, editTo: assetEditUrl(b.refKey) }] : []
    })
}

/**
 * 资源绑定的「去编辑」目标：资源工作台按容器路径定位。
 * refKey 为空时不给链接（不猜跳转目标）——按钮也不渲染。
 */
function assetEditUrl(refKey: string | undefined | null): string | undefined {
  if (!refKey || refKey.trim() === '') return undefined
  return `/assets?container=${encodeURIComponent(refKey)}`
}

function goEditAsset(target: string) {
  router.push(target)
}

// ═══════════ 文本 / 静态两条「去编辑」深链 ═══════════

/** 深链目标：path + query（拿不到依据就返回 undefined，不猜、不渲染链接）。 */
type EditTarget = { path: string; query: Record<string, string> }

/**
 * 文本工作台深链：Text 绑定且 refKey 是 .json → 当成语言文件相对路径。
 * 依据：这类条目后端标的 sourceDetail 是「Lang 文件名约定 …」，refKey 形如
 * `EGOVoiceDig/Voice_EGO_YiSang_1.json`，与文本库 files.rel_path 同口径。
 */
function textEditTargetOf(binding: ResourceBinding): EditTarget | undefined {
  if (binding.kind !== 'Text') return undefined
  const file = binding.refKey.trim()
  if (!file.toLowerCase().endsWith('.json')) return undefined
  return { path: '/text', query: { file } }
}

/**
 * 静态数据工作台深链：StaticData 绑定且容器路径在 static-data 下 → 表名取文件名（去 .json）。
 * 依据：refKey 形如 `…/static-data/personality/personality-01.json`，
 * 与静态库 tables.name 一致（personality-01 / skin-data 实测可匹配）。
 */
function staticEditTargetOf(binding: ResourceBinding): EditTarget | undefined {
  if (binding.kind !== 'StaticData') return undefined
  const path = binding.refKey.replace(/\\/g, '/')
  if (!path.toLowerCase().includes('/static-data/')) return undefined
  const name = path.split('/').pop() ?? ''
  const table = name.toLowerCase().endsWith('.json') ? name.slice(0, -5) : ''
  return table ? { path: '/static', query: { table } } : undefined
}

/** 绑定的「去编辑」目标：文本 → 静态 → 资源工作台（都没有就不渲染链接）。 */
function editTargetOf(binding: ResourceBinding): EditTarget | undefined {
  return (
    textEditTargetOf(binding) ??
    staticEditTargetOf(binding) ??
    (binding.refKey.trim() ? { path: '/assets', query: { container: binding.refKey } } : undefined)
  )
}

/** 按钮悬浮提示：说清楚这条链接到底去哪个工作台。 */
function editTargetTitle(binding: ResourceBinding): string {
  if (textEditTargetOf(binding)) return '到文本工作台按文件定位：' + binding.refKey
  if (staticEditTargetOf(binding)) return '到静态数据工作台按表名定位：' + (staticEditTargetOf(binding)!.query.table)
  return '到资源工作台定位这条容器路径'
}

/** 其余绑定（StaticData / Text / Prefab / Video / Mesh / Animation …）走纯列表 */
function otherBindingsOf(section: WikiSection): ResourceBinding[] {
  return bindingsOf(section).filter(
    (b) => b.kind !== 'Audio' && b.kind !== 'Image' && b.kind !== 'Spine',
  )
}

/**
 * Spine：骨架 / 图集 / 纹理三个地址由后端下发，缺任一整块不渲染。
 * 实测 134 条 Spine 绑定的 ref_key 全是 .prefab —— 本地没有对应的三件套，
 * 后端因此全给 null，块不会渲染；这是数据缺口，不是渲染逻辑问题。
 */
const spineMap = computed<Record<string, SpineAssets>>(() => {
  const map: Record<string, SpineAssets> = {}
  for (const section of bodySections.value) {
    const spine = bindingsOf(section).find((b) => b.kind === 'Spine')
    if (!spine) continue
    const assets = resolveSpineAssets(spine)
    if (!assets) continue
    map[section.id] = assets
  }
  return map
})

// ═══════════════ 来源标注 ═══════════════

interface SectionSource {
  authority?: string
  confidence?: number | string
  writableSource?: string
  detail?: string
}

/**
 * 后端补齐 authority / confidence / writableSource 后自动显示；当前缺失则不渲染。
 * 与 mediaUrl 同一套路：字段在 DTO 里但前端 TS 尚未声明，读取后按「有才显示」处理。
 */
const sourceMap = computed<Record<string, SectionSource>>(() => {
  const map: Record<string, SectionSource> = {}
  for (const section of page.value?.sections ?? []) {
    const raw = section as unknown as SectionSource
    const has =
      (raw.authority?.trim() ?? '') !== '' ||
      raw.confidence !== undefined ||
      (raw.writableSource?.trim() ?? '') !== ''
    if (has) map[section.id] = raw
  }
  return map
})

// ═══════════════ 分节折叠 ═══════════════

const collapsedSections = ref<Set<string>>(new Set())

function toggleSection(sectionId: string) {
  const next = new Set(collapsedSections.value)
  if (next.has(sectionId)) next.delete(sectionId)
  else next.add(sectionId)
  collapsedSections.value = next
}

function isSectionCollapsed(section: WikiSection): boolean {
  if (!section.collapsible) return false
  return collapsedSections.value.has(section.id)
}

// ═══════════════ 目录跳转 ═══════════════

function scrollToSection(id: string) {
  // 目标分节可能在未激活的 Tab 里，先切到它所属的分组
  const group = sectionGroups.value.find((g) => g.sections.some((s) => s.id === id))
  if (group) activeTab.value = group.key
  const next = new Set(collapsedSections.value)
  next.delete(id)
  collapsedSections.value = next
  requestAnimationFrame(() => {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth' })
  })
}

function scrollToTop() {
  document.querySelector('.wiki-slot')?.scrollTo({ top: 0, behavior: 'smooth' })
}

// ═══════════════ 内容编辑（只改内容、不改结构）═══════════════

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
  ipc
    .request('wiki.saveContent', {
      pageId: page.value.id,
      sectionId: section.id,
      content: editBuffer.value,
    })
    .then(() => {
      section.content = editBuffer.value
      editingSection.value = null
      editBuffer.value = ''
    })
    .catch(() => {
      error.value = '保存失败，请重试'
    })
}

// ═══════════════ 提示块（只提示能从数据推出的事实）═══════════════

const noticeText = computed(() => {
  if (!page.value) return ''
  const list = page.value.sections ?? []
  if (list.length === 0) return '该页面当前没有可展示的分节内容。'
  // 有正文的判据：content 非空，或分节内有条目且至少一条条目带非空 body。
  // 只看 content 会漏掉"正文都在 entries 里"的页面（后端现在就是这么下发的）。
  const hasBody = (s: WikiSection) =>
    (s.content ?? '').trim() !== '' ||
    (s.entries ?? []).some((e) => (e.body ?? '').trim() !== '')
  if (!list.some(hasBody)) return '该页面的分节正文目前均为空。'
  return ''
})

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
  if (field.type === 'boolean') return field.value === 'true' ? '是' : '否'
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

      <!--
        剧情类走专用视图（WikiStoryView）。
        选择「同组件内条件渲染」而非新增路由分支的理由：
        两者共用 /wiki/page/:id 同一路由、同一次 wiki.getPage 取数与同一套
        加载/错误/面包屑逻辑；拆到路由层会引入二次取数与两个加载态，收益不成立。
      -->
      <WikiStoryView v-else-if="page && isStory" :page="page" />

      <!-- 实体页内容 -->
      <template v-else-if="page">
        <!-- 标题区 -->
        <header class="page-header">
          <h1 class="page-title">{{ page.title }}</h1>
          <p v-if="page.subtitle" class="page-subtitle">{{ page.subtitle }}</p>
          <div class="page-meta">
            <span v-if="categoryLabel" class="meta-category">{{ categoryLabel }}</span>
            <WikiChipList v-if="tags.length > 0" :items="tags" class="page-meta-tags" />
            <span v-if="page.lastModified" class="meta-date">
              最后修改：{{ page.lastModified }}
            </span>
          </div>
        </header>

        <WikiNoticeBox v-if="noticeText" :text="noticeText" icon="ℹ️" />

        <div class="page-body">
          <!-- 左栏：编号目录（吸顶 + 回到顶部） -->
          <WikiToc
            v-if="toc.length > 0"
            class="page-toc"
            :items="toc"
            show-back-to-top
            @select="scrollToSection"
            @back-top="scrollToTop"
          />

          <!-- 主区 -->
          <main class="page-main">
            <!-- 页首引言（取自引言分节，不重复进入分节流） -->
            <WikiQuoteBlock
              v-if="leadSection"
              :text="leadSection.content"
              :attribution="leadSection.title"
            />

            <!-- 分节：可推导语义则用 Tab 分组，否则按原顺序平铺 -->
            <WikiTabGroup v-if="useTabs" v-model:active="activeTab" :tabs="tabs">
              <template v-for="group in sectionGroups" :key="group.key" #[group.key]>
                <section
                  v-for="section in group.sections"
                  :key="section.id"
                  :id="section.id"
                  class="wiki-section"
                  :class="{ collapsed: isSectionCollapsed(section) }"
                >
                  <h2 class="section-title">
                    <span class="section-title-text">{{ section.title }}</span>
                    <span class="section-actions">
                    <WikiSourceBadge
                      v-if="sourceMap[section.id]"
                      v-bind="sourceMap[section.id]"
                    />
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

                  <!-- 阅读模式：纯文本 + 换行（不解析 HTML，杜绝 XSS） -->
                  <div v-if="editingSection !== section.id" class="section-content">
                    {{ section.content }}
                  </div>

                  <!-- 编辑模式 -->
                  <div v-else class="section-editor">
                    <textarea v-model="editBuffer" class="editor-textarea" rows="12" />
                    <div class="editor-actions">
                      <button class="editor-save" @click="saveEdit(section)">保存</button>
                      <button class="editor-cancel" @click="cancelEdit">取消</button>
                    </div>
                  </div>

                  <!-- 分节条目（后端 section.entries[]）：标题 + 正文 + 来源标注 -->
                  <div v-if="entriesOf(section).length > 0" class="entry-list">
                    <div v-for="entry in entriesOf(section)" :key="entry.id" class="entry-item">
                      <div class="entry-head">
                        <h3 v-if="entry.title?.trim()" class="entry-title">{{ entry.title }}</h3>
                        <WikiSourceBadge v-if="entrySourceOf(entry)" v-bind="entrySourceOf(entry)" />
                      </div>
                      <div v-if="entry.body?.trim()" class="entry-body">{{ entry.body }}</div>
                    </div>
                  </div>

                  <!-- 媒体派发：按 binding.kind 分发，无绑定则不渲染 -->
                  <WikiAudioPlayer
                    v-if="audioOf(section).length > 0"
                    :items="audioOf(section)"
                    :url-for="resolveAudioUrl"
                    title="语音"
                  />
                  <WikiGallery
                    v-if="galleryOf(section).length > 0"
                    :images="galleryOf(section)"
                    title="图集"
                    @edit="goEditAsset"
                  />
                  <WikiSpineViewer
                    v-if="spineMap[section.id]"
                    v-bind="spineMap[section.id]"
                    title="动画"
                  />
                  <div v-if="otherBindingsOf(section).length > 0" class="binding-list">
                    <div class="binding-list-title">关联资源</div>
                    <ul>
                      <li v-for="b in otherBindingsOf(section)" :key="b.refKey" class="binding-item">
                        <span class="binding-kind">{{ b.kind }}</span>
                        <span class="binding-display">{{ b.display }}</span>
                        <RouterLink
                          v-if="editTargetOf(b)"
                          class="binding-edit-btn"
                          :title="editTargetTitle(b)"
                          :to="editTargetOf(b)!"
                        >
                          去编辑
                        </RouterLink>
                      </li>
                    </ul>
                  </div>
                </section>
              </template>
            </WikiTabGroup>

            <template v-else>
              <section
                v-for="section in bodySections"
                :key="section.id"
                :id="section.id"
                class="wiki-section"
                :class="{ collapsed: isSectionCollapsed(section) }"
              >
                <h2 class="section-title">
                  <span class="section-title-text">{{ section.title }}</span>
                  <span class="section-actions">
                    <WikiSourceBadge
                      v-if="sourceMap[section.id]"
                      v-bind="sourceMap[section.id]"
                    />
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

                <div v-if="editingSection !== section.id" class="section-content">
                  {{ section.content }}
                </div>

                <div v-else class="section-editor">
                  <textarea v-model="editBuffer" class="editor-textarea" rows="12" />
                  <div class="editor-actions">
                    <button class="editor-save" @click="saveEdit(section)">保存</button>
                    <button class="editor-cancel" @click="cancelEdit">取消</button>
                  </div>
                </div>

                <!-- 分节条目（后端 section.entries[]）：标题 + 正文 + 来源标注 -->
                <div v-if="entriesOf(section).length > 0" class="entry-list">
                  <div v-for="entry in entriesOf(section)" :key="entry.id" class="entry-item">
                    <div class="entry-head">
                      <h3 v-if="entry.title?.trim()" class="entry-title">{{ entry.title }}</h3>
                      <WikiSourceBadge v-if="entrySourceOf(entry)" v-bind="entrySourceOf(entry)" />
                    </div>
                    <div v-if="entry.body?.trim()" class="entry-body">{{ entry.body }}</div>
                  </div>
                </div>

                <WikiAudioPlayer
                  v-if="audioOf(section).length > 0"
                  :items="audioOf(section)"
                  :url-for="resolveAudioUrl"
                  title="语音"
                />
                <WikiGallery
                  v-if="galleryOf(section).length > 0"
                  :images="galleryOf(section)"
                  title="图集"
                  @edit="goEditAsset"
                />
                <WikiSpineViewer
                  v-if="spineMap[section.id]"
                  v-bind="spineMap[section.id]"
                  title="动画"
                />
                <div v-if="otherBindingsOf(section).length > 0" class="binding-list">
                  <div class="binding-list-title">关联资源</div>
                  <ul>
                    <li v-for="b in otherBindingsOf(section)" :key="b.refKey" class="binding-item">
                      <span class="binding-kind">{{ b.kind }}</span>
                      <span class="binding-display">{{ b.display }}</span>
                      <RouterLink
                        v-if="editTargetOf(b)"
                        class="binding-edit-btn"
                        :title="editTargetTitle(b)"
                        :to="editTargetOf(b)!"
                      >
                        去编辑
                      </RouterLink>
                    </li>
                  </ul>
                </div>
              </section>
            </template>

            <!-- 页面级画廊（page.gallery，地址由后端下发） -->
            <WikiGallery v-if="gallery.length > 0" :images="gallery" title="画廊" />
          </main>

          <!-- 右栏：信息框 -->
          <aside class="page-aside">
            <WikiInfoboxCard
              v-if="infoboxCard"
              :title="infoboxCard.title"
              :subtitle="infoboxCard.subtitle"
              :image-url="infoboxCard.imageUrl"
              :fields="infoboxCard.fields"
              :tags="tags"
            />

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
                  <span class="related-item-cat">
                    {{ WikiPageCategoryLabels[related.category] ?? related.category }}
                  </span>
                </li>
              </ul>
            </div>
          </aside>
        </div>
      </template>
    </div>
  </WikiShell>
</template>

<style scoped>
/* ═══════════════ 根容器 ═══════════════ */
.wiki-entity-page {
  padding: var(--lme-gap-lg) var(--lme-gap-xl);
  max-width: 1440px;
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
  color: var(--wiki-infobox-header-text);
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
}

.page-title {
  margin: 0;
  font-size: var(--wiki-title-size);
  line-height: var(--wiki-title-line);
  font-weight: 700;
  color: var(--wiki-title);
}

.page-subtitle {
  margin: var(--lme-gap-xs) 0 0;
  font-size: var(--lme-font-size-lg);
  color: var(--lme-text-secondary);
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
  border-radius: var(--wiki-chip-radius);
  font-size: var(--lme-font-size-xs);
  font-weight: 600;
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  color: var(--wiki-chip-text);
}

.page-meta-tags {
  display: inline-flex;
}

.meta-date {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ═══════════════ 主体栅格：目录 | 主区 | 信息框（主区:信息框 ≈ 7:3） ═══════════════ */
.page-body {
  display: grid;
  grid-template-columns: auto minmax(0, 7fr) minmax(0, 3fr);
  gap: var(--lme-gap-xl);
  align-items: start;
}

.page-toc {
  grid-column: 1;
}

.page-main {
  grid-column: 2;
  min-width: 0;
}

.page-aside {
  grid-column: 3;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
}

/* 窄屏：右栏下沉到主区下方，目录移到主区上方 */
@media (max-width: 1024px) {
  .page-body {
    grid-template-columns: minmax(0, 1fr);
  }
  .page-toc,
  .page-main,
  .page-aside {
    grid-column: 1;
  }
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
  gap: var(--lme-gap-md);
  margin: 0;
  padding: var(--lme-gap-md) var(--lme-gap-lg);
  font-size: var(--lme-font-size-lg);
  font-weight: 600;
  color: var(--wiki-section-title);
  border-bottom: 1px solid var(--lme-border);
  background: var(--lme-bg-elevated);
}

.section-title-text {
  flex: 1;
  min-width: 0;
}

.section-actions {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
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

.wiki-section.collapsed .section-content {
  display: none;
}

.section-content {
  padding: var(--lme-gap-lg);
  font-size: var(--lme-font-size-md);
  color: var(--wiki-body-text);
  line-height: 1.8;
  white-space: pre-wrap;
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
  color: var(--wiki-infobox-header-text);
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

/* ═══════════════ 分节条目 ═══════════════ */
.entry-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  padding: 0 var(--lme-gap-lg) var(--lme-gap-md);
}

.entry-item {
  padding-left: var(--lme-gap-md);
  border-left: 2px solid var(--lme-border);
}

.entry-head {
  display: flex;
  align-items: baseline;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
}

.entry-title {
  margin: 0;
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--wiki-entry-title);
}

.entry-body {
  margin-top: var(--lme-gap-xs);
  font-size: var(--lme-font-size-md);
  color: var(--wiki-body-text);
  line-height: 1.8;
  white-space: pre-wrap;
  word-break: break-word;
}

/* ═══════════════ 关联资源列表 ═══════════════ */
.binding-list {
  padding: 0 var(--lme-gap-lg) var(--lme-gap-lg);
}

.binding-list-title {
  font-size: var(--lme-font-size-sm);
  font-weight: 600;
  color: var(--lme-text-secondary);
  margin-bottom: var(--lme-gap-xs);
}

.binding-list ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-xs);
}

.binding-item {
  display: flex;
  gap: var(--lme-gap-sm);
  align-items: baseline;
  font-size: var(--lme-font-size-sm);
}

.binding-kind {
  flex-shrink: 0;
  min-width: 88px;
  color: var(--lme-text-muted);
}

.binding-display {
  color: var(--lme-text-primary);
  word-break: break-all;
}

.binding-edit-btn {
  flex-shrink: 0;
  text-decoration: none;
  padding: 1px 8px;
  background: var(--lme-bg-elevated);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  color: var(--wiki-link);
  font-size: var(--lme-font-size-xs);
  cursor: pointer;
}

.binding-edit-btn:hover {
  background: var(--lme-bg-hover);
  border-color: var(--lme-accent);
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
  color: var(--wiki-link);
}

.related-item-cat {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}
</style>
