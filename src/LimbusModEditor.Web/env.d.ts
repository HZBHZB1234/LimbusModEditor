/// <reference types="vite/client" />

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<{}, {}, any>
  export default component
}

// WebView2 宿主桥接对象
interface Window {
  chrome?: {
    webview?: {
      postMessage: (message: string) => void
      addEventListener: (
        type: 'message',
        listener: (event: { data: string }) => void,
      ) => void
      removeEventListener: (
        type: 'message',
        listener: (event: { data: string }) => void,
      ) => void
    }
  }
}
