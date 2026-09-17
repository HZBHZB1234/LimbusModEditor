// 验证接缝初始化（必须在 IPC 客户端之前）
import { initHarness } from './ipc/harness'
initHarness()

import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import './styles/tokens.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.mount('#app')
