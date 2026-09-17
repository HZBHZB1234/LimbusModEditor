/**
 * 验证专用接缝（Verification Harness）
 *
 * 用途：在普通 Chromium 浏览器中注入桩桥，驱动 wiki.* 页面渲染真实数据。
 * 仅用于自动化验证，生产环境完全不受影响。
 *
 * 使用方式：
 * 1. 在 URL 后添加 ?harness=1 参数
 * 2. 在页面加载前注入桩桥（见下方示例）
 * 3. 桩桥收到请求后，调用 window.__lmeIpc.__messageHandler({ data: jsonString }) 模拟响应
 *
 * 示例（Playwright）：
 * await page.addInitScript(() => {
 *   window.__lmeIpc = {
 *     postMessage: (msg) => {
 *       const req = JSON.parse(msg);
 *       const res = { id: req.id, kind: 'response', ok: true, payload: {} };
 *       setTimeout(() => window.__lmeIpc.__messageHandler?.({ data: JSON.stringify(res) }), 10);
 *     },
 *     addEventListener: (t, h) => { window.__lmeIpc.__messageHandler = h; },
 *     removeEventListener: () => {},
 *   };
 * });
 */

/**
 * 检查当前是否处于验证模式
 */
export function isHarnessMode(): boolean {
  try {
    const params = new URLSearchParams(window.location.search)
    return params.get('harness') === '1'
  } catch {
    return false
  }
}

/**
 * 获取验证桥（如果存在）
 * 仅在 ?harness=1 模式下返回桥对象
 *
 * 注意：每次调用都重新读取 window.__lmeIpc，
 * 这样测试 harness 在刷新后重新注入桥时，IPC 客户端能感知到新的桥。
 */
export function getHarnessBridge(): NonNullable<Window['__lmeIpc']> | null {
  if (!isHarnessMode()) return null
  
  // 每次都读取最新的桥，支持刷新后重新注入
  const bridge = window.__lmeIpc
  if (!bridge) return null
  
  return bridge
}

/**
 * 初始化验证接缝
 * 应在应用启动时调用（在 IPC 客户端初始化之前）
 *
 * 注意：空壳桥的 addEventListener 必须保存 handler，
 * 否则 IpcClient 注册的消息处理器会丢失，导致响应无法回到 pending 请求。
 */
export function initHarness(): void {
  if (!isHarnessMode()) return

  // 暴露全局接口供验证工具注入
  if (!window.__lmeIpc) {
    const bridge: Window['__lmeIpc'] = {
      postMessage: (msg: string) => {
        console.warn('[Harness] 桩桥未注入，请求被忽略:', msg)
      },
      addEventListener: (_type: string, handler: (event: { data: string }) => void) => {
        // 保存 handler，使桩桥响应能回到 IpcClient 的 pending 请求
        if (bridge) {
          bridge.__messageHandler = handler
        }
      },
      removeEventListener: () => {
        if (bridge) {
          bridge.__messageHandler = undefined
        }
      },
      __messageHandler: undefined,
    }
    window.__lmeIpc = bridge
  }

  console.info('[Harness] 验证接缝已启用（?harness=1）')
}
