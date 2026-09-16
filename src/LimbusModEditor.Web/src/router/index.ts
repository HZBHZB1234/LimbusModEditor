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
      path: '/presets',
      name: 'presets',
      component: () => import('@/views/PresetsView.vue'),
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
  ],
})

export default router
