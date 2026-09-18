/**
 * 全局状态 store —— 应用「当前在做什么 / 刚才发生了什么」的唯一事实来源。
 *
 * 三条来源：
 *  1) 宿主事件（契约 §4）：`progress`（已在后端实现）、`toast`、`state.scanDone`、
 *     `state.projectChanged`、`state.indexInvalid`、`session.hello`（后几个为契约已定义，
 *     宿主尚未推送时静默等待，不报错）。
 *  2) 页面主动登记：`track()` / `beginActivity()` —— 前端自己发起的耗时操作。
 *  3) IPC 在途请求数（client.onPendingChange），用于显示后台通信活动。
 *
 * 只读消费方：StatusBar.vue、PageHeader.vue、各视图的操作反馈。
 * 本 store 不发任何 IPC 请求，也不改变既有调用时序。
 */
import { defineStore } from 'pinia'
import { ipc } from '@/ipc'
import type { ProgressPayload } from '@/ipc'

/** 通知级别（与 tokens.css 的语义色一一对应） */
export type NoticeLevel = 'info' | 'success' | 'warning' | 'error'

/** 进行中的操作 */
export interface Activity {
  /** 稳定标识：宿主事件用 operationId，页面自登记用业务 id */
  id: string
  /** 中文动作名，例如「正在导出模组」 */
  label: string
  /** 阶段/明细说明（可选，来自进度事件的 message 或页面补充） */
  detail: string
  /** 已完成量（无进度时为 null） */
  current: number | null
  /** 总量（无进度时为 null） */
  total: number | null
  /** 开始时间戳 */
  startedAt: number
  /** 是否由宿主事件驱动（true 时不由页面 endActivity 结束） */
  fromHost: boolean
}

/** 一条通知 */
export interface Notice {
  id: number
  level: NoticeLevel
  message: string
  at: number
  /** 是否已读（状态栏红点用） */
  read: boolean
}

/** 宿主事件名（契约 §4） */
const EVENT_PROGRESS = 'progress'
const EVENT_TOAST = 'toast'
const EVENT_SCAN_DONE = 'state.scanDone'
const EVENT_PROJECT_CHANGED = 'state.projectChanged'
const EVENT_INDEX_INVALID = 'state.indexInvalid'
const EVENT_SESSION_HELLO = 'session.hello'

/** 通知历史上限（超出丢最旧） */
const MAX_NOTICES = 50
/** 操作超过此时长才值得出现在状态栏（避免闪烁） */
const ACTIVITY_VISIBLE_AFTER_MS = 250

let noticeSeq = 0

interface StatusState {
  /** IPC 桥接是否可用 */
  connected: boolean
  /** 在途 IPC 请求数 */
  pendingRequests: number
  /** 最近一次 IPC 方法名（调试用） */
  lastMethod: string
  /** 宿主程序版本（session.hello） */
  hostVersion: string
  /** 契约版本（session.hello） */
  contractVersion: string
  /** WebView2 运行时版本（session.hello） */
  runtimeVersion: string
  /** 当前项目名（空 = 未打开项目） */
  projectName: string
  /** 当前项目文件路径 */
  projectPath: string
  /** 索引是否已失效（宿主推 state.indexInvalid 后置位） */
  indexStale: boolean
  /** 最近一次扫描完成时间 */
  lastScanAt: number | null
  /** 进行中的操作 */
  activities: Activity[]
  /** 通知历史（新的在前） */
  notices: Notice[]
  /** 事件订阅是否已挂载 */
  attached: boolean
}

export const useStatusStore = defineStore('status', {
  state: (): StatusState => ({
    connected: false,
    pendingRequests: 0,
    lastMethod: '',
    hostVersion: '',
    contractVersion: '',
    runtimeVersion: '',
    projectName: '',
    projectPath: '',
    indexStale: false,
    lastScanAt: null,
    activities: [],
    notices: [],
    attached: false,
  }),

  getters: {
    /** 是否有操作正在进行 */
    isBusy: (state): boolean => state.activities.length > 0,

    /** 当前主操作（最近开始的） */
    activeActivity(state): Activity | null {
      return state.activities.length > 0 ? state.activities[state.activities.length - 1] : null
    },

    /** 主操作是否已经跑够久、值得显示（避免一闪而过） */
    showActivity(state): boolean {
      const a = state.activities[state.activities.length - 1]
      if (!a) return false
      // 有明确进度的立刻显示；无进度的等一会儿，避免闪烁
      if (a.total !== null) return true
      return Date.now() - a.startedAt >= ACTIVITY_VISIBLE_AFTER_MS
    },

    /** 未读通知数 */
    unreadCount: (state): number => state.notices.filter((n) => !n.read).length,

    /** 最近一条通知 */
    latestNotice(state): Notice | null {
      return state.notices.length > 0 ? state.notices[0] : null
    },

    /** 是否已打开项目 */
    hasProject: (state): boolean => state.projectName.length > 0,

    /** 状态栏用的健康度摘要 */
    healthSummary(state): { level: NoticeLevel; text: string } {
      if (!state.connected) return { level: 'warning', text: '宿主未连接' }
      if (state.indexStale) return { level: 'warning', text: '索引已失效' }
      const failed = state.notices.find((n) => n.level === 'error' && !n.read)
      if (failed) return { level: 'error', text: '最近有操作失败' }
      return { level: 'success', text: '运行正常' }
    },
  },

  actions: {
    /** 挂载宿主事件订阅（幂等） */
    attach(): void {
      if (this.attached) return
      this.attached = true
      this.connected = ipc.isConnected

      ipc.on(EVENT_PROGRESS, (payload) => {
        this.onProgress(payload as ProgressPayload)
      })
      ipc.on(EVENT_TOAST, (payload) => {
        const p = payload as { level?: NoticeLevel; message?: string }
        if (p?.message) this.notify(p.level ?? 'info', p.message)
      })
      ipc.on(EVENT_SCAN_DONE, () => {
        this.lastScanAt = Date.now()
        this.endActivity('scan')
        this.notify('success', '游戏资源扫描完成')
      })
      ipc.on(EVENT_PROJECT_CHANGED, () => {
        this.notify('info', '项目内容已变更')
      })
      ipc.on(EVENT_INDEX_INVALID, () => {
        this.indexStale = true
        this.notify('warning', '资源索引已失效，建议重新扫描')
      })
      ipc.on(EVENT_SESSION_HELLO, (payload) => {
        const p = payload as {
          contractVersion?: string
          appVersion?: string
          runtimeVersion?: string
        }
        this.contractVersion = p?.contractVersion ?? ''
        this.hostVersion = p?.appVersion ?? ''
        this.runtimeVersion = p?.runtimeVersion ?? ''
      })

      ipc.onPendingChange((count, method) => {
        this.pendingRequests = count
        if (method) this.lastMethod = method
      })
    },

    /** 进度事件 → 操作表 */
    onProgress(payload: ProgressPayload): void {
      if (!payload?.operationId) return
      const existing = this.activities.find((a) => a.id === payload.operationId)
      const hasTotal = typeof payload.total === 'number' && payload.total > 0
      if (existing) {
        existing.detail = payload.message || existing.detail
        existing.current = hasTotal ? payload.current : null
        existing.total = hasTotal ? payload.total : null
      } else {
        this.activities.push({
          id: payload.operationId,
          label: payload.message || '正在处理',
          detail: payload.message || '',
          current: hasTotal ? payload.current : null,
          total: hasTotal ? payload.total : null,
          startedAt: Date.now(),
          fromHost: true,
        })
      }
      // 进度达到总量视为结束（宿主末条事件必达）
      if (hasTotal && payload.current >= payload.total) {
        this.endActivity(payload.operationId)
      }
    },

    /** 登记一个进行中的操作 */
    beginActivity(id: string, label: string, detail = ''): void {
      const existing = this.activities.find((a) => a.id === id)
      if (existing) {
        existing.label = label
        existing.detail = detail
        return
      }
      this.activities.push({
        id,
        label,
        detail,
        current: null,
        total: null,
        startedAt: Date.now(),
        fromHost: false,
      })
    },

    /** 更新操作的进度/说明 */
    updateActivity(id: string, patch: Partial<Pick<Activity, 'label' | 'detail' | 'current' | 'total'>>): void {
      const a = this.activities.find((x) => x.id === id)
      if (!a) return
      if (patch.label !== undefined) a.label = patch.label
      if (patch.detail !== undefined) a.detail = patch.detail
      if (patch.current !== undefined) a.current = patch.current
      if (patch.total !== undefined) a.total = patch.total
    },

    /** 结束一个操作 */
    endActivity(id: string): void {
      const i = this.activities.findIndex((a) => a.id === id)
      if (i >= 0) this.activities.splice(i, 1)
    },

    /**
     * 包住一段异步操作：自动登记/结束，成功失败都发通知。
     * 页面里最常用的入口。
     *
     * @param id     操作 id（同一 id 并发调用会复用同一条活动）
     * @param label  动作名（进行时，如「正在导出模组」）
     * @param fn     实际操作
     * @param opts.successText 成功提示；传 false 表示不提示
     * @param opts.errorPrefix 失败提示前缀
     */
    async track<T>(
      id: string,
      label: string,
      fn: () => Promise<T>,
      opts: { successText?: string | false; errorPrefix?: string } = {},
    ): Promise<T> {
      this.beginActivity(id, label)
      try {
        const result = await fn()
        if (opts.successText !== false) {
          this.notify('success', opts.successText || `${label.replace(/^正在/, '')}完成`)
        }
        return result
      } catch (e: unknown) {
        const msg = e instanceof Error ? e.message : String(e)
        this.notify('error', `${opts.errorPrefix ?? label.replace(/^正在/, '')}失败：${msg}`)
        throw e
      } finally {
        this.endActivity(id)
      }
    },

    /** 记一条通知 */
    notify(level: NoticeLevel, message: string): void {
      this.notices.unshift({
        id: ++noticeSeq,
        level,
        message,
        at: Date.now(),
        read: false,
      })
      if (this.notices.length > MAX_NOTICES) this.notices.length = MAX_NOTICES
    },

    /** 全部标记已读 */
    markAllRead(): void {
      for (const n of this.notices) n.read = true
    },

    /** 清空通知历史 */
    clearNotices(): void {
      this.notices = []
    },

    /** 设置当前项目信息（由项目页 / 启动流程调用） */
    setProject(name: string, path: string): void {
      this.projectName = name
      this.projectPath = path
    },

    /** 清除当前项目信息 */
    clearProject(): void {
      this.projectName = ''
      this.projectPath = ''
    },
  },
})
