// IPC 契约 DTO —— 与 docs/WEB-IPC-CONTRACT.md 完全对齐
// 仅做本地类型化；真实 DTO 定义在 Application/Ipc（铁律 §3-10）

// ── 资产类型 ──────────────────────────────────────────────
export type AssetType =
  | 'Unknown'
  | 'Texture'
  | 'Sprite'
  | 'Audio'
  | 'Text'
  | 'Json'
  | 'MonoBehaviour'
  | 'MonoScript'
  | 'ScriptableObject'
  | 'Mesh'
  | 'Animation'
  | 'Font'
  | 'Binary'
  | 'GameObject'
  | 'Component'
  | 'Material'
  | 'Shader'
  | 'Video'
  | 'SpriteAtlas'

export type AssetEditState =
  | 'Unchanged'
  | 'Modified'
  | 'Added'
  | 'Deleted'
  | 'Conflict'
  | 'Invalid'

export type AssetSortKind =
  | 'Name'
  | 'SizeDescending'
  | 'SizeAscending'
  | 'Type'
  | 'ModifiedFirst'

// ── 搜索判据 ──────────────────────────────────────────────
export interface AssetSearchQuery {
  text?: string
  type?: AssetType
  state?: AssetEditState
  container?: string
  unityPathId?: number
  unityTypeId?: number
  minSize?: number
  maxSize?: number
  hasReplacement?: boolean
  sort?: AssetSortKind
  hasContainerEntry?: boolean
  showStaticTables?: boolean
}

// ── 资产记录 ──────────────────────────────────────────────
export interface AssetRecord {
  assetId: string
  logicalPath: string
  sourcePath?: string
  containerPath?: string
  account?: string
  bundle?: string
  unityPathId?: number
  unityTypeId?: number
  type: AssetType
  originalHash?: string
  modifiedHash?: string
  editState: AssetEditState
  size: number
  metadata: Record<string, string>
}

// ── 分页结果 ──────────────────────────────────────────────
export interface AssetCatalogPage {
  items: AssetRecord[]
  totalCount: number
  offset: number
  take: number
}

// ── 预览 ──────────────────────────────────────────────────
export type AssetPreviewKind =
  | 'Image'
  | 'SpriteComposite'
  | 'Audio'
  | 'Text'
  | 'JsonFields'
  | 'Hex'
  | 'Material'
  | 'Shader'
  | 'Video'
  | 'Atlas'
  | 'Summary'
  | 'None'

export interface PropertyRow {
  label: string
  value: string
}

export interface AssetPreviewResult {
  kind: AssetPreviewKind
  rows: PropertyRow[]
  binaryUrl: string | null
}

// ── 关联资源 ──────────────────────────────────────────────
export interface RelationSubject {
  subjectId: string
  displayName: string
  category: string
  categoryLabel: string
  subtitle: string
  character: string
  kind: string
  kindLabel: string
  display: string
  detail: string | null
  pageId: string | null
}

export interface RelationLink {
  assetId: string
  previewText: string
  previewKind: AssetPreviewKind
  deepLink: string
}

// ── 容器树 ────────────────────────────────────────────────
export interface ContainerNode {
  name: string
  path: string
  isLeaf: boolean
  childCount: number
  assetIds: string[]
}

// ── IPC 信封 ──────────────────────────────────────────────
export interface IpcRequest {
  id: string
  kind: 'request'
  method: string
  payload: unknown
}

export interface IpcResponse {
  id: string
  kind: 'response'
  ok: true
  payload: unknown
}

export interface IpcError {
  id: string
  kind: 'response'
  ok: false
  error: { code: IpcErrorCode; message: string }
}

export interface IpcEvent {
  kind: 'event'
  method: string
  payload: unknown
}

export type IpcMessage = IpcRequest | IpcResponse | IpcError | IpcEvent

// ── 错误码 ────────────────────────────────────────────────
export type IpcErrorCode =
  | 'cancelled'
  | 'not-found'
  | 'invalid-query'
  | 'io-error'
  | 'unsupported'
  | 'internal'

// ── 进度事件 ──────────────────────────────────────────────
export interface ProgressPayload {
  operationId: string
  phase: string
  current: number
  total: number
  message: string
}

// ── 维基页面类型 ──────────────────────────────────────────
export type WikiPageCategory =
  | 'persona'
  | 'enemy'
  | 'abnormality'
  | 'ego'
  | 'ego_gift'
  | 'announcer'
  | 'story'
  | 'stage'
  | 'item'
  | 'mechanism'
  | 'keyword'

export const WikiPageCategoryLabels: Record<WikiPageCategory, string> = {
  persona: '人格',
  enemy: '敌方单位',
  abnormality: '异想体',
  ego: 'E.G.O 装备',
  ego_gift: 'E.G.O 饰品',
  announcer: '播报员',
  story: '剧情',
  stage: '关卡',
  item: '物品',
  mechanism: '机制',
  keyword: '关键词',
}

// ── 信息框字段 ──────────────────────────────────────────────
export interface InfoboxField {
  label: string
  value: string
  type: 'text' | 'image' | 'number' | 'boolean' | 'link' | 'tags'
  editable: boolean
  source?: string // WritableSource 出处
}

// ── 信息框行 ────────────────────────────────────────────────
// 后端 wiki.getPage 经 IPC 下发的信息框是「行数组」（WikiInfoboxRowDto：
// [{label,value},…]），不是对象——契约按真实字节写，别再写成 {title,fields}。
export interface InfoboxRow {
  label: string
  value: string
}

// ── 分节 ────────────────────────────────────────────────────
export interface WikiSection {
  id: string
  title: string
  content: string // Markdown or HTML
  collapsible: boolean
  editable: boolean
  bindings?: ResourceBinding[]
  /** 分节下的条目（后端 wiki.getPage 下发） */
  entries?: WikiEntry[]
}

// ── 目录项 ──────────────────────────────────────────────────
export interface TocItem {
  id: string
  title: string
  level: number
  children?: TocItem[]
}

// ── 维基页面 ────────────────────────────────────────────────
export interface WikiPage {
  id: string
  title: string
  category: WikiPageCategory
  subtitle?: string
  infobox?: InfoboxRow[]
  sections: WikiSection[]
  toc: TocItem[]
  gallery?: GalleryImage[]
  relatedPages?: WikiRelatedPage[]
  tags?: string[]
  /** 封面地址（lme.data 虚拟主机；拿不到真实地址时为 null） */
  cover?: string | null
  lastModified?: string
}

export interface GalleryImage {
  url: string
  caption?: string
  credit?: string
  /** 「去编辑」目标路由（形如 /assets?container=...）；无则不显示该链接 */
  editTo?: string
}

export interface WikiRelatedPage {
  id: string
  title: string
  category: WikiPageCategory
  url: string
}

// ── 二级页面树 ──────────────────────────────────────────────
export interface WikiSubPage {
  id: string
  title: string
  sortOrder: number
  entries: WikiEntry[]
}

export interface WikiEntry {
  id: string
  title: string
  body: string
  bindings: ResourceBinding[]
  source: 'user' | 'auto' | 'candidate'
  lastModified?: string
  /** 来源标注：权威（后端 WikiEntryDto 下发，缺失则不渲染） */
  authority?: string
  /** 来源标注：置信度（原样展示，不做换算） */
  confidence?: number | string
  /** 来源标注：详细说明，作为悬浮提示 */
  sourceDetail?: string
}

export interface ResourceBinding {
  refKey: string
  kind: string
  display: string
  deepLink: string
  previewText?: string
  mediaKind?: string
  durationSec?: number
  /** 可展示地址（lme.data 虚拟主机）；本地拿不到真实地址时为 null，前端降级不渲染 */
  mediaUrl?: string | null
  /** 音频地址（后端解码为 WAV 后下发）；拿不到时为 null */
  audioUrl?: string | null
  /** Spine 骨架地址（.json / .skel），拿不到时为 null */
  skeletonUrl?: string | null
  /** Spine 图集地址（.atlas），拿不到时为 null */
  atlasUrl?: string | null
  /** Spine 纹理：atlas 页名 → 地址；拿不到时为 null 或空对象 */
  textureUrls?: Record<string, string> | null
}

// ── 内容编辑 ────────────────────────────────────────────────
export interface ContentEdit {
  pageId: string
  sectionId?: string
  entryId?: string
  field: string
  oldValue: string
  newValue: string
  source: 'user' | 'auto' | 'candidate'
  timestamp: number
}

// ── 搜索 ────────────────────────────────────────────────────
export interface WikiSearchQuery {
  keyword: string
  category?: WikiPageCategory
  offset: number
  limit: number
}

export interface WikiSearchResult {
  pageId: string
  title: string
  category: WikiPageCategory
  snippet: string
  url: string
}

export interface WikiSearchResponse {
  total: number
  results: WikiSearchResult[]
}

// ── 分类索引 ────────────────────────────────────────────────
export interface CategoryIndex {
  category: WikiPageCategory
  label: string
  pages: CategoryPageRef[]
}

export interface CategoryPageRef {
  id: string
  title: string
  subtitle?: string
  thumbnailUrl?: string
}

// ── 首页数据 ────────────────────────────────────────────────
export interface HomeData {
  categories: HomeCategoryCard[]
  recentPages: HomeRecentPage[]
  stats: WikiStats
}

export interface HomeCategoryCard {
  category: WikiPageCategory
  label: string
  icon: string
  pageCount: number
  featuredPages: CategoryPageRef[]
}

export interface HomeRecentPage {
  id: string
  title: string
  category: WikiPageCategory
  lastModified: string
}

export interface WikiStats {
  totalPages: number
  totalEntries: number
  totalBindings: number
  userEdits: number
}

// ── 维基页面生成（wiki.generate / wiki.generateStatus）──────
export interface WikiCategoryCount {
  category: string
  label: string
  pageCount: number
}

export interface WikiGenerateResponse {
  ok: boolean
  message: string
  pages: number
  subPages: number
  entries: number
  bindings: number
  writtenEntries: number
  revisedPreserved: number
  unknownSourceEntries: number
  elapsedMs: number
  databasePath: string
  categories: WikiCategoryCount[]
}

export interface WikiGenerateStatusResponse {
  ready: boolean
  databaseExists: boolean
  databasePath: string
  pages: number
  subPages: number
  entries: number
  bindings: number
  revisedEntries: number
  categories: WikiCategoryCount[]
}

// ── 面包屑 ──────────────────────────────────────────────────
export interface BreadcrumbItem {
  label: string
  route: string
}

// ── Spine 全库浏览（spine.catalog / spine.resolve）──────────
// 名册只列「有哪些 Spine 挂点」，不解素材（解一套实测平均 2.1s，几百套一次解不可接受）；
// 某一条的三件套地址由 spine.resolve 按需取，取不到给中文 reason（不造假地址）。

export interface SpineCatalogSummary {
  total: number
  bound: number
  unbound: number
  /** 其中 bundle 文件此刻不在本机的（Unity 缓存被清，非代码可补）。 */
  bundleMissing: number
}

export interface SpineCatalogItem {
  /** 容器路径，取数键（拿它调 spine.resolve）。 */
  refKey: string
  name: string
  /** 中文归类：人格立绘 / 敌方单位 / 异想体 / 人格(战斗) / E.G.O / 战斗其它。 */
  group: string
  bundlePresent: boolean
  /** 被多少个维基页面绑定（0 = 从未在界面出现过）。 */
  boundPageCount: number
  /** 归属来源的中文说明（既有页面绑定 / 关联索引自动接入 / 未归类）。 */
  source: string
  /** 解析状态的机器可读值（parsed / likely / bundle-missing / no-skeleton / uncategorized / failed）。 */
  parseStatus: string
  /** 解析状态的中文说明。 */
  parseStatusLabel: string
  /** 归属的维基页面 id（点得进去；未归类时为空）。 */
  ownerPageIds: string[]
}

export interface SpineCatalogResponse {
  summary: SpineCatalogSummary
  total: number
  items: SpineCatalogItem[]
}

export interface SpineResolveResult {
  ok: boolean
  skeletonUrl: string | null
  atlasUrl: string | null
  textureUrls: Record<string, string> | null
  skeletonFormat: string | null
  label: string | null
  /** 取不到时的中文原因（如实说，不造假）。 */
  reason: string | null
  /** 归属来源（中文）。 */
  source: string
  /** 归属的维基页面 id（点得进去）。 */
  ownerPageIds: string[]
  /** 所在 bundle 此刻在不在本机。 */
  bundlePresent: boolean
  /** 解析状态（parsed / no-skeleton / bundle-missing / failed）。 */
  parseStatus: string
  /** 解析状态的中文说明。 */
  parseStatusLabel: string
}

// ── Spine 导出（spine.export）──────────────────────────────
// 语义：请求给一个 targetDirectory 与若干 refKey，**每条在目标目录下落一个以骨架名命名的子目录**
// （<骨架名>.json / <骨架名>.atlas.txt / <页名>.png），这正是 Spine 运行时期望的磁盘形态。
// 默认**不覆盖**（已存在的文件跳过并如实列在 skippedFiles 里）；覆盖与否由用户显式决定。
// 字段与后端 IpcDtos.cs 的 SpineExportRequest / SpineExportResponse 一一对应（不改名、不造字段）。

/** spine.export 请求载荷。`operationId` 供 `cancel` 中断与 `progress` 事件关联。 */
export interface SpineExportRequest {
  /** refKey（容器路径），与 spine.catalog 的 refKey 同口径。 */
  assetIds: string[]
  /** 导出根目录（由 dialog.folderPick 给定）。 */
  targetDirectory: string
  /** 是否覆盖同名文件；默认 false = 跳过已存在并如实列出。 */
  overwrite?: boolean
  /** 操作 id；不传则本次导出不可单独取消，也收不到进度事件。 */
  operationId?: string
}

/** 导出到磁盘的一个文件（相对导出目录的路径 + 字节数）。 */
export interface SpineExportedFile {
  /** 相对导出根目录的路径（`/` 分隔）。 */
  path: string
  /** 角色：skeleton / atlas / texture。 */
  role: string
  bytes: number
}

/** 一条挂点的导出明细（成功与失败都在里面；批量时单条失败不中断整批）。 */
export interface SpineExportItem {
  /** 容器路径。 */
  refKey: string
  /** 骨架名（子目录名）。 */
  name: string
  ok: boolean
  outputDirectory: string | null
  files: SpineExportedFile[]
  /** 被「不覆盖」策略跳过的相对路径。 */
  skippedFiles: string[]
  /** 失败时的中文原因。 */
  reason: string | null
}

/** spine.export 响应载荷。 */
export interface SpineExportResponse {
  /** 成功导出的 refKey（顺序与请求一致）。 */
  written: string[]
  /** 失败/未导出的「refKey：中文原因」清单。 */
  skipped: string[]
  outputDirectory: string | null
  /** 逐文件清单（仅成功的，含相对路径 + 字节数 + 角色）。 */
  files: SpineExportedFile[] | null
  /** 逐条明细（成功与失败都在里面）。 */
  items: SpineExportItem[] | null
  /** 本次采用的覆盖策略（中文回显）。 */
  overwrite: string | null
  /** 一句话中文汇总。 */
  info: string | null
  /** 是否被取消。 */
  cancelled: boolean
}
