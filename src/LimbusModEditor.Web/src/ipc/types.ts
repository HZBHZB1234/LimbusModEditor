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
  id: string
  category: string
  label: string
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
