<script setup lang="ts">
// 递归树节点组件（解决 vue-tsc 嵌套模板作用域问题）
// ui-redesign r6：emoji/几何符号换 AppIcon；悬停/按下配色走令牌；props / emits 未变

import { NTooltip } from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'

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
        <AppIcon v-if="node.loading" name="loader" :size="12" class="tree-spin" />
        <AppIcon
          v-else-if="!node.isLeaf"
          :name="node.expanded ? 'chevronDown' : 'chevronRight'"
          :size="13"
        />
      </span>

      <span class="tree-node-icon">
        <AppIcon
          :name="node.isLeaf ? 'file' : node.expanded ? 'folderOpen' : 'folder'"
          :size="13"
        />
      </span>

      <span class="tree-node-name">{{ node.name }}</span>

      <NTooltip placement="right" :show-arrow="false" :delay="500">
        <template #trigger>
          <span class="tree-count">{{ node.isLeaf ? node.assetIds.length : node.childCount }}</span>
        </template>
        {{ node.isLeaf ? '该容器下的资源条数' : '该容器下的子容器数' }}
      </NTooltip>
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
  /* 选中/按下时的左侧强调条占位（避免位移） */
  border-left: 2px solid transparent;
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-secondary);
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.tree-node-row:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* 选中态：本组件无 selected 入参（对外接口不得改动），
   故用「按下 / 键盘聚焦」表达选中，配色与强调条与列表行一致 */
.tree-node-row:active,
.tree-node-row:focus-visible {
  background: var(--lme-bg-selected);
  border-left-color: var(--lme-accent);
  color: var(--lme-text-primary);
  outline: none;
}

.tree-node-row.leaf {
  color: var(--lme-text-muted);
}

.tree-node-row.expanded {
  color: var(--lme-text-primary);
}

.tree-arrow {
  width: 14px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.tree-spin {
  animation: lme-spin 1.1s linear infinite;
}

.tree-node-icon {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  color: var(--lme-text-muted);
}

.tree-node-row.expanded .tree-node-icon {
  color: var(--lme-accent);
}

.tree-node-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tree-count {
  display: inline-block;
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
  background: var(--lme-bg-elevated);
  padding: 1px 6px;
  border-radius: var(--lme-radius-full);
}
</style>
