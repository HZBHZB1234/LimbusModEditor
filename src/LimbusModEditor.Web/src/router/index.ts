import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  // WebView2 虚拟主机下用 history 模式，导航到 https://lme.app/index.html
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/assets' },
    {
      path: '/assets',
      name: 'assets',
      component: () => import('@/views/AssetsView.vue'),
    },
    {
      path: '/bank',
      name: 'bank',
      component: () => import('@/views/BankView.vue'),
    },
    {
      path: '/text',
      name: 'text',
      component: () => import('@/views/TextView.vue'),
    },
    {
      path: '/static',
      name: 'static',
      component: () => import('@/views/StaticView.vue'),
    },
    {
      path: '/export',
      name: 'export',
      component: () => import('@/views/ExportView.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
    },
    {
      path: '/help',
      name: 'help',
      component: () => import('@/views/HelpView.vue'),
    },
    {
      path: '/project',
      name: 'project',
      component: () => import('@/views/ProjectView.vue'),
    },
    // ── 维基页面 ──
    {
      path: '/wiki',
      name: 'wiki-home',
      component: () => import('@/views/WikiHomeView.vue'),
    },
    {
      path: '/wiki/category/:category',
      name: 'wiki-category',
      component: () => import('@/views/WikiCategoryView.vue'),
      props: true,
    },
    {
      path: '/wiki/page/:id',
      name: 'wiki-page',
      component: () => import('@/views/WikiEntityPage.vue'),
      props: true,
    },
    {
      path: '/wiki/search',
      name: 'wiki-search',
      component: () => import('@/views/WikiSearchView.vue'),
    },
    // ── 组件库自检（不进工作区 tab，仅用于验证主题映射是否正确） ──
    {
      path: '/lib-check',
      name: 'lib-check',
      component: () => import('@/views/LibCheckView.vue'),
    },
    // 组件库自检页（不暴露在工作区 tab 中，仅供主题/组件验证）
    {
      path: '/lib-check',
      name: 'lib-check',
      component: () => import('@/views/LibCheckView.vue'),
    },
  ],
})

export default router
