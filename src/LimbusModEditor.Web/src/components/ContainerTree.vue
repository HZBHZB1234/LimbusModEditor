<script setup lang="ts">
// 容器树：惰性展开（只展开到可视层，不一次性物化全部节点）
// 对应 WPF 旧界面的目录树视图

import { ref } from 'vue'
import { ipc } from '@/ipc'
import TreeNode from './TreeNode.vue'
import type { TreeNode as TreeNodeModel } from './TreeNode.vue'

defineProps<{
  visible: boolean
}>()

const emit = defineEmits<{
  (e: 'select', assetId: string): void
}>()

// 根节点（惰性加载）
const rootNodes = ref<TreeNodeModel[]>([])
const loading = ref(false)
const loadGeneration = ref(0)

async function loadRoots() {
  const gen = ++loadGeneration.value
  loading.value = true
  try {
    // 从服务端获取根级容器节点
    const result = await ipc.request<{ nodes: TreeNodeModel[] }>('catalog.containerRoots', {})
    if (gen !== loadGeneration.value) return
    rootNodes.value = result.nodes.map((n) => ({
      ...n,
      expanded: false,
      loading: false,
      children: null,
      depth: 0,
    }))
  } catch {
    if (gen !== loadGeneration.value) return
    rootNodes.value = []
  } finally {
    if (gen === loadGeneration.value) loading.value = false
  }
}

async function toggleNode(node: TreeNodeModel) {
  if (node.isLeaf) {
    if (node.assetIds.length > 0) {
      emit('select', node.assetIds[0])
    }
    return
  }

  node.expanded = !node.expanded

  // F-05 fix: 折叠时释放子节点数组，减少内存占用
  if (!node.expanded) {
    node.children = null
    return
  }

  if (node.expanded && node.children === null) {
    node.loading = true
    const gen = loadGeneration.value
    try {
      const result = await ipc.request<{ nodes: TreeNodeModel[] }>('catalog.containerChildren', {
        parentPath: node.path,
      })
      if (gen !== loadGeneration.value) return
      node.children = result.nodes.map((n) => ({
        ...n,
        expanded: false,
        loading: false,
        children: null,
        depth: node.depth + 1,
      }))
    } catch {
      if (gen !== loadGeneration.value) return
      node.children = []
    } finally {
      if (gen === loadGeneration.value) node.loading = false
    }
  }
}

// 初始化加载根节点
loadRoots()
</script>

<template>
  <div class="container-tree" v-if="visible">
    <div v-if="loading" class="tree-loading">加载目录树…</div>
    <div v-else class="tree-nodes">
      <TreeNode
        v-for="node in rootNodes"
        :key="node.path"
        :node="node"
        @toggle="toggleNode"
        @select="(id: string) => emit('select', id)"
      />
      <div v-if="rootNodes.length === 0 && !loading" class="tree-empty">
        <div class="empty-icon">📂</div>
        <div class="empty-title">暂无容器数据</div>
        <div class="empty-desc">尚未建立资源目录索引。请先运行「启动扫描」建立索引，或检查缓存目录配置是否正确。</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.container-tree {
  flex: 1;
  overflow: auto;
  padding: var(--lme-gap-sm);
}

.tree-loading,
.tree-empty {
  padding: var(--lme-gap-xl);
  text-align: center;
  color: var(--lme-text-muted);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--lme-gap-sm);
}

.empty-icon {
  font-size: 32px;
  margin-bottom: var(--lme-gap-sm);
}

.empty-title {
  font-size: var(--lme-font-size-md);
  font-weight: 600;
  color: var(--lme-text-secondary);
}

.empty-desc {
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
  max-width: 280px;
  line-height: 1.5;
}
</style>
