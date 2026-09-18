<script setup lang="ts">
/**
 * WikiEntityPage — 通用实体页面（人格 / E.G.O / 饰品 / 敌人 / 异想体 / 播报员）
 *
 * 布局对照 docs/WIKI-PREVIEW-UPGRADE-SPEC.md §4.1 与 docs/img/wiki-personality-page.png：
 *   左栏 WikiToc（编号 + 吸顶 + 当前项高亮） | 主区（引言 → 分节 Tab → 媒体） | 右栏 WikiInfoboxCard
 * 剧情类（category === 'story'）走 WikiStoryView 专用布局（见模板分派处说明）。
 */

import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ipc } from '@/ipc'
import {
  NAlert,
  NButton,
  NCard,
  NEmpty,
  NInput,
  NSpin,
  NTabs,
  NTabPane,
  NTag,
  useMessage,
} from 'naive-ui'
import WikiShell from '@/components/WikiShell.vue'
import WikiToc from '@/components/wiki/WikiToc.vue'
import WikiInfoboxCard from '@/components/wiki/WikiInfoboxCard.vue'
import WikiQuoteBlock from '@/components/wiki/WikiQuoteBlock.vue'
import WikiNoticeBox from '@/components/wiki/WikiNoticeBox.vue'
import WikiChipList from '@/components/wiki/WikiChipList.vue'
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

/** 轻提示（外壳已在 NMessageProvider 内挂载本页） */
const message = useMessage()

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

/**
 * NTabs 的 value 类型是 string | number（库签名），这里收敛成 string 后再写回。
 * （不直接用 v-model:value，避免 number 落进 Ref<string>）
 */
function onTabChange(key: string | number) {
  activeTab.value = String(key)
}

/**
 * 库 NTabs 的 tab 不是可聚焦控件、也不带键盘导航；
 * 这里用 tabProps 补 role/tabindex 并在容器上补回 ← → Home End（原 WikiTabGroup 行为）。
 */
function onTabsKeydown(e: KeyboardEvent) {
  const keys = sectionGroups.value.map((g) => g.key)
  if (keys.length === 0) return
  const index = keys.indexOf(activeTab.value)
  let next = index
  if (e.key === 'ArrowLeft') next = (index - 1 + keys.length) % keys.length
  else if (e.key === 'ArrowRight') next = (index + 1) % keys.length
  else if (e.key === 'Home') next = 0
  else if (e.key === 'End') next = keys.length - 1
  else return
  e.preventDefault()
  activeTab.value = keys[next]
  requestAnimationFrame(() => {
    document.querySelector<HTMLElement>(`.wiki-tab[data-name="${keys[next]}"]`)?.focus()
  })
}

/** 分节 Tab 的可访问性属性（roving tabindex），库 tab 元素靠 tabProps 挂载 */
function tabPropsOf(key: string) {
  return {
    class: 'wiki-tab',
    role: 'tab',
    tabindex: key === activeTab.value ? 0 : -1,
    'aria-selected': key === activeTab.value,
  }
}

// ═══════════════ 目录 ═══════════════

const toc = computed<TocItem[]>(() => {
  if (page.value?.toc?.length) return page.value.toc
  return (page.value?.sections ?? []).map((s) => ({ id: s.id, title: s.title, level: 1 }))
})

// ═══════════════ 目录当前项联动（滚动 → TOC 高亮）═══════════════

/** 当前视口顶部最近的分节 id，传给 WikiToc 的 activeId 做高亮 */
const activeSectionId = ref('')

/** 不在当前 Tab 面板 / 已被 display:none 的分节不参与判定 */
function spyable(id: string): HTMLElement | null {
  const el = document.getElementById(id)
  if (!el || el.offsetParent === null) return null
  return el
}

function updateActiveSection() {
  if (!page.value || isStory.value) return
  const root = document.querySelector('.wiki-slot')
  if (!root) return
  const rootTop = root.getBoundingClientRect().top
  let current = ''
  for (const item of toc.value) {
    const el = spyable(item.id)
    if (!el) continue
    // 以「分节标题滚过视口顶部以下 120px」为界取最后一个越界者
    if (el.getBoundingClientRect().top - rootTop <= 120) current = item.id
    else break
  }
  activeSectionId.value = current
}

let spyEl: Element | null = null
let spyRaf = 0

function onSpyScroll() {
  cancelAnimationFrame(spyRaf)
  spyRaf = requestAnimationFrame(updateActiveSection)
}

onMounted(() => {
  spyEl = document.querySelector('.wiki-slot')
  spyEl?.addEventListener('scroll', onSpyScroll, { passive: true })
})

onUnmounted(() => {
  spyEl?.removeEventListener('scroll', onSpyScroll)
  cancelAnimationFrame(spyRaf)
})

// 页面数据或激活 Tab 变化后，可见分节集合会变，重算一次当前项
watch([page, activeTab], () => requestAnimationFrame(updateActiveSection))

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

/**
 * 音频工作台深链：Audio 绑定的 refKey 形如
 * `…\Voice_Battle_Announcer_S7.assets.bank\0announcer_911404_90001_1`
 * （实测：银行绝对路径 + NUL 分隔 + 样本名，与 bank.list 的 bankId、`bank.samples`
 * 的 name 同口径）。拆不出这两段就不给链接（不猜 bank / 样本名）。
 */
function audioEditTargetOf(binding: ResourceBinding): EditTarget | undefined {
  if (binding.kind !== 'Audio') return undefined
  const parts = binding.refKey.split('\u0000')
  if (parts.length !== 2) return undefined
  const bank = parts[0].trim()
  const sample = parts[1].trim()
  if (!bank || !sample) return undefined
  return { path: '/bank', query: { bank, sample } }
}

/** Spine 深链：走已打通的资源工作台容器定位（/assets?container=）。 */
function spineEditTargetOf(binding: ResourceBinding): EditTarget | undefined {
  if (binding.kind !== 'Spine') return undefined
  return binding.refKey.trim() ? { path: '/assets', query: { container: binding.refKey } } : undefined
}

/** 绑定的「去编辑」目标：音频 → Spine → 文本 → 静态 → 资源工作台（都没有就不渲染链接）。 */
function editTargetOf(binding: ResourceBinding): EditTarget | undefined {
  return (
    audioEditTargetOf(binding) ??
    spineEditTargetOf(binding) ??
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
      message.success('分节内容已保存')
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
      <!-- 顶部细进度条（与首页 / 资源工作台同一套加载语言） -->
      <div v-if="loading" class="loading-bar" aria-hidden="true" />

      <!-- 加载状态 -->
      <div v-if="loading" class="page-state state-block">
        <NSpin size="medium" />
        <span class="state-text">加载页面内容…</span>
      </div>

      <!-- 错误状态 -->
      <NAlert v-else-if="error" type="error" class="page-error">
        <template #header>{{ error }}</template>
        <NButton size="small" type="primary" @click="loadPage">重试</NButton>
      </NAlert>

      <!--
        剧情类走专用视图（WikiStoryView）。
        选择「同组件内条件渲染」而非新增路由分支的理由：
        两者共用 /wiki/page/:id 同一路由、同一次 wiki.getPage 取数与同一套
        加载/错误/面包屑逻辑；拆到路由层会引入二次取数与两个加载态，收益不成立。
      -->
      <WikiStoryView v-else-if="page && isStory" :page="page" />

      <!-- 实体页内容 -->
      <template v-else-if="page">
        <!-- 标题区：金色大标题 + 副题 + 元信息，下缘分隔线 -->
        <header class="page-header">
          <h1 class="page-title">{{ page.title }}</h1>
          <p v-if="page.subtitle" class="page-subtitle">{{ page.subtitle }}</p>
          <div class="page-meta">
            <!-- type="primary" 在维基工作区即金色（库主题主色取 --wiki-accent） -->
            <NTag v-if="categoryLabel" size="small" type="primary">{{ categoryLabel }}</NTag>
            <WikiChipList v-if="tags.length > 0" :items="tags" class="page-meta-tags" />
            <span v-if="page.lastModified" class="meta-date">
              最后修改：{{ page.lastModified }}
            </span>
          </div>
        </header>

        <WikiNoticeBox v-if="noticeText" :text="noticeText" icon="ℹ️" />

        <div class="page-body">
          <!-- 左栏：编号目录（吸顶 + 当前项高亮 + 回到顶部） -->
          <WikiToc
            v-if="toc.length > 0"
            class="page-toc"
            :items="toc"
            :active-id="activeSectionId"
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
            <NTabs
              v-if="useTabs"
              class="wiki-tabs"
              type="segment"
              size="small"
              :value="activeTab"
              @update:value="onTabChange"
              @keydown="onTabsKeydown"
            >
              <NTabPane
                v-for="group in sectionGroups"
                :key="group.key"
                :name="group.key"
                :tab-props="tabPropsOf(group.key)"
              >
                <template #tab>
                  <span class="wiki-tab-label">{{ group.label }}</span>
                  <span class="wiki-tab-badge">{{ group.sections.length }}</span>
                </template>
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
                      <NButton
                        v-if="section.collapsible"
                        size="tiny"
                        text
                        :title="isSectionCollapsed(section) ? '展开' : '折叠'"
                        @click="toggleSection(section.id)"
                      >
                        {{ isSectionCollapsed(section) ? '展开' : '折叠' }}
                      </NButton>
                      <NButton
                        v-if="section.editable"
                        size="tiny"
                        text
                        title="编辑"
                        @click="startEdit(section)"
                      >
                        编辑
                      </NButton>
                    </span>
                  </h2>

                  <!-- 阅读模式：纯文本 + 换行（不解析 HTML，杜绝 XSS） -->
                  <div v-if="editingSection !== section.id" class="section-content">
                    {{ section.content }}
                  </div>

                  <!-- 编辑模式 -->
                  <div v-else class="section-editor">
                    <NInput
                      v-model:value="editBuffer"
                      class="editor-textarea"
                      type="textarea"
                      :rows="12"
                    />
                    <div class="editor-actions">
                      <NButton size="small" type="primary" @click="saveEdit(section)">保存</NButton>
                      <NButton size="small" @click="cancelEdit">取消</NButton>
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
                        <!-- 深链目标与原来一致（/assets /text /static /bank），只是按钮走库组件 -->
                        <NButton
                          v-if="editTargetOf(b)"
                          size="tiny"
                          text
                          type="primary"
                          class="binding-edit-btn"
                          :title="editTargetTitle(b)"
                          @click="router.push(editTargetOf(b)!)"
                        >
                          去编辑
                        </NButton>
                      </li>
                    </ul>
                  </div>
                </section>
              </NTabPane>
            </NTabs>

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
                    <NButton
                      v-if="section.collapsible"
                      size="tiny"
                      text
                      :title="isSectionCollapsed(section) ? '展开' : '折叠'"
                      @click="toggleSection(section.id)"
                    >
                      {{ isSectionCollapsed(section) ? '展开' : '折叠' }}
                    </NButton>
                    <NButton
                      v-if="section.editable"
                      size="tiny"
                      text
                      title="编辑"
                      @click="startEdit(section)"
                    >
                      编辑
                    </NButton>
                  </span>
                </h2>

                <div v-if="editingSection !== section.id" class="section-content">
                  {{ section.content }}
                </div>

                <div v-else class="section-editor">
                  <NInput
                    v-model:value="editBuffer"
                    class="editor-textarea"
                    type="textarea"
                    :rows="12"
                  />
                  <div class="editor-actions">
                    <NButton size="small" type="primary" @click="saveEdit(section)">保存</NButton>
                    <NButton size="small" @click="cancelEdit">取消</NButton>
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
                      <!-- 深链目标与原来一致（/assets /text /static /bank），只是按钮走库组件 -->
                      <NButton
                        v-if="editTargetOf(b)"
                        size="tiny"
                        text
                        type="primary"
                        class="binding-edit-btn"
                        :title="editTargetTitle(b)"
                        @click="router.push(editTargetOf(b)!)"
                      >
                        去编辑
                      </NButton>
                    </li>
                  </ul>
                </div>
              </section>
            </template>

            <!-- 页面级画廊（page.gallery，地址由后端下发） -->
            <WikiGallery v-if="gallery.length > 0" :images="gallery" title="画廊" />
          </main>

          <!-- 右栏：信息框 + 相关页面 -->
          <aside class="page-aside">
            <!-- 信息框容器走库 NCard；黄底标题栏 / 字段行配色仍由 --wiki-* tokens 决定 -->
            <NCard
              v-if="infoboxCard"
              size="small"
              class="page-infobox-card"
              :content-style="{ padding: 0 }"
            >
              <WikiInfoboxCard
                :title="infoboxCard.title"
                :subtitle="infoboxCard.subtitle"
                :image-url="infoboxCard.imageUrl"
                :fields="infoboxCard.fields"
                :tags="tags"
              />
            </NCard>

            <NCard
              v-if="relatedPages.length > 0"
              size="small"
              title="相关页面"
              class="page-related-card"
            >
              <ul class="related-list">
                <li
                  v-for="related in relatedPages"
                  :key="related.id"
                  class="related-item"
                  tabindex="0"
                  @click="navigateToRelated(related)"
                  @keydown.enter.prevent="navigateToRelated(related)"
                >
                  <span class="related-item-title">{{ related.title }}</span>
                  <span class="related-item-cat">
                    {{ WikiPageCategoryLabels[related.category] ?? related.category }}
                  </span>
                </li>
              </ul>
            </NCard>
          </aside>
        </div>
      </template>

      <!-- 空态：既无数据也无错误（页面不存在等） -->
      <div v-else class="state-block page-empty">
        <NEmpty description="当前没有可展示的页面内容" />
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
/* ═══════════════ 根容器 ═══════════════ */
.wiki-entity-page {
  position: relative;
  padding: var(--lme-gap-xl) var(--lme-gap-2xl) var(--lme-gap-3xl);
}

/* ═══════════════ 状态：顶部细进度条 / state-block / NAlert 错误条 ═══════════════ */
.loading-bar {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 2px;
  overflow: hidden;
  z-index: var(--lme-z-raised);
  background: var(--lme-progressbar-bg);
}

.loading-bar::after {
  content: '';
  position: absolute;
  inset: 0;
  width: 40%;
  border-radius: var(--lme-radius-full);
  background: var(--wiki-accent);
  animation: loading-slide 1s var(--lme-ease-standard) infinite;
}

@keyframes loading-slide {
  from {
    transform: translateX(-100%);
  }
  to {
    transform: translateX(350%);
  }
}

.page-state.state-block {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-md);
  padding: var(--lme-gap-3xl) var(--lme-gap-md);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-md);
}

.state-text {
  font-size: var(--lme-font-size-sm);
}

/* 错误提示条：库 NAlert（配色走库主题的 --lme-error），只补下缘间距 */
.page-error {
  margin-bottom: var(--lme-gap-lg);
}

/* 空态：库 NEmpty */
.page-empty.state-block {
  padding: var(--lme-gap-3xl) var(--lme-gap-md);
}

/* ═══════════════ 标题区：金标题 + 分隔线 ═══════════════ */
.page-header {
  max-width: var(--wiki-content-max-width);
  margin: 0 auto var(--lme-gap-xl);
  padding-bottom: var(--lme-gap-md);
  border-bottom: 1px solid var(--wiki-section-head-border);
}

.page-title {
  margin: 0;
  font-size: var(--wiki-title-size);
  line-height: var(--wiki-title-line);
  font-weight: var(--lme-font-weight-bold);
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

.page-meta-tags {
  display: inline-flex;
}

.meta-date {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ═══════════════ 主体栅格：目录 | 主区 | 信息框 ═══════════════
   不再整页定宽：栅格本身 max-width 居中；信息框列用 minmax(260px, 320px)
   弹性收缩，正文列 minmax(0,1fr) 永不被挤爆。 */
.page-body {
  display: grid;
  grid-template-columns: var(--wiki-toc-width) minmax(0, 1fr) minmax(var(--wiki-aside-min), var(--wiki-aside-max));
  grid-template-areas: 'toc main aside';
  gap: var(--lme-gap-2xl);
  align-items: start;
  max-width: var(--wiki-page-max-width);
  margin-inline: auto;
}

.page-toc {
  grid-area: toc;
}

.page-main {
  grid-area: main;
  min-width: 0;
  max-width: var(--wiki-content-max-width);
}

.page-aside {
  grid-area: aside;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-lg);
  min-width: 0;
}

/* 中窄屏：信息框下沉到正文下方，目录保持左栏吸顶 */
@media (max-width: 1280px) {
  .page-body {
    grid-template-columns: var(--wiki-toc-width) minmax(0, 1fr);
    grid-template-areas:
      'toc main'
      'toc aside';
  }
}

/* 窄屏（<1100px）：单列上下堆叠，目录变为页内静态块 */
@media (max-width: 1100px) {
  .page-body {
    grid-template-columns: minmax(0, 1fr);
    grid-template-areas:
      'toc'
      'main'
      'aside';
  }

  .page-body > .page-toc {
    position: static;
    width: auto;
    max-height: none;
    padding: 0 0 var(--lme-gap-md);
  }
}

/* ═══════════════ 信息框（右栏 NCard 容器）：宽度交给栅格，补 zebra / hover ═══════════════ */
.page-infobox-card {
  overflow: hidden;
}

.page-aside :deep(.wiki-infobox) {
  width: 100%;
  border: none;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}

.page-aside :deep(.wiki-infobox-fields tr:nth-child(even)) {
  background: var(--wiki-infobox-zebra-bg);
}

.page-aside :deep(.wiki-infobox-fields tr:hover) {
  background: var(--wiki-infobox-row-hover);
}

/* ═══════════════ 分节 Tab（库 NTabs segment；.wiki-tab 由 tabProps 挂在库 tab 上） ═══════════════ */
.wiki-tabs {
  margin: var(--lme-gap-xl) 0;
}

.wiki-tab-label {
  font-size: var(--lme-font-size-md);
}

.wiki-tab-badge {
  padding: 0 var(--lme-gap-xs);
  background: var(--wiki-chip-bg);
  border: 1px solid var(--wiki-chip-border);
  border-radius: var(--wiki-chip-radius);
  color: var(--wiki-chip-text);
  font-size: var(--lme-font-size-xs);
  font-weight: var(--lme-font-weight-regular);
}

.wiki-tabs :deep(.n-tab-pane) {
  padding: 0;
}

/* ═══════════════ 分节：维基式开放式排版（金标题 + 下缘分隔线） ═══════════════ */
.wiki-section {
  margin: 0 0 var(--lme-gap-2xl);
  scroll-margin-top: var(--lme-gap-sm);
}

.section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lme-gap-md);
  margin: 0 0 var(--lme-gap-md);
  padding-bottom: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xl);
  font-weight: var(--lme-font-weight-bold);
  color: var(--wiki-section-title);
  border-bottom: 1px solid var(--wiki-section-head-border);
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

/* 分节操作（库 NButton text）：hover 收金色，与维基强调色一致 */
.section-actions :deep(.n-button) {
  color: var(--lme-text-muted);
}

.section-actions :deep(.n-button:hover) {
  color: var(--wiki-accent);
}

.wiki-section.collapsed .section-content {
  display: none;
}

.section-content {
  padding: 0 0 var(--lme-gap-md);
  font-size: var(--lme-font-size-md);
  color: var(--wiki-body-text);
  line-height: var(--lme-line-height-relaxed);
  white-space: pre-wrap;
}

/* 编辑器 */
.section-editor {
  padding: 0 0 var(--lme-gap-md);
}

/* 编辑器：库 NInput(textarea)，底色/边框走库主题，只补等宽字体 */
.editor-textarea :deep(.n-input__textarea-el) {
  font-family: var(--lme-font-mono);
  font-size: var(--lme-font-size-sm);
  line-height: var(--lme-line-height-normal);
}

.editor-actions {
  display: flex;
  gap: var(--lme-gap-sm);
  margin-top: var(--lme-gap-md);
}

/* ═══════════════ 分节条目 ═══════════════ */
.entry-list {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-md);
  padding: 0 0 var(--lme-gap-md);
}

.entry-item {
  padding-left: var(--lme-gap-md);
  border-left: 2px solid var(--wiki-accent-dim);
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
  font-weight: var(--lme-font-weight-semibold);
  color: var(--wiki-entry-title);
}

.entry-body {
  margin-top: var(--lme-gap-xs);
  font-size: var(--lme-font-size-md);
  color: var(--wiki-body-text);
  line-height: var(--lme-line-height-relaxed);
  white-space: pre-wrap;
  word-break: break-word;
}

/* ═══════════════ 关联资源列表 ═══════════════ */
.binding-list {
  padding: 0 0 var(--lme-gap-md);
}

.binding-list-title {
  font-size: var(--lme-font-size-sm);
  font-weight: var(--lme-font-weight-semibold);
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

/* 「去编辑」：库 NButton text-primary（维基区即金色） */
.binding-edit-btn {
  flex-shrink: 0;
}

/* ═══════════════ 相关页面（右栏 NCard） ═══════════════ */
.page-related-card :deep(.n-card__content) {
  padding: var(--lme-gap-sm);
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
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
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

/* ═══════════════ 焦点可访问性（聚焦环一律金色，不用紫） ═══════════════ */
.page-header :deep(.n-button:focus-visible),
.page-main :deep(.n-button:focus-visible),
.page-aside :deep(.n-button:focus-visible),
.page-error :deep(.n-button:focus-visible),
.wiki-tab:focus-visible,
.related-item:focus-visible,
.page-toc :deep(.wiki-toc-link):focus-visible {
  outline: none;
  box-shadow: var(--wiki-focus-ring);
}
</style>
