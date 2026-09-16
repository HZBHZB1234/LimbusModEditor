// 资源目录 store：搜索 + 服务端分页 + 虚拟滚动
// 一律「查询→一页」，禁止取全量再前端筛

import { defineStore } from 'pinia'
import { ipc } from '@/ipc'
import type {
  AssetSearchQuery,
  AssetRecord,
  AssetCatalogPage,
  AssetSortKind,
  AssetType,
  AssetEditState,
} from '@/ipc'

interface CatalogState {
  // 当前搜索判据
  query: AssetSearchQuery
  // 当前页数据
  items: AssetRecord[]
  totalCount: number
  offset: number
  pageSize: number
  // 加载状态
  loading: boolean
  error: string | null
  // 搜索代际（用于废弃过期结果）
  generation: number
  // 选中
  selectedAssetId: string | null
  // 视图模式
  treeMode: boolean
  // 性能测量
  lastQueryMs: number
}

export const useCatalogStore = defineStore('catalog', {
  state: (): CatalogState => ({
    query: {
      sort: 'Name',
      hasContainerEntry: true,
      showStaticTables: false,
    },
    items: [],
    totalCount: 0,
    offset: 0,
    pageSize: 200,
    loading: false,
    error: null,
    generation: 0,
    selectedAssetId: null,
    treeMode: true,
    lastQueryMs: 0,
  }),

  getters: {
    pageCount(state): number {
      return Math.max(1, Math.ceil(state.totalCount / state.pageSize))
    },
    currentPage(state): number {
      return Math.floor(state.offset / state.pageSize) + 1
    },
    selectedAsset(state): AssetRecord | null {
      return state.items.find((a) => a.assetId === state.selectedAssetId) ?? null
    },
  },

  actions: {
    /**
     * 执行搜索（带防抖由调用方处理）。
     * 每次搜索重置到第 0 页，递增 generation 废弃过期结果。
     */
    async search(query?: Partial<AssetSearchQuery>) {
      if (query) {
        this.query = { ...this.query, ...query }
      }
      this.offset = 0
      await this.fetchPage(0)
    },

    /**
     * 翻到指定页（服务端分页）。
     */
    async goToPage(pageIndex: number) {
      const clamped = Math.max(0, Math.min(pageIndex, this.pageCount - 1))
      this.offset = clamped * this.pageSize
      await this.fetchPage(this.offset)
    },

    async nextPage() {
      await this.goToPage(this.currentPage) // currentPage is 1-based, goToPage expects 0-based
    },

    async prevPage() {
      await this.goToPage(this.currentPage - 2)
    },

    async firstPage() {
      await this.goToPage(0)
    },

    async lastPage() {
      await this.goToPage(this.pageCount - 1)
    },

    /**
     * 核心：从服务端取一页数据。
     * 使用 generation 守卫——只保留最新一代的结果。
     */
    async fetchPage(offset: number) {
      const gen = ++this.generation
      this.loading = true
      this.error = null
      const t0 = performance.now()

      try {
        const result = await ipc.request<AssetCatalogPage>('catalog.query', {
          query: this.query,
          offset,
          take: this.pageSize,
        })

        // 代际守卫：只接受最新一代的响应
        if (gen !== this.generation) return

        this.items = result.items
        this.totalCount = result.totalCount
        this.offset = result.offset
        this.lastQueryMs = performance.now() - t0
      } catch (e: unknown) {
        if (gen !== this.generation) return
        this.error = e instanceof Error ? e.message : String(e)
        this.items = []
        this.totalCount = 0
      } finally {
        if (gen === this.generation) {
          this.loading = false
        }
      }
    },

    selectAsset(assetId: string | null) {
      this.selectedAssetId = assetId
    },

    setViewMode(tree: boolean) {
      this.treeMode = tree
    },

    updateFilter(partial: Partial<AssetSearchQuery>) {
      this.query = { ...this.query, ...partial }
    },
  },
})
