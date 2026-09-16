# LimbusModEditor.Web 前端工程

## 快速开始

```bash
npm install
npm run dev    # 开发模式
npm run build  # 构建产物 → dist/
npm run type-check  # 类型检查
```

构建产物 `dist/` 发布到 `artifacts/publish-win-x64/wwwroot/` 后，由 WebView2 虚拟主机 `https://lme.app/` 加载。

## 目录结构

```
src/
├── main.ts              # 应用入口
├── App.vue              # 应用外壳（活动栏 + 页面宿主）
├── router/index.ts      # 路由配置
├── styles/tokens.css    # 设计色（唯一允许出处）
├── ipc/                 # IPC 契约客户端
│   ├── types.ts         # 契约 DTO
│   ├── client.ts        # 请求/响应/事件/取消/错误
│   └── index.ts
├── stores/              # Pinia 状态管理
│   ├── catalog.ts       # 资源目录搜索/分页
│   ├── preview.ts       # 预览（代际守卫 + 并发闸门）
│   └── uiState.ts       # UI 状态持久化
├── components/          # 共享构件
│   ├── VirtualList.vue  # 虚拟滚动列表
│   ├── SearchFilters.vue # 搜索筛选栏
│   ├── PreviewPane.vue  # 预览面板
│   ├── PageBar.vue      # 页码条
│   ├── ContainerTree.vue # 容器树（惰性展开）
│   └── TreeNode.vue     # 递归树节点
└── views/               # 页面
    └── AssetsView.vue   # 资源工作台
```

## 如何新增一个工作台页面

### 1. 创建视图文件

在 `src/views/` 下创建 `XxxView.vue`：

```vue
<script setup lang="ts">
// 使用共享组件：SearchFilters, VirtualList, PreviewPane, PageBar, ContainerTree
// 使用 store：useCatalogStore, usePreviewStore, useUiStateStore
// 使用 IPC：import { ipc } from '@/ipc'
</script>
<template>
  <!-- 设计色只允许使用 tokens.css 中的 CSS 变量 -->
</template>
<style scoped>
/* 禁止硬编码色值，一律使用 var(--lme-xxx) */
</style>
```

### 2. 注册路由

在 `src/router/index.ts` 中添加路由：

```ts
{
  path: '/xxx',
  name: 'xxx',
  component: () => import('@/views/XxxView.vue'),
}
```

### 3. 添加导航入口

在 `src/App.vue` 的 `navItems` 数组中添加：

```ts
{ key: 'xxx', label: '显示名', icon: '🔧', route: '/xxx' }
```

### 4. 如需新建 store

在 `src/stores/` 下创建 `xxx.ts`，使用 Pinia `defineStore`：

```ts
import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useXxxStore = defineStore('xxx', () => {
  // state / getters / actions
})
```

### 5. 如需扩展 IPC 方法

在 `src/ipc/types.ts` 中添加 DTO 类型，在 `src/ipc/client.ts` 无需修改（通用 `request<T>()` 方法已支持任意方法名）。

## IPC 客户端使用

```ts
import { ipc } from '@/ipc'

// 类型化请求
const result = await ipc.request<AssetCatalogPage>('catalog.query', {
  query: { text: 'keyword', sort: 'Name' },
  offset: 0,
  take: 200,
})

// 事件订阅（返回退订函数)
const unsubscribe = ipc.on('progress', (payload) => {
  console.log('进度:', payload)
})

// 取消操作
ipc.cancel('operation-id')

// 销毁（退订所有监听）
ipc.destroy()
```

## 性能纪律（铁律）

1. **服务端分页**：一律「查询→一页」，禁止取全量再前端筛。`pageSize = 200`。
2. **虚拟滚动**：使用 `VirtualList` 组件，只渲染可视窗口 + overscan 行。
3. **代际守卫**：快速切换选中/翻页时，递增 generation 废弃过期响应。
4. **并发闸门**：大图解码使用 `acquireSlot()` / `releaseSlot()`，最多 4 并发。
5. **内存释放**：切页/切选中时释放不再需要的解码结果、子节点数组、blob URL。

## 设计色铁律

- 设计色只允许出现在 `src/styles/tokens.css`
- `src/views/` 与 `src/components/` 下硬编码色值必须为 0
- 一律使用 `var(--lme-xxx)` CSS 变量

## 路由约定

| 路由 | 页面 | 分类 |
|------|------|------|
| `/assets` | 资源工作台 | 保真迁移 |
| `/bank` | 音频工作台 | 保真迁移 |
| `/text` | 文本工作台 | 保真迁移 |
| `/static` | 静态数据工作台 | 保真迁移 |
| `/presets` | 预设卡片流 | 重设计 |
| `/export` | 导出向导 | 保真迁移 |
| `/settings` | 设置页 | 保真迁移 |
| `/help` | 帮助/教程 | 砍掉（内容并入 docs） |
| `/project` | 项目页 | 保真迁移 |
