import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // 产物可被 WebView2 虚拟主机 https://lme.app/ 加载
    // 资源引用使用相对路径，避免硬编码绝对路径
    assetsDir: 'assets',
    sourcemap: false,
  },
})
