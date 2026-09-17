/**
 * 验证用 Stub Bridge —— 与 IpcClient 协议完全对齐
 *
 * 协议说明：
 * - 请求：{ id: "req-N", kind: "request", method: string, payload: unknown }
 * - 响应：{ id: "req-N", kind: "response", ok: true, payload: unknown }
 *         或 { id: "req-N", kind: "response", ok: false, error: { code, message } }
 * - 事件：{ kind: "event", method: string, payload: unknown }
 *
 * 使用方式：
 * 1. 创建 stub 桥：const bridge = createStubBridge(fixtures)
 * 2. 注入到 window.__lmeIpc
 * 3. 页面刷新后重新注入即可
 */

import type { IpcErrorCode } from './types'

type MessageHandler = (event: { data: string }) => void

export interface StubFixture {
  method: string
  payloadMatcher?: (payload: unknown) => boolean
  response: unknown
  error?: { code: IpcErrorCode; message: string }
  delay?: number
}

export interface StubBridge {
  postMessage: (message: string) => void
  addEventListener: (type: string, handler: MessageHandler) => void
  removeEventListener: (type: string, handler: MessageHandler) => void
  __messageHandler?: (data: string) => void
  __pendingEvents: Array<{ method: string; payload: unknown }>
  __pushEvent: (method: string, payload: unknown) => void
}

export function createStubBridge(
  fixtures: StubFixture[],
  options: { defaultDelay?: number; verbose?: boolean } = {},
): StubBridge {
  const { defaultDelay = 10, verbose = false } = options
  const messageHandlers: MessageHandler[] = []
  const pendingEvents: Array<{ method: string; payload: unknown }> = []

  function addEventListener(_type: string, handler: MessageHandler) {
    messageHandlers.push(handler)
  }

  function removeEventListener(_type: string, handler: MessageHandler) {
    const idx = messageHandlers.indexOf(handler)
    if (idx >= 0) messageHandlers.splice(idx, 1)
  }

  function emit(event: { data: string }) {
    for (const handler of messageHandlers) {
      handler(event)
    }
  }

  function postMessage(message: string) {
    try {
      const req = JSON.parse(message)
      const id = req.id as string
      const method = req.method as string
      const payload = req.payload

      if (verbose) {
        console.log('[StubBridge] 收到请求:', method, payload)
      }

      const fixture = fixtures.find((f) => {
        if (f.method !== method) return false
        if (f.payloadMatcher && !f.payloadMatcher(payload)) return false
        return true
      })

      const delay = fixture?.delay ?? defaultDelay

      setTimeout(() => {
        if (fixture?.error) {
          emit({
            data: JSON.stringify({
              id,
              kind: 'response',
              ok: false,
              error: fixture.error,
            }),
          })
        } else {
          emit({
            data: JSON.stringify({
              id,
              kind: 'response',
              ok: true,
              payload: fixture?.response ?? null,
            }),
          })
        }
      }, delay)
    } catch (e) {
      console.error('[StubBridge] 解析请求失败:', e)
    }
  }

  function pushEvent(method: string, payload: unknown) {
    emit({
      data: JSON.stringify({ kind: 'event', method, payload }),
    })
  }

  return {
    postMessage,
    addEventListener,
    removeEventListener,
    __messageHandler: undefined,
    __pendingEvents: pendingEvents,
    __pushEvent: pushEvent,
  }
}

export async function loadFixturesFromUrl(url: string): Promise<StubFixture[]> {
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`加载夹具失败: ${url} (${response.status})`)
  }
  return response.json()
}

export function createDefaultWikiFixtures(): StubFixture[] {
  return [
    {
      // 方法名已收敛为 wiki.getPage（wiki.page.load 是历史别名）
      method: 'wiki.getPage',
      response: {
        id: 'default',
        title: '示例页面',
        category: 'persona',
        sections: [
          { id: 'overview', title: '概述', content: '这是一个示例页面。', bindings: [] },
        ],
        toc: [{ id: 'overview', title: '概述', level: 1 }],
      },
    },
    {
      method: 'wiki.search',
      response: { total: 0, results: [] },
    },
    {
      method: 'wiki.category.load',
      response: { category: 'persona', pages: [] },
    },
    {
      method: 'wiki.getEditPlan',
      response: { editable: true, fields: [] },
    },
    {
      method: 'wiki.applyEdit',
      response: { ok: true },
    },
  ]
}
