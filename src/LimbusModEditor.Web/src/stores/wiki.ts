// 维基 store：页面加载 / 保存 / 搜索 / 分类索引
// 含代际守卫（generation guard）—— 只保留最新一代异步结果
// 注意：无候选导入功能（候选导入 = 结构变更，已被 t56 移除）

import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ipc } from '@/ipc'
import type {
  WikiPage,
  WikiPageCategory,
  WikiSearchQuery,
  WikiSearchResult,
  WikiSearchResponse,
  CategoryIndex,
  ContentEdit,
} from '@/ipc'

export const useWikiStore = defineStore('wiki', () => {
  // ── 响应式状态 ──
  const pages = ref<WikiPage[]>([])
  const currentPage = ref<WikiPage | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)
  const searchResults = ref<WikiSearchResult[]>([])
  const categoryIndex = ref<CategoryIndex | null>(null)

  // ── 代际守卫 ──
  let generation = 0

  // ── 辅助：从数组中按 id 查找页面索引 ──
  function findPageIndex(pageId: string): number {
    return pages.value.findIndex((p) => p.id === pageId)
  }

  // ── 加载页面 ──
  async function loadPage(pageId: string): Promise<WikiPage | null> {
    const gen = ++generation
    loading.value = true
    error.value = null

    try {
      const result = await ipc.request<WikiPage>('wiki.page.load', { pageId })

      if (gen !== generation) return null // 代际守卫：结果已过期

      // 更新或追加到 pages 数组
      const idx = findPageIndex(pageId)
      if (idx >= 0) {
        pages.value.splice(idx, 1, result)
      } else {
        pages.value.push(result)
      }
      currentPage.value = result
      return result
    } catch (e: unknown) {
      if (gen !== generation) return null
      error.value = e instanceof Error ? e.message : String(e)
      return null
    } finally {
      if (gen === generation) {
        loading.value = false
      }
    }
  }

  // ── 保存页面 ──
  async function savePage(page: WikiPage): Promise<boolean> {
    const gen = ++generation
    loading.value = true
    error.value = null

    try {
      await ipc.request<WikiPage>('wiki.page.save', { page })

      if (gen !== generation) return false

      // 更新数组中的页面
      const idx = findPageIndex(page.id)
      if (idx >= 0) {
        pages.value.splice(idx, 1, page)
      } else {
        pages.value.push(page)
      }
      currentPage.value = page
      return true
    } catch (e: unknown) {
      if (gen !== generation) return false
      error.value = e instanceof Error ? e.message : String(e)
      return false
    } finally {
      if (gen === generation) {
        loading.value = false
      }
    }
  }

  // ── 搜索 ──
  async function search(query: WikiSearchQuery): Promise<WikiSearchResult[]> {
    const gen = ++generation
    loading.value = true
    error.value = null

    try {
      const result = await ipc.request<WikiSearchResponse>('wiki.search', {
        keyword: query.keyword,
        category: query.category,
        offset: query.offset,
        limit: query.limit,
      })

      if (gen !== generation) return []

      searchResults.value = result.results
      return result.results
    } catch (e: unknown) {
      if (gen !== generation) return []
      error.value = e instanceof Error ? e.message : String(e)
      searchResults.value = []
      return []
    } finally {
      if (gen === generation) {
        loading.value = false
      }
    }
  }

  // ── 加载分类索引 ──
  async function loadCategory(category: WikiPageCategory): Promise<CategoryIndex | null> {
    const gen = ++generation
    loading.value = true
    error.value = null

    try {
      const result = await ipc.request<CategoryIndex>('wiki.category.load', { category })

      if (gen !== generation) return null

      categoryIndex.value = result

      // 将分类下的页面同步到 pages 数组（去重合并）
      for (const ref of result.pages) {
        const idx = findPageIndex(ref.id)
        if (idx < 0) {
          // 仅存储引用信息，详细内容需按需加载
          pages.value.push({
            id: ref.id,
            title: ref.title,
            category,
            subtitle: ref.subtitle,
            sections: [],
            toc: [],
          })
        }
      }

      return result
    } catch (e: unknown) {
      if (gen !== generation) return null
      error.value = e instanceof Error ? e.message : String(e)
      return null
    } finally {
      if (gen === generation) {
        loading.value = false
      }
    }
  }

  // ── 应用编辑到本地 currentPage ──
  function applyEdit(edit: ContentEdit): void {
    if (!currentPage.value) return

    const page = currentPage.value
    if (edit.sectionId) {
      const section = page.sections.find((s) => s.id === edit.sectionId)
      if (section) {
        if (edit.field === 'title') section.title = edit.newValue
        else if (edit.field === 'content') section.content = edit.newValue
      }
    }

    // 同步更新 pages 数组
    const idx = findPageIndex(page.id)
    if (idx >= 0) {
      pages.value.splice(idx, 1, { ...page })
    }
  }

  // ── 清除错误 ──
  function clearError(): void {
    error.value = null
  }

  // ── 重置状态 ──
  function reset(): void {
    pages.value = []
    currentPage.value = null
    searchResults.value = []
    categoryIndex.value = null
    error.value = null
    loading.value = false
  }

  return {
    // 状态
    pages,
    currentPage,
    loading,
    error,
    searchResults,
    categoryIndex,
    // 动作
    loadPage,
    savePage,
    search,
    loadCategory,
    applyEdit,
    clearError,
    reset,
  }
})
