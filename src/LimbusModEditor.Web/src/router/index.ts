import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  // WebView2 虚拟主机下用 history 模式，导航到 https://lme.app/index.html
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      redirect: '/assets',
    },
    {
      path: '/assets',
      name: 'assets',
      component: () => import('@/views/AssetsView.vue'),
    },
  ],
})

export default router
