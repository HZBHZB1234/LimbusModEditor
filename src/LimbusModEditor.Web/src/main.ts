// 外观偏好必须在挂载前落到 <html>，否则首帧会闪默认色（见 theme/preferences.ts）
import { bootstrapAppearance } from './theme/preferences'
bootstrapAppearance()

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
