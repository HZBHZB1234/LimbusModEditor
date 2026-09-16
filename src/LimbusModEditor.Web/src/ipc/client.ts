// IPC 客户端：类型化封装契约消息（请求/响应/事件、取消、错误）
// 依赖契约 docs/WEB-IPC-CONTRACT.md，不含任何 WPF/原生细节

import type {
  IpcRequest,
  IpcResponse,
  IpcError,
  IpcEvent,
  IpcMessage,
  IpcErrorCode,
} from './types'

type PendingReq = {
  resolve: (payload: unknown) => void
  reject: (err: IpcClientError) => void
  method: string
  createdAt: number
}

export class IpcClientError extends Error {
  constructor(
    public readonly code: IpcErrorCode,
    message: string,
  ) {
    super(message)
    this.name = 'IpcClientError'
  }
}

export class IpcClient {
  private seq = 0
  private pending = new Map<string, PendingReq>()
  private listeners = new Map<string, Set<(payload: unknown) => void>>()
  private connected = false
  private messageHandler: ((event: { data: string }) => void) | null = null

  constructor() {
    this.attachHost()
  }

  private attachHost(): void {
    const w = window as unknown as {
      chrome?: {
        webview?: {
          addEventListener?: (t: string, l: (e: { data: string }) => void) => void
          removeEventListener?: (t: string, l: (e: { data: string }) => void) => void
        }
      }
    }
    const webview = w.chrome?.webview
    if (!webview?.addEventListener) {
      // 非 WebView2 环境（开发/测试）：降级为 no-op
      console.warn('[IPC] WebView2 桥接不可用，进入离线降级模式')
      return
    }
    // 保存引用以便 destroy 时退订（F-04 fix: 防止监听泄漏）
    this.messageHandler = (event: { data: string }) => {
      try {
        const msg = JSON.parse(event.data) as IpcMessage
        this.dispatch(msg)
      } catch (e) {
        console.error('[IPC] 消息解析失败', e)
      }
    }
    webview.addEventListener('message', this.messageHandler)
    this.connected = true
  }

  /** 销毁客户端，退订所有事件监听（F-04 fix） */
  destroy(): void {
    if (this.messageHandler) {
      const w = window as unknown as {
        chrome?: { webview?: { removeEventListener?: (t: string, l: (e: { data: string }) => void) => void } }
      }
      w.chrome?.webview?.removeEventListener?.('message', this.messageHandler)
      this.messageHandler = null
    }
    this.listeners.clear()
    this.pending.clear()
    this.connected = false
  }

  private dispatch(msg: IpcMessage): void {
    if (msg.kind === 'response') {
      const pending = this.pending.get(msg.id)
      if (!pending) return
      this.pending.delete(msg.id)
      if (msg.ok) {
        pending.resolve(msg.payload)
      } else {
        pending.reject(new IpcClientError(msg.error.code, msg.error.message))
      }
    } else if (msg.kind === 'event') {
      const handlers = this.listeners.get(msg.method)
      if (handlers) {
        for (const h of handlers) h(msg.payload)
      }
    }
    // 忽略未知 kind（契约 §7：不认识的必须忽略并记日志）
  }

  /**
   * 发送类型化请求，返回 Promise。
   * method: 契约方法名（如 'catalog.query'）
   * payload: 契约载荷
   * timeoutMs: 超时（毫秒），默认 30000
   */
  request<TResponse>(
    method: string,
    payload: unknown,
    timeoutMs = 30000,
  ): Promise<TResponse> {
    return new Promise<TResponse>((resolve, reject) => {
      const id = `req-${++this.seq}`
      const req: IpcRequest = { id, kind: 'request', method, payload }

      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new IpcClientError('internal', `请求超时: ${method}`))
      }, timeoutMs)

      this.pending.set(id, {
        resolve: (p) => {
          clearTimeout(timer)
          resolve(p as TResponse)
        },
        reject: (err) => {
          clearTimeout(timer)
          reject(err)
        },
        method,
        createdAt: Date.now(),
      })

      this.post(req)
    })
  }

  /** 发送取消请求（契约 §3.2） */
  cancel(operationId: string): void {
    const id = `req-${++this.seq}`
    const req: IpcRequest = {
      id,
      kind: 'request',
      method: 'cancel',
      payload: { operationId },
    }
    this.post(req)
  }

  /** 订阅事件 */
  on(method: string, handler: (payload: unknown) => void): () => void {
    let set = this.listeners.get(method)
    if (!set) {
      set = new Set()
      this.listeners.set(method, set)
    }
    set.add(handler)
    return () => set!.delete(handler)
  }

  private post(msg: IpcRequest): void {
    const w = window as unknown as {
      chrome?: { webview?: { postMessage?: (m: string) => void } }
    }
    const webview = w.chrome?.webview
    if (!webview?.postMessage) {
      console.warn('[IPC] 无法发送消息（桥接不可用）:', msg.method)
      return
    }
    webview.postMessage(JSON.stringify(msg))
  }

  get isConnected(): boolean {
    return this.connected
  }
}

// 单例
export const ipc = new IpcClient()
