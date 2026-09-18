/**
 * 启动流程 store —— 软件启动时的三步编排，全局唯一入口。
 *
 * ```
 * ① 路径定位   config.autoDetect       游戏 / 缓存 / 模组 / FMOD 四个目录
 * ② 强制项目   project.create|open     必须新建或打开一个项目，否则不放行
 * ③ 自动扫描   scan.run(scope=all)     把游戏资源登记进当前项目（含四个工作台索引）
 * ```
 *
 * 为什么扫描排在项目之后：`scan.run` 的契约前提就是「必须有当前项目」
 * （网关在没有项目时直接回 `invalid-query` 中文错误），所以「自动扫描」只能在
 * 项目确定之后自动触发，不能在项目之前。
 *
 * 本 store 只调用**既有** IPC 方法（`config.autoDetect` / `config.read` /
 * `project.recent` / `project.create` / `project.open` / `dialog.openFile` /
 * `scan.run` / `cancel`），不新增方法名、不改载荷字段、不改既有页面的调用时序。
 *
 * 只读消费方：`components/StartupFlow.vue`（两个模态窗）、`App.vue`（工作区锁定）。
 */
import { defineStore } from 'pinia'
import { ipc, IpcClientError } from '@/ipc'
import type { ProgressPayload } from '@/ipc'
import { useStatusStore } from '@/stores/status'

/** 扫描用的操作 id（与 ProjectView 的「重新扫描游戏资源」同一条活动，状态栏不会出现两条） */
export const SCAN_OPERATION_ID = 'scan'

/** 扫描超时：首次冷扫描要枚举全部缓存 bundle，给足 15 分钟 */
const SCAN_TIMEOUT_MS = 900_000

/** 扫描成功后的自动收起延时（给用户看清结果的时间） */
const SCAN_AUTO_CLOSE_MS = 2500

/** 启动阶段：idle → locating → project → scanning → done */
export type StartupPhase = 'idle' | 'locating' | 'project' | 'scanning' | 'done'

/** 路径定位结果（`config.autoDetect` 的返回；探不到的项为 null，不猜路径） */
export interface StartupPaths {
  gameDirectory: string | null
  unityCacheDirectory: string | null
  modDirectory: string | null
  fmodLibraryDirectory: string | null
  /** 后端给的中文说明 */
  info: string
  /** 探测本身失败（与「探测成功但没找到」区分开） */
  error: string
}

/** 扫描响应里的一个步骤（对应后端 `ScanStepDto`） */
export interface StartupScanStep {
  key: string
  label: string
  status: string
  detail: string
  elapsedSeconds: number
}

/** 扫描完成摘要（成功路径才填） */
export interface StartupScanSummary {
  status: string
  detail: string
  bundleCount: number
  assetCount: number
  elapsedSeconds: number
  cacheDirectory: string
  info: string
}

/** 最近项目一行 */
export interface StartupRecentProject {
  name: string
  path: string
  lastOpened: string
}

/**
 * 扫描步骤清单（与后端 `StartupScanService` 的六个步骤一一对应）。
 * `match` 用于把进度事件的阶段名（如「扫描游戏资源」）落到对应步骤行上。
 */
export const SCAN_STEPS: { key: string; label: string; match: string }[] = [
  { key: 'cache-databases', label: '缓存库', match: '缓存库' },
  { key: 'unity-assets', label: '游戏资源', match: '游戏资源' },
  { key: 'bank-index', label: '音频索引', match: '音频索引' },
  { key: 'static-tables', label: '静态数据表', match: '静态数据表' },
  { key: 'text-index', label: 'lang 文本索引', match: '文本' },
  { key: 'relations', label: '资源关联', match: '关联' },
]

interface StartupState {
  /** 当前阶段 */
  phase: StartupPhase
  /** 路径定位结果 */
  paths: StartupPaths
  /** 最近项目（project.recent，已过滤掉文件不存在的记录） */
  recentProjects: StartupRecentProject[]
  recentInfo: string
  /** 上次使用的项目文件（config.read('lastProjectFile')；只读展示，点「继续」才打开） */
  lastProjectFile: string

  /** 项目选择/打开是否进行中（门组件按钮 loading 用） */
  projectBusy: boolean

  /** 当前项目 */
  projectName: string
  projectPath: string

  /** 扫描进行中 */
  scanRunning: boolean
  /** 扫描进度：当前阶段名（进度事件的 phase） */
  scanPhase: string
  /** 扫描进度：明细（进度事件的 message） */
  scanDetail: string
  /** 扫描进度：已收到的进度事件条数（用于「仍在活动」的观感） */
  scanTicks: number
  /** 扫描开始时间戳（算耗时用） */
  scanStartedAt: number
  /** 扫描完成后的步骤结果 */
  scanSteps: StartupScanStep[]
  /** 扫描完成摘要（成功时非空） */
  scanSummary: StartupScanSummary | null
  /** 扫描失败原因（中文） */
  scanError: string
  /** 扫描被用户取消 */
  scanCancelled: boolean
}

export const useStartupStore = defineStore('startup', {
  state: (): StartupState => ({
    phase: 'idle',
    paths: {
      gameDirectory: null,
      unityCacheDirectory: null,
      modDirectory: null,
      fmodLibraryDirectory: null,
      info: '',
      error: '',
    },
    recentProjects: [],
    recentInfo: '',
    lastProjectFile: '',

    projectBusy: false,

    projectName: '',
    projectPath: '',

    scanRunning: false,
    scanPhase: '',
    scanDetail: '',
    scanTicks: 0,
    scanStartedAt: 0,
    scanSteps: [],
    scanSummary: null,
    scanError: '',
    scanCancelled: false,
  }),

  getters: {
    /** 启动流程未走完 → 工作区必须锁定（这是「强制」的落点） */
    blocking: (state): boolean => state.phase !== 'done',
    /** 项目门是否打开（不可关闭） */
    gateOpen: (state): boolean => state.phase === 'project',
    /** 扫描模态是否打开 */
    scanOpen: (state): boolean => state.phase === 'scanning',

    /** 定位到的目录数（门组件的概览用） */
    locatedCount: (state): number =>
      [
        state.paths.gameDirectory,
        state.paths.unityCacheDirectory,
        state.paths.modDirectory,
        state.paths.fmodLibraryDirectory,
      ].filter((p) => !!p).length,

    /** 缓存目录是否定位到（没定位到 = 扫描必然跳过，要提前告知） */
    hasCacheDirectory: (state): boolean => !!state.paths.unityCacheDirectory,

    /** 当前正在跑的步骤 key（进度阶段名 → 步骤行；无命中时为 ''） */
    activeStepKey(state): string {
      if (!state.scanRunning) return ''
      const phase = state.scanPhase
      if (!phase) return ''
      return SCAN_STEPS.find((s) => phase.includes(s.match))?.key ?? ''
    },

    /** 步骤行：后端返回的结果优先，未返回的按「进行中/待执行」占位 */
    stepRows(state): { key: string; label: string; status: string; detail: string; elapsedSeconds: number }[] {
      return SCAN_STEPS.map((plan) => {
        const done = state.scanSteps.find((s) => s.key === plan.key)
        if (done) {
          return {
            key: plan.key,
            label: done.label || plan.label,
            status: done.status,
            detail: done.detail,
            elapsedSeconds: done.elapsedSeconds,
          }
        }
        return {
          key: plan.key,
          label: plan.label,
          status: state.scanRunning ? 'Pending' : 'Unknown',
          detail: '',
          elapsedSeconds: 0,
        }
      })
    },
  },

  actions: {
    /**
     * 启动流程入口（幂等）：定位路径 → 打开项目门。
     * 后续两步由用户在门里选完项目后自动接续（见 {@link activateProject}）。
     */
    async start(): Promise<void> {
      if (this.phase !== 'idle') return
      this.phase = 'locating'
      useStatusStore().attach()
      await Promise.all([this.locatePaths(), this.loadRecentProjects()])
      this.phase = 'project'
    },

    /** ① 路径定位：config.autoDetect（探不到的项为 null，不猜路径） */
    async locatePaths(): Promise<void> {
      this.paths.error = ''
      try {
        const res = await ipc.request<{
          gameDirectory?: string | null
          unityCacheDirectory?: string | null
          modDirectory?: string | null
          fmodLibraryDirectory?: string | null
          info?: string
        }>('config.autoDetect', {})
        this.paths.gameDirectory = res?.gameDirectory ?? null
        this.paths.unityCacheDirectory = res?.unityCacheDirectory ?? null
        this.paths.modDirectory = res?.modDirectory ?? null
        this.paths.fmodLibraryDirectory = res?.fmodLibraryDirectory ?? null
        this.paths.info = res?.info ?? ''
      } catch (e: unknown) {
        this.paths.error = e instanceof Error ? e.message : String(e)
      }
    },

    /** 最近项目 + 上次使用的项目（都是只读展示，不自动打开） */
    async loadRecentProjects(): Promise<void> {
      try {
        const res = await ipc.request<{ projects?: StartupRecentProject[]; info?: string }>(
          'project.recent',
          {},
        )
        this.recentProjects = res?.projects ?? []
        this.recentInfo = res?.info ?? ''
      } catch {
        this.recentProjects = []
        this.recentInfo = ''
      }
      try {
        const res = await ipc.request<{ value?: string }>('config.read', { key: 'lastProjectFile' })
        this.lastProjectFile = res?.value ?? ''
      } catch {
        this.lastProjectFile = ''
      }
    },

    /** 新建项目（project.create）→ 成功即接上自动扫描 */
    async createProject(name: string): Promise<void> {
      const trimmed = name.trim()
      if (!trimmed) throw new Error('请先填写模组名称')
      this.projectBusy = true
      try {
        const res = await ipc.request<{ path: string; name: string; directory: string }>(
          'project.create',
          { name: trimmed },
        )
        this.activateProject(res?.path ?? '', res?.name || trimmed)
      } finally {
        this.projectBusy = false
      }
    },

    /** 打开项目文件（project.open）→ 成功即接上自动扫描 */
    async openProject(projectFile: string): Promise<void> {
      if (!projectFile) throw new Error('没有可打开的项目文件')
      this.projectBusy = true
      try {
        const res = await ipc.request<{ ok: boolean; name: string; assetCount: number }>(
          'project.open',
          { projectFile },
        )
        this.activateProject(projectFile, res?.name || basename(projectFile))
      } finally {
        this.projectBusy = false
      }
    },

    /** 弹原生文件对话框选项目（dialog.openFile）→ 选中就直接打开；取消则什么都不做 */
    async pickProjectFile(): Promise<void> {
      const res = await ipc.request<{ path?: string }>('dialog.openFile', {
        title: '打开项目',
        filters: 'Limbus 模组项目 (*.lmeproj)|*.lmeproj|所有文件 (*.*)|*.*',
      })
      if (!res?.path) return
      await this.openProject(res.path)
    },

    /** 项目就绪：记进全局状态 → 关掉项目门 → 自动开始扫描 */
    activateProject(projectFile: string, name: string): void {
      this.projectName = name
      this.projectPath = projectFile
      useStatusStore().setProject(name, projectFile)
      this.phase = 'scanning'
      void this.rescanWithProjectContext()
    },

    /**
     * 带着项目再定位一次路径，然后自动扫描。
     *
     * 为什么再定位一次：`config.autoDetect` 的入参是「当前项目」，只有在项目就绪后
     * 才会把老 `.lmeproj` 里的目录值迁进共享配置的空位（只填空位，手动值永不覆盖），
     * 并补上第一轮（无项目时）探不到的项。扫描要按生效目录跑，所以这一步排在它前面。
     */
    async rescanWithProjectContext(): Promise<void> {
      await this.locatePaths()
      await this.runScan()
    },

    /** ③ 自动扫描：scan.run(scope=all)，进度走 progress 事件（状态栏 + 模态窗共用） */
    async runScan(): Promise<void> {
      if (this.scanRunning) return
      const status = useStatusStore()
      this.scanRunning = true
      this.scanError = ''
      this.scanCancelled = false
      this.scanSummary = null
      this.scanSteps = []
      this.scanPhase = '准备扫描'
      this.scanDetail = ''
      this.scanTicks = 0
      this.scanStartedAt = Date.now()
      status.beginActivity(SCAN_OPERATION_ID, '正在扫描游戏资源', '准备中')

      const off = ipc.on('progress', (payload) => {
        const p = payload as ProgressPayload
        if (!p || p.operationId !== SCAN_OPERATION_ID) return
        this.scanPhase = p.phase || this.scanPhase
        this.scanDetail = p.message || ''
        this.scanTicks += 1
      })

      try {
        const res = await ipc.request<{
          scope: string
          status: string
          detail: string
          bundleCount: number
          assetCount: number
          elapsedSeconds: number
          cacheDirectory: string
          steps?: StartupScanStep[]
          info?: string
        }>('scan.run', { scope: 'all', operationId: SCAN_OPERATION_ID }, SCAN_TIMEOUT_MS)

        this.scanSteps = res?.steps ?? []
        this.scanSummary = {
          status: res?.status ?? '',
          detail: res?.detail ?? '',
          bundleCount: res?.bundleCount ?? 0,
          assetCount: res?.assetCount ?? 0,
          elapsedSeconds: res?.elapsedSeconds ?? 0,
          cacheDirectory: res?.cacheDirectory ?? '',
          info: res?.info ?? '',
        }
        this.scanPhase = '扫描完成'
        this.scanDetail = this.scanSummary.detail
        status.notify(
          'success',
          `扫描完成：${this.scanSummary.assetCount} 条资源 / ${this.scanSummary.bundleCount} 个 bundle`,
        )
        // 成功后自动收起（用户也可点「进入工作台」立即收起）
        window.setTimeout(() => this.enterWorkspace(), SCAN_AUTO_CLOSE_MS)
      } catch (e: unknown) {
        if (e instanceof IpcClientError && e.code === 'cancelled') {
          // 用户主动取消：不是错误，直接放行（半成品索引由下一次扫描自愈）
          this.scanCancelled = true
          this.scanPhase = '扫描已取消'
          this.scanDetail = '本次启动扫描已取消，索引可能不完整；可稍后在「项目」页重新扫描'
          status.notify('warning', '启动扫描已取消')
          this.enterWorkspace()
        } else {
          this.scanError = e instanceof Error ? e.message : String(e)
          this.scanPhase = '扫描失败'
          this.scanDetail = this.scanError
          status.notify('error', `启动扫描失败：${this.scanError}`)
        }
      } finally {
        off()
        this.scanRunning = false
        status.endActivity(SCAN_OPERATION_ID)
      }
    },

    /** 取消扫描（协作式取消，契约 §3.2） */
    cancelScan(): void {
      if (!this.scanRunning) return
      ipc.cancel(SCAN_OPERATION_ID)
    },

    /** 重试扫描（失败后） */
    retryScan(): void {
      void this.runScan()
    },

    /** 跳过扫描，直接进工作台（失败/无缓存目录时的出路，不让用户卡死在模态窗里） */
    skipScan(): void {
      if (this.scanRunning) return
      useStatusStore().notify('warning', '已跳过启动扫描；索引可能不完整，可在「项目」页重新扫描')
      this.enterWorkspace()
    },

    /** 放行：关闭启动流程，工作区解锁 */
    enterWorkspace(): void {
      if (this.phase === 'done') return
      this.phase = 'done'
    },
  },
})

/** 取路径末段（项目名兜底） */
function basename(path: string): string {
  const parts = path.split(/[\\/]/)
  const last = parts[parts.length - 1] || path
  return last.replace(/\.lmeproj$/i, '')
}
