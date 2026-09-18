<script setup lang="ts">
// 维基 · Spine 总览（/wiki/spine）：只读浏览全库所有 Spine 挂点。
//
// 为什么要有这一页：Spine 三件套（骨架 / 图集 / 纹理）一直都在本地缓存里，但只有
// 「被维基页面绑定的那一小批」才会出现在界面上 —— 战斗用的敌人 / 异想体 / 人格 / E.G.O
// 的 Appearance prefab（Assets/Resources_moved/Prefab/SD/**）从来没有被任何页面绑定过，
// 于是用户在编辑器里完全看不到它们。这一页把它们列出来并支持直接渲染。
//
// 数据链路两条（与维基页面同一套后端，不新造渲染器）：
//   ① spine.catalog —— 只列名册，**不解素材**（解一套实测平均 2.1s，几百套一次解不可接受）；
//   ② spine.resolve —— 点开某一条时**按需**解它的三件套地址（后端有按 refKey 的结果缓存），
//      再交给既有的 WikiSpineViewer 渲染。
//
// 诚实边界：bundle 被 Unity 清掉、或该 prefab 的引用链里确实没有骨架与图集的，
// 如实显示中文原因，不给半个地址、不拿别的素材凑。
import { computed, onMounted, ref } from 'vue'
import { NAlert, NButton, NEmpty, NInput, NSpin, NSwitch, NTag } from 'naive-ui'
import { ipc } from '@/ipc'
import type { SpineCatalogItem, SpineCatalogResponse, SpineResolveResult } from '@/ipc'
import WikiShell from '@/components/WikiShell.vue'
import WikiSpineViewer from '@/components/wiki/WikiSpineViewer.vue'

const PAGE_SIZE = 60

const loading = ref(false)
const error = ref<string | null>(null)
const items = ref<SpineCatalogItem[]>([])
const total = ref(0)
const summary = ref({ total: 0, bound: 0, unbound: 0, bundleMissing: 0 })

const keyword = ref('')
const onlyUnbound = ref(true)
const showMissing = ref(true)
const page = ref(0)

/** 当前选中要预览的那条（null = 还没点开）。 */
const selected = ref<SpineCatalogItem | null>(null)
const resolving = ref(false)
const resolved = ref<SpineResolveResult | null>(null)

/** 客户端过滤「bundle 不在本机」的那批（名册一次最多取一页，过滤放在当前页上，口径说清楚）。 */
const visibleItems = computed(() =>
  showMissing.value ? items.value : items.value.filter((i) => i.bundlePresent),
)

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

/** 归属页面：明细回来后用明细的（更准），否则用名册的。 */
const ownerLinks = computed(
  () => resolved.value?.ownerPageIds ?? selected.value?.ownerPageIds ?? [],
)

async function loadCatalog() {
  loading.value = true
  error.value = null
  try {
    const result = await ipc.request<SpineCatalogResponse>('spine.catalog', {
      keyword: keyword.value.trim() || null,
      onlyUnbound: onlyUnbound.value,
      offset: page.value * PAGE_SIZE,
      limit: PAGE_SIZE,
    })
    items.value = result.items
    total.value = result.total
    summary.value = result.summary
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  page.value = 0
  void loadCatalog()
}

function goToPage(next: number) {
  if (next < 0 || next >= pageCount.value) return
  page.value = next
  void loadCatalog()
}

/**
 * 解析状态 → 标签配色。只影响观感，不改变任何口径：
 * 「未命中」这类是**正确结果**（该 prefab 本来就不是 Spine），不是错误，所以用中性色。
 */
function statusTagType(status: string): 'default' | 'info' | 'warning' | 'error' | 'success' {
  switch (status) {
    case 'parsed':
      return 'success'
    case 'likely':
      return 'info'
    case 'bundle-missing':
      return 'warning'
    case 'no-skeleton':
      return 'default'
    case 'uncategorized':
      return 'default'
    default:
      return 'error'
  }
}

/** 点开一条：按需解三件套地址（后端缓存命中时会很快）。 */
async function openItem(item: SpineCatalogItem) {
  selected.value = item
  resolved.value = null
  resolving.value = true
  try {
    resolved.value = await ipc.request<SpineResolveResult>(
      'spine.resolve',
      { refKey: item.refKey },
      // 解一套要整读一个大 bundle，实测最慢几秒；30s 默认超时留足余量。
      120000,
    )
  } catch (e: unknown) {
    resolved.value = {
      ok: false,
      skeletonUrl: null,
      atlasUrl: null,
      textureUrls: null,
      skeletonFormat: null,
      label: null,
      reason: e instanceof Error ? e.message : String(e),
      source: item.source,
      ownerPageIds: item.ownerPageIds,
      bundlePresent: item.bundlePresent,
      parseStatus: 'failed',
      parseStatusLabel: '取数失败',
    }
  } finally {
    resolving.value = false
  }
}

onMounted(loadCatalog)
</script>

<template>
  <WikiShell
    current-page="spine"
    :breadcrumbs="[
      { label: '维基', route: '/wiki' },
      { label: 'Spine 总览', route: '/wiki/spine' },
    ]"
  >
    <div class="spine-browser">
      <header class="spine-head">
        <h1 class="spine-title">Spine 总览</h1>
        <p class="spine-sub">
          全库 Spine 挂点（人格立绘 + 战斗用的敌人 / 异想体 / 人格 / E.G.O）。名册只列清单，
          点开才解那一条的三件套 —— 解一套要整读一个大 bundle，全量预解不现实。
        </p>
        <div class="spine-stats">
          <NTag size="small" :bordered="false">挂点 {{ summary.total }}</NTag>
          <NTag size="small" type="info" :bordered="false">已被页面绑定 {{ summary.bound }}</NTag>
          <NTag size="small" type="warning" :bordered="false">未绑定 {{ summary.unbound }}</NTag>
          <NTag size="small" type="error" :bordered="false">
            bundle 不在本机 {{ summary.bundleMissing }}
          </NTag>
        </div>
      </header>

      <div class="spine-filters">
        <NInput
          v-model:value="keyword"
          placeholder="按骨架名或路径搜索（如 fairy_ism、SD/Enemy）"
          clearable
          style="width: 320px"
          @keyup.enter="applyFilters"
        />
        <NButton size="small" type="primary" @click="applyFilters">搜索</NButton>
        <label class="spine-switch">
          <NSwitch v-model:value="onlyUnbound" size="small" @update:value="applyFilters" />
          <span>只看未被页面绑定的</span>
        </label>
        <label class="spine-switch">
          <NSwitch v-model:value="showMissing" size="small" />
          <span>显示 bundle 不在本机的</span>
        </label>
      </div>

      <NAlert v-if="error" type="error" :bordered="false" class="spine-alert">
        取 Spine 名册失败：{{ error }}
      </NAlert>

      <div class="spine-body">
        <!-- 左：名册 -->
        <div class="spine-list">
          <div v-if="loading" class="spine-list-state"><NSpin size="small" /> 加载中…</div>
          <NEmpty v-else-if="visibleItems.length === 0" size="small" description="没有匹配的 Spine 挂点" />
          <ul v-else class="spine-items">
            <li
              v-for="item in visibleItems"
              :key="item.refKey"
              class="spine-item"
              :class="{ active: selected?.refKey === item.refKey }"
              :title="item.refKey"
              @click="openItem(item)"
            >
              <div class="spine-item-main">
                <span class="spine-item-name">{{ item.name }}</span>
                <NTag size="tiny" :bordered="false">{{ item.group }}</NTag>
                <NTag v-if="item.boundPageCount > 0" size="tiny" type="info" :bordered="false">
                  {{ item.boundPageCount }} 页
                </NTag>
                <NTag
                  size="tiny"
                  :bordered="false"
                  :type="statusTagType(item.parseStatus)"
                >
                  {{ item.parseStatusLabel }}
                </NTag>
              </div>
              <div class="spine-item-path lme-mono">{{ item.refKey }}</div>
              <div class="spine-item-source">
                <span>{{ item.source }}</span>
                <RouterLink
                  v-for="owner in item.ownerPageIds"
                  :key="owner"
                  class="spine-item-link"
                  :to="`/wiki/page/${owner}`"
                  @click.stop
                >
                  去 {{ owner }}
                </RouterLink>
              </div>
            </li>
          </ul>

          <div v-if="pageCount > 1" class="spine-pager">
            <NButton size="tiny" :disabled="page === 0" @click="goToPage(page - 1)">上一页</NButton>
            <span class="spine-pager-text">{{ page + 1 }} / {{ pageCount }}（共 {{ total }} 条）</span>
            <NButton size="tiny" :disabled="page + 1 >= pageCount" @click="goToPage(page + 1)">下一页</NButton>
          </div>
        </div>

        <!-- 右：预览 -->
        <div class="spine-preview">
          <NEmpty v-if="!selected" size="small" description="从左边选一条查看三件套" />

          <template v-else>
            <div class="spine-preview-head">
              <span class="spine-preview-name">{{ selected.name }}</span>
              <NTag size="tiny" :bordered="false">{{ selected.group }}</NTag>
              <NTag
                size="tiny"
                :bordered="false"
                :type="statusTagType(resolved?.parseStatus ?? selected.parseStatus)"
              >
                {{ resolved?.parseStatusLabel ?? selected.parseStatusLabel }}
              </NTag>
            </div>
            <div class="spine-preview-path lme-mono">{{ selected.refKey }}</div>

            <div class="spine-preview-source">
              <span class="spine-preview-source-label">来源</span>
              <span>{{ resolved?.source ?? selected.source }}</span>
            </div>
            <div v-if="ownerLinks.length > 0" class="spine-preview-owners">
              <span class="spine-preview-source-label">归属页面</span>
              <RouterLink
                v-for="owner in ownerLinks"
                :key="owner"
                class="spine-item-link"
                :to="`/wiki/page/${owner}`"
              >
                {{ owner }}
              </RouterLink>
            </div>

            <div v-if="resolving" class="spine-preview-state">
              <NSpin size="small" /> 正在解三件套（要整读它所在的 bundle，可能要几秒）…
            </div>

            <NAlert v-else-if="resolved && !resolved.ok" type="warning" :bordered="false">
              <div class="spine-reason-title">这条取不到 Spine 三件套</div>
              <div class="spine-reason">{{ resolved.reason ?? '原因未知' }}</div>
              <div v-if="!resolved.bundlePresent" class="spine-reason-hint">
                它所在的 bundle 文件此刻不在本机（Unity 临时缓存被清）。
                这不是代码能补的 —— 跑一次游戏或让平台重新下载后就会回来。
              </div>
              <div v-else class="spine-reason-hint">
                该 prefab 的引用链里确实没有骨架与图集，它本来就不是 Spine 资源
                （界面如实显示原因，不拿别的素材凑）。
              </div>
            </NAlert>

            <template v-else-if="resolved?.ok">
              <WikiSpineViewer
                :skeleton-url="resolved.skeletonUrl ?? undefined"
                :atlas-url="resolved.atlasUrl ?? undefined"
                :texture-urls="resolved.textureUrls ?? undefined"
                :title="resolved.label ?? selected.name"
                :width="480"
                :height="560"
              />
              <dl class="spine-files">
                <dt>骨架（{{ resolved.skeletonFormat }}）</dt>
                <dd class="lme-mono">{{ resolved.skeletonUrl }}</dd>
                <dt>图集</dt>
                <dd class="lme-mono">{{ resolved.atlasUrl }}</dd>
                <dt>纹理页</dt>
                <dd class="lme-mono">
                  {{ Object.keys(resolved.textureUrls ?? {}).join('、') || '（无）' }}
                </dd>
              </dl>
            </template>
          </template>
        </div>
      </div>
    </div>
  </WikiShell>
</template>

<style scoped>
.spine-browser {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: var(--lme-gap-lg);
  gap: var(--lme-gap-md);
}

.spine-head {
  flex-shrink: 0;
}

.spine-title {
  margin: 0 0 var(--lme-gap-xs);
  font-size: var(--lme-font-size-xl);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--wiki-section-title);
}

.spine-sub {
  margin: 0 0 var(--lme-gap-sm);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  max-width: 780px;
}

.spine-stats {
  display: flex;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.spine-filters {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
  flex-shrink: 0;
}

.spine-switch {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.spine-alert {
  flex-shrink: 0;
}

.spine-body {
  flex: 1;
  display: flex;
  gap: var(--lme-gap-lg);
  min-height: 0;
}

.spine-list {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-panel);
  overflow: hidden;
}

.spine-list-state {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-lg);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
}

.spine-items {
  list-style: none;
  margin: 0;
  padding: var(--lme-gap-xs);
  overflow-y: auto;
  flex: 1;
}

.spine-item {
  padding: var(--lme-gap-sm);
  border-radius: var(--lme-radius-sm);
  cursor: pointer;
  border: 1px solid transparent;
}

.spine-item:hover {
  background: var(--lme-bg-hover);
}

.spine-item.active {
  background: var(--wiki-nav-active-bg);
  border-color: var(--wiki-nav-active-bar);
}

.spine-item-main {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  flex-wrap: wrap;
}

.spine-item-name {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-primary);
  font-weight: 500;
}

.spine-item-path {
  margin-top: 2px;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spine-item-source {
  margin-top: 2px;
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.spine-item-link {
  color: var(--wiki-link);
  text-decoration: none;
}

.spine-item-link:hover {
  text-decoration: underline;
}

.spine-pager {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-sm);
  border-top: 1px solid var(--lme-border);
  flex-shrink: 0;
}

.spine-pager-text {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.spine-preview {
  width: 520px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-sm);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-md);
  background: var(--lme-bg-panel);
  padding: var(--lme-gap-md);
  overflow-y: auto;
}

.spine-preview-head {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.spine-preview-name {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-primary);
}

.spine-preview-path {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  word-break: break-all;
}

.spine-preview-source,
.spine-preview-owners {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  flex-wrap: wrap;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-secondary);
}

.spine-preview-source-label {
  color: var(--lme-text-muted);
}

.spine-preview-state {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  color: var(--lme-text-muted);
  font-size: var(--lme-font-size-sm);
  padding: var(--lme-gap-md) 0;
}

.spine-reason-title {
  font-weight: 600;
  margin-bottom: var(--lme-gap-xs);
}

.spine-reason {
  font-size: var(--lme-font-size-sm);
  word-break: break-all;
}

.spine-reason-hint {
  margin-top: var(--lme-gap-sm);
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

.spine-files {
  margin: 0;
  display: grid;
  grid-template-columns: auto 1fr;
  gap: var(--lme-gap-xs) var(--lme-gap-md);
  font-size: var(--lme-font-size-xs);
}

.spine-files dt {
  color: var(--lme-text-muted);
}

.spine-files dd {
  margin: 0;
  color: var(--lme-text-secondary);
  word-break: break-all;
}
</style>
