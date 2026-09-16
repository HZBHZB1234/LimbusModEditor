<script setup lang="ts">
// 递归树节点组件（解决 vue-tsc 嵌套模板作用域问题）

export interface TreeNode {
  name: string
  path: string
  isLeaf: boolean
  childCount: number
  assetIds: string[]
  expanded: boolean
  loading: boolean
  children: TreeNode[] | null
  depth: number
}

defineProps<{
  node: TreeNode
}>()

const emit = defineEmits<{
  (e: 'toggle', node: TreeNode): void
  (e: 'select', assetId: string): void
}>()

function handleClick(node: TreeNode) {
  if (node.isLeaf && node.assetIds.length > 0) {
    emit('select', node.assetIds[0])
  } else {
    emit('toggle', node)
  }
}
</script>

<template>
  <div class="tree-node-entry">
    <!-- 节点行 -->
    <div
      class="tree-node-row"
      :class="{ leaf: node.isLeaf, expanded: node.expanded }"
      :style="{ paddingLeft: node.depth * 16 + 8 + 'px' }"
      @click="handleClick(node)"
    >
      <span class="tree-arrow">
        {{ node.isLeaf ? '•' : node.loading ? '⏳' : node.expanded ? '▼' : '▶' }}
      </span>
      <span class="tree-node-name">{{ node.name }}</span>
      <span class="tree-count">{{ node.isLeaf ? node.assetIds.length : node.childCount }}</span>
    </div>

    <!-- 子节点（展开时渲染） -->
    <template v-if="node.expanded && node.children">
      <TreeNode
        v-for="child in node.children"
        :key="child.path"
        :node="child"
        @toggle="(n: TreeNode) => emit('toggle', n)"
        @select="(id: string) => emit('select', id)"
      />
    </template>
  </div>
</template>

<style scoped>
.tree-node-row {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-xs);
  padding: 2px var(--lme-gap-sm);
  cursor: pointer;
  border-radius: var(--lme-radius-sm);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
}

.tree-node-row:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.tree-node-row.leaf {
  color: var(--lme-text-muted);
}

.tree-arrow {
  width: 14px;
  text-align: center;
  font-size: 10px;
  flex-shrink: 0;
}

.tree-node-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tree-count {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  background: var(--lme-bg-elevated);
  padding: 1px 6px;
  border-radius: 8px;
}
</style>
