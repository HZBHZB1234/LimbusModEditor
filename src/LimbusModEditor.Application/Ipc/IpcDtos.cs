using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Ipc;

// ── 2.1 资源目录（统一分页）────────────────────────────────────────

/// <summary>catalog.query 请求载荷（WEB-IPC-CONTRACT §2.1）。</summary>
public sealed record CatalogQueryRequest(
    AssetSearchQuery Query,
    int Offset,
    int Take);

/// <summary>catalog.query 响应载荷 = AssetCatalogPage(Items,TotalCount,Offset,Take)。</summary>
public sealed record CatalogQueryResponse(
    IReadOnlyList<AssetRecord> Items,
    long TotalCount,
    long Offset,
    int Take);

/// <summary>catalog.count 请求载荷。</summary>
public sealed record CatalogCountRequest(AssetSearchQuery Query);

/// <summary>catalog.count 响应载荷。</summary>
public sealed record CatalogCountResponse(long TotalCount);

/// <summary>catalog.locate 请求载荷。</summary>
public sealed record CatalogLocateRequest(string AssetId);

/// <summary>catalog.locate 响应载荷。</summary>
public sealed record CatalogLocateResponse(long Offset);

// ── 2.1b 容器树（惰性加载，前端竖切片需要）────────────────────────

/// <summary>catalog.containerRoots 请求载荷（空）。</summary>
public sealed record CatalogContainerRootsRequest;

/// <summary>catalog.containerChildren 请求载荷。</summary>
public sealed record CatalogContainerChildrenRequest(string ParentPath);

/// <summary>容器树节点。</summary>
public sealed record ContainerTreeNode(
    string Name,
    string Path,
    bool IsLeaf,
    int ChildCount,
    IReadOnlyList<string> AssetIds);

/// <summary>容器树响应载荷。</summary>
public sealed record ContainerTreeResponse(IReadOnlyList<ContainerTreeNode> Nodes);

/// <summary>asset.preview 请求载荷。</summary>
public sealed record AssetPreviewRequest(string AssetId);

/// <summary>asset.preview 响应载荷。</summary>
public sealed record AssetPreviewResponse(
    string Kind,
    IReadOnlyList<PropertyRow> Rows,
    string? BinaryUrl);

/// <summary>属性行（预览卡）。</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>asset.readText 请求载荷。</summary>
public sealed record AssetReadTextRequest(string AssetId, int? CharLimit);

/// <summary>asset.readText 响应载荷。</summary>
public sealed record AssetReadTextResponse(string Text);

// ── 2.5 关联与精确跳转 ──────────────────────────────────────────

/// <summary>relation.reveal 请求载荷（WEB-IPC-CONTRACT §2.5）。
/// payload 即 RelationDeepLink 的 '\0' 分段载荷，原样透传不解析。</summary>
public sealed record RelationRevealRequest(
    string Payload,
    string FallbackKeyword);

/// <summary>relation.reveal 响应载荷。</summary>
public sealed record RelationRevealResponse(bool Located);

/// <summary>relation.describe 请求载荷。</summary>
public sealed record RelationDescribeRequest(string AssetId);

/// <summary>
/// relation.describe 响应里的一行：对象身份 + <b>命中的那条引用</b>的形态。
///
/// <para><paramref name="SubjectId"/> 形如 <c>persona:10201</c>，它<b>同时也是维基页面 id</b>
/// （<c>/wiki/page/persona:10201</c>）；<paramref name="PageId"/> 是「维基里真的生成过这一页」
/// 的确认值——没生成过给 null，前端就不画跳转（不猜 id）。</para>
/// </summary>
public sealed record RelationDescribeSubjectDto(
    string SubjectId,
    string DisplayName,
    string Category,
    string CategoryLabel,
    string Subtitle,
    string Character,
    string Kind,
    string KindLabel,
    string Display,
    string? Detail,
    string? PageId);

/// <summary>relation.describe 响应载荷（资源 → 所属对象，反向索引）。</summary>
/// <param name="Subjects">命中的对象行（可为空）。</param>
/// <param name="Info">中文说明（也解释「为什么没有行」）。</param>
public sealed record RelationDescribeResponse(
    IReadOnlyList<RelationDescribeSubjectDto> Subjects,
    string Info);

/// <summary>relation.links 请求载荷。</summary>
public sealed record RelationLinksRequest(string SubjectId);

/// <summary>relation.links 响应里的一行（<see cref="RelationLink"/> 的契约投影）。</summary>
public sealed record RelationLinkDto(
    string SubjectId,
    string Category,
    string Kind,
    string RefKey,
    string Display,
    string? Detail,
    string? PreviewText,
    string PreviewKind,
    string MediaKind,
    double? DurationSec,
    string? RefPath,
    string? DeepLink,
    string? TargetSubjectId,
    long SizeBytes);

/// <summary>relation.links 响应载荷（对象 → 资源）。</summary>
/// <param name="Links">该对象的全部关联资源（可为空）。</param>
/// <param name="Info">中文说明（也解释「为什么没有」）。</param>
public sealed record RelationLinksResponse(IReadOnlyList<RelationLinkDto> Links, string Info);

// ── 2.7 配置 / UI 状态 ──────────────────────────────────────────

/// <summary>config.read / config.write 载荷。</summary>
public sealed record ConfigReadRequest(string Key);
public sealed record ConfigWriteRequest(string Key, string Value);

/// <summary>
/// config.autoDetect 响应载荷：四个目录的探测结果。
/// <b>探不到就给 null</b>（不猜路径、不拿占位值充数）；<paramref name="Info"/>
/// 说明这次补上了什么、还缺什么。
/// </summary>
public sealed record ConfigAutoDetectResponse(
    string? GameDirectory,
    string? UnityCacheDirectory,
    string? ModDirectory,
    string? FmodLibraryDirectory,
    string Info);
public sealed record ConfigReadResponse(string? Value);

/// <summary>uiState.read / uiState.write 载荷。</summary>
public sealed record UiStateReadRequest(string PageKey, int? ColumnWidth);
public sealed record UiStateWriteRequest(string PageKey, int? ColumnWidth);

// ── 6. 原生对话框与剪贴板 / Process.Start 回调 ───────────────────

/// <summary>dialog.openFile 请求载荷。</summary>
public sealed record DialogOpenFileRequest(
    string? Title,
    string? Filters,
    string? StartPath);

/// <summary>dialog.saveFile 请求载荷。</summary>
public sealed record DialogSaveFileRequest(
    string? Title,
    string? Filters,
    string? StartPath,
    string? DefaultName);

/// <summary>dialog.folderPick 请求载荷。</summary>
public sealed record DialogFolderPickRequest(
    string? Title,
    string? StartPath);

/// <summary>dialog.messageBox 请求载荷。</summary>
public sealed record DialogMessageBoxRequest(
    string Text,
    string Title,
    string Buttons,
    string? Icon);

/// <summary>对话框响应载荷。</summary>
public sealed record DialogResponse(bool Ok, string? Path, string? Button);

/// <summary>clipboard.readText 响应载荷。</summary>
public sealed record ClipboardReadResponse(string Text);

/// <summary>clipboard.writeText 请求载荷。</summary>
public sealed record ClipboardWriteRequest(string Text);

/// <summary>process.start 请求载荷。</summary>
public sealed record ProcessStartRequest(
    string? AppId,
    string? Args);

/// <summary>process.start 响应载荷。</summary>
public sealed record ProcessStartResponse(
    bool Ok,
    int? ExitCode,
    string? Message);

// ── 2.3 资产编辑 ──────────────────────────────────────────────────

/// <summary>asset.edit.replacePayload 请求载荷。</summary>
public sealed record AssetEditReplacePayloadRequest(string AssetId, string ReplacementPath);

/// <summary>asset.edit.fieldEdit 请求载荷。</summary>
public sealed record AssetEditFieldEditRequest(string AssetId, UnityFieldEditSetDto Edits);

/// <summary>Unity 字段编辑集 DTO。</summary>
public sealed record UnityFieldEditSetDto(IReadOnlyList<FieldEditDto> Edits);
public sealed record FieldEditDto(string Path, string Value);

/// <summary>asset.edit.spriteMetadata 请求载荷。</summary>
public sealed record AssetEditSpriteMetadataRequest(
    string AssetId,
    SpriteRectDto Rect,
    SpritePivotDto Pivot,
    SpriteBorderDto Border,
    int Ppu);

/// <summary>asset.edit.batchReplace 请求载荷：从一个目录里按文件名批量登记替换。
/// 源目录只读（文件会被复制进项目 edits/assets），namePattern 为空 = 不限文件名。</summary>
public sealed record AssetEditBatchReplaceRequest(
    string SourceDirectory,
    string? NamePattern = null,
    bool OnlyUnreplaced = true);

/// <summary>asset.edit.batchReplace 响应里登记成功的一条。</summary>
public sealed record AssetBatchReplaceItem(string AssetId, string LogicalPath, string ReplacementPath);

/// <summary>asset.edit.batchReplace 响应载荷。</summary>
public sealed record AssetEditBatchReplaceResponse(
    int Replaced,
    int Skipped,
    IReadOnlyList<AssetBatchReplaceItem> Items,
    IReadOnlyList<string> Warnings,
    string Info);

/// <summary>asset.edit.clearEdits 请求载荷。</summary>
public sealed record AssetEditClearEditsRequest(string AssetId);

/// <summary>asset.edit.clearEdits 响应载荷：同时给出项目编辑清单的同步情况，
/// 让「还有 X 处改动」的计数不会留下幽灵条目。</summary>
public sealed record AssetEditClearEditsResponse(
    bool Ok,
    int ClearedCount,
    int RemovedEditOperations,
    int RemainingEdits,
    string Info);

public sealed record SpriteRectDto(int X, int Y, int Width, int Height);
public sealed record SpritePivotDto(float X, float Y);
public sealed record SpriteBorderDto(int Left, int Top, int Right, int Bottom);

// ── 2.4 Spine ────────────────────────────────────────────────────

/// <summary>spine.locate 请求载荷。</summary>
public sealed record SpineLocateRequest(string AssetId);

/// <summary>spine.locate 响应载荷。</summary>
public sealed record SpineLocateResponse(
    string Folder,
    SpineFiles Files,
    IReadOnlyList<string> Pages);

/// <summary>Spine 三件套 URL。</summary>
public sealed record SpineFiles(string? Skeleton, string? Atlas, IReadOnlyList<string> Pages);

/// <summary>spine.export 请求载荷。</summary>
public sealed record SpineExportRequest(IReadOnlyList<string> AssetIds, string TargetDirectory);

/// <summary>spine.export 响应载荷。</summary>
public sealed record SpineExportResponse(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Skipped);

// ── 2.4b Spine 全库浏览（只读）──────────────────────────────────

/// <summary>
/// <c>spine.catalog</c> 请求载荷：<b>只列名册，不解素材</b>。
/// 解一套实测平均 2.1 s（主因是解大 bundle），几百套一次性解不可接受，
/// 所以名册只做索引查询，明细由 <c>spine.resolve</c> 按需取（见 WEB-IPC-CONTRACT）。
/// </summary>
public sealed record SpineCatalogRequest(string? Keyword, bool OnlyUnbound, int Offset, int Limit);

/// <summary>名册概览计数。</summary>
/// <param name="Total">全部 Spine 挂点数。</param>
/// <param name="Bound">被至少一个维基页面绑定的。</param>
/// <param name="Unbound">没有任何页面绑定、界面上从未出现过的。</param>
/// <param name="BundleMissing">其中 bundle 文件此刻不在本机的（Unity 缓存被清，非代码可补）。</param>
public sealed record SpineCatalogSummaryDto(int Total, int Bound, int Unbound, int BundleMissing);

/// <summary>名册的一条（<b>还没解素材</b>）。</summary>
/// <param name="RefKey">容器路径（取数键，前端拿它调 spine.resolve）。</param>
/// <param name="Name">显示名（prefab 文件名去扩展名）。</param>
/// <param name="Group">中文归类（人格立绘 / 敌方单位 / 异想体 / 人格(战斗) / E.G.O / 战斗其它）。</param>
/// <param name="BundlePresent">所在 bundle 此刻在不在本机。</param>
/// <param name="BoundPageCount">被多少个维基页面绑定（0 = 从未在界面出现过）。</param>
public sealed record SpineCatalogItemDto(
    string RefKey,
    string Name,
    string Group,
    bool BundlePresent,
    int BoundPageCount);

/// <summary><c>spine.catalog</c> 响应载荷（分页）。</summary>
public sealed record SpineCatalogResponse(
    SpineCatalogSummaryDto Summary,
    int Total,
    IReadOnlyList<SpineCatalogItemDto> Items);

/// <summary><c>spine.resolve</c> 请求载荷：按 refKey 取一条的三件套地址。</summary>
public sealed record SpineResolveRequest(string RefKey);

/// <summary>
/// <c>spine.resolve</c> 响应载荷（与维基绑定同一套地址口径，前端复用同一渲染器）。
/// <b>取不到就 Ok=false + 中文 Reason</b>，绝不编造地址。
/// </summary>
/// <param name="Ok">三件套是否取到（骨架 + 图集齐）。</param>
/// <param name="SkeletonUrl">骨架地址（<c>lme.data</c> 虚拟主机）。</param>
/// <param name="AtlasUrl">图集文本地址。</param>
/// <param name="TextureUrls">页名 → 纹理地址（页名与裸名两种键都给）。</param>
/// <param name="SkeletonFormat"><c>json</c> 或 <c>binary</c>。</param>
/// <param name="Label">显示标签。</param>
/// <param name="Reason">取不到时的中文原因（如实说，不造假）。</param>
public sealed record SpineResolveResponse(
    bool Ok,
    string? SkeletonUrl,
    string? AtlasUrl,
    IReadOnlyDictionary<string, string>? TextureUrls,
    string? SkeletonFormat,
    string? Label,
    string? Reason);

// ── 2.6 项目 / 导出 ──────────────────────────────────────────────

/// <summary>project.open / project.save 载荷。</summary>
public sealed record ProjectOpenRequest(string ProjectFile);
public sealed record ProjectSaveRequest(string ProjectFile);

/// <summary>
/// project.create 载荷。<paramref name="Directory"/> 缺省时落在程序目录的
/// projects 子目录，<paramref name="Name"/> 缺省时为「未命名模组」。
/// </summary>
public sealed record ProjectCreateRequest(string? Directory, string? Name);

/// <summary>project.create 响应载荷。</summary>
/// <param name="Path">项目文件（.lmeproj）全路径。</param>
/// <param name="Name">项目名。</param>
/// <param name="Directory">项目根（sources/workspace/… 的父目录）。</param>
public sealed record ProjectCreateResponse(string Path, string Name, string Directory);

/// <summary>project.recent 响应里的一行。</summary>
public sealed record ProjectRecentItem(string Name, string Path, string LastOpened);

/// <summary>
/// project.recent 响应载荷。最近项目读的是程序目录
/// <c>config/shared-config.json</c>（<see cref="LimbusModEditor.Application.AppConfig.SharedAppConfig.RecentProjects"/>）；
/// 一条都没有时 <paramref name="Projects"/> 为空，<paramref name="Info"/> 给中文说明。
/// </summary>
public sealed record ProjectRecentResponse(
    IReadOnlyList<ProjectRecentItem> Projects,
    string Info);

/// <summary>
/// scan.run 请求载荷：触发既有启动扫描，把游戏资源登记进<b>当前项目</b>。
/// </summary>
/// <param name="Scope">
/// <c>assets</c>（默认，只扫游戏资源 → 项目资产）/ <c>all</c>（资源 + 音频/静态/文本/关联四个索引）。
/// </param>
/// <param name="OperationId">进度事件的操作号（缺省 <c>scan-&lt;请求号&gt;</c>）；<c>cancel</c> 按它取消。</param>
/// <param name="UnityCacheDirectory">
/// 可选覆盖：不传时走既有解析链（共享配置 → 项目字段 → 本机规范缓存根）。
/// 传了就以它为准（冒烟/设置页要扫指定缓存时用）。
/// </param>
/// <param name="GameDirectory">可选覆盖：游戏目录（vanilla 基线判据；不传就不做该判定）。</param>
public sealed record ScanRunRequest(
    string? Scope = null,
    string? OperationId = null,
    string? UnityCacheDirectory = null,
    string? GameDirectory = null);

/// <summary>scan.run 响应里的一个步骤（与 <c>StartupScanStepResult</c> 一一对应）。</summary>
public sealed record ScanStepDto(
    string Key,
    string Label,
    string Status,
    string Detail,
    double ElapsedSeconds);

/// <summary>
/// scan.run 响应载荷。<paramref name="Status"/> 取 <c>Scanned</c> / <c>AlreadyFresh</c> /
/// <c>Skipped</c> / <c>Failed</c>（原样来自既有扫描，不改写）。
/// </summary>
/// <param name="BundleCount">扫到的缓存条目（bundle）数；该步骤没给计数时为 0。</param>
/// <param name="AssetCount">扫描后项目里的资源数（<b>实测</b>，不是估计）。</param>
/// <param name="CacheDirectory">本次实际使用的 Unity 缓存目录（回退/覆盖后到底扫了哪儿，照实给）。</param>
public sealed record ScanRunResponse(
    string Scope,
    string Status,
    string Detail,
    int BundleCount,
    int AssetCount,
    double ElapsedSeconds,
    string CacheDirectory,
    IReadOnlyList<ScanStepDto> Steps,
    string? Info = null);

/// <summary>export.plan 请求载荷。</summary>
public sealed record ExportPlanRequest;

/// <summary>写前校验的一条结论（分级 + 中文理由）。Level 取 error / warning / info。</summary>
public sealed record ExportCheckDto(string Level, string Target, string Message);

/// <summary>写前校验汇总（dry-run、只读）：随 export.plan / export.run 一起返回给「导出前校验」面板。</summary>
public sealed record ExportValidationDto(
    int ChangedCount,
    int CheckedFileCount,
    int ErrorCount,
    int WarningCount,
    bool HasBlockingError,
    IReadOnlyList<ExportCheckDto> Checks,
    string Info);

/// <summary>export.plan 响应载荷。</summary>
public sealed record ExportPlanResponse(
    IReadOnlyList<ExportSlot> Slots,
    IReadOnlyList<string> SkippedReasons,
    ExportValidationDto Validation);

/// <summary>导出槽位。</summary>
public sealed record ExportSlot(
    string Format,
    string Container,
    int Count);

/// <summary>export.run 请求载荷。</summary>
public sealed record ExportRunRequest(string TargetDirectory);

/// <summary>
/// export.run 响应里的一行：<b>计划与结果合一条</b>——写出的槽位给产物清单，
/// 没写出的给中文原因（计划阶段的跳过原因 / 写出阶段的诊断，谁有理由就用谁的）。
/// </summary>
public sealed record ExportRunItem(
    string Format,
    bool Written,
    string Directory,
    int ArtifactCount,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<string> SkippedReasons,
    IReadOnlyList<string> Warnings);

/// <summary>
/// export.run 响应载荷。<paramref name="Items"/> 是逐槽位明细（顺序与计划一致），
/// <paramref name="Skipped"/> 是「槽位：原因」的一行式清单（页面直接列）。
/// 计划只算一次（<see cref="Build.ModExportPlanService"/>），结果直接取
/// <see cref="Build.ModPackExportResult"/>，不二次计算。
/// </summary>
public sealed record ExportRunResponse(
    bool Ok,
    string Root,
    string ModName,
    int Slots,
    int WrittenSlots,
    int WrittenFileCount,
    IReadOnlyList<ExportRunItem> Items,
    IReadOnlyList<string> Skipped,
    string Info,
    ExportValidationDto Validation);

// ── 工具 ─────────────────────────────────────────────────────────

internal static class IpcDtoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

// ── 音频工作台 ────────────────────────────────────────────────────

public sealed record BankListRequest(int Offset = 0, int Take = 200);
public sealed record BankListItem(string BankId, string Name, int SampleCount, long SizeBytes);
public sealed record BankListResponse(IReadOnlyList<BankListItem> Items, long TotalCount, int Offset, int Take);

public sealed record BankSamplesRequest(string BankId);
public sealed record SampleListItem(string Name, double DurationSec, string CodecName, int Channels, int SampleRate);
public sealed record BankSamplesResponse(string BankId, IReadOnlyList<SampleListItem> Samples);

public sealed record BankPreviewRequest(string BankId, string SampleName);

/// <summary>bank.preview 响应载荷。</summary>
/// <param name="AudioUrl">可播放地址（<c>lme.data</c> 虚拟主机上的 WAV）；
/// 解不出来时整个方法按 Unsupported 失败，不给空地址、不编造。</param>
public sealed record BankPreviewResponse(string BankId, string SampleName, string AudioUrl);

public sealed record BankExportRebankRequest(string BankId, string TargetDirectory);

// ── 文本工作台 ────────────────────────────────────────────────────

public sealed record LangActiveLanguagesResponse(IReadOnlyList<string> Languages);

public sealed record LangFilesRequest(string Language, int Offset = 0, int Take = 200);
public sealed record LangFileInfoItem(string RelativePath, long SizeBytes, int KeyCount, bool IsUtf8);
public sealed record LangFilesResponse(IReadOnlyList<LangFileInfoItem> Items, long TotalCount, int Offset, int Take);

public sealed record LangSearchRequest(string Language, string Keyword, int Offset = 0, int Take = 200);
public sealed record LangSearchHitItem(string RelativePath, string Kind, string? KeyPath, string? Snippet);
public sealed record LangSearchResponse(IReadOnlyList<LangSearchHitItem> Items, long TotalCount, int Offset, int Take);

public sealed record LangReadEntryRequest(string Language, string RelativePath, string? KeyPath);
public sealed record LangReadEntryResponse(string? Value);

public sealed record LangEditEntryRequest(string Language, string RelativePath, string KeyPath, string Value);
/// <summary>lang.exportPatch 请求载荷。</summary>
/// <param name="Language">语言（空/缺省 = 活动语言）。</param>
/// <param name="TargetDirectory">
/// 补丁要写到哪个<b>目录</b>（不存在就创建）。文件名由网关定：固定 <c>langpatch.json</c>
/// （此前它被当成「文件路径」直接写了，产物是一个没有扩展名的文件）。真正落地的完整路径
/// 在响应的 <c>outputPath</c> 里给出，调用方不用自己拼。
/// </param>
public sealed record LangExportPatchRequest(string Language, string TargetDirectory);

/// <summary>lang.fileEntries 请求载荷：一个 lang 文件的<b>键值分页</b>（不一次全量返回）。</summary>
public sealed record LangFileEntriesRequest(string RelativePath, int Offset = 0, int Take = 200, string? Language = null);
public sealed record LangFileEntryItem(string KeyPath, string Value);
public sealed record LangFileEntriesResponse(
    string RelativePath,
    IReadOnlyList<LangFileEntryItem> Items,
    int TotalCount,
    int Offset,
    int Take);

/// <summary>lang.applyPatch 里的一条改动：<paramref name="Value"/> 为 null 表示删除该键。</summary>
public sealed record LangKeyEditDto(string KeyPath, string? Value);

/// <summary>lang.applyPatch 请求载荷：<b>按 key 写回</b>（不是整份文本替换）。</summary>
public sealed record LangApplyPatchRequest(
    string RelativePath,
    IReadOnlyList<LangKeyEditDto> Edits,
    string? Language = null);

/// <summary>lang.applyPatch 响应载荷：逐条给结论（生效 / 找不到 / 被拒），不静默丢弃。</summary>
public sealed record LangApplyPatchResponse(
    string RelativePath,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Rejected,
    string Info);

// ── 文本目录树（懒加载）──────────────────────────────────────────

/// <summary>text.fileTreeChildren 请求载荷：<paramref name="ParentPath"/> 为空表示根。</summary>
public sealed record LangFileTreeRequest(string? ParentPath);

/// <summary>text.fileTreeChildren 响应载荷：该目录下的一层子节点 + 中文说明。</summary>
public sealed record LangFileTreeResponse(
    IReadOnlyList<LangFileTreeNode> Nodes,
    string Info);

// ── 静态数据工作台 ────────────────────────────────────────────────

public sealed record StaticTableListRequest(int Offset = 0, int Take = 200);
public sealed record StaticTableItem(string TableId, string Name, int RecordCount);
public sealed record StaticTableListResponse(IReadOnlyList<StaticTableItem> Items, long TotalCount, int Offset, int Take);

public sealed record StaticRecordsRequest(string TableId, int Offset = 0, int Take = 200);
public sealed record StaticRecordItem(string RecordId, string Summary, string? RawJson = null);

/// <summary>static.records 响应载荷（一张表的记录分页）。</summary>
/// <param name="Items">本页记录。</param>
/// <param name="TotalCount">表内记录总数（不是本页条数）。</param>
/// <param name="Info">中文说明；只在「正文读到了但解析不出记录」这类情况下给，正常分页为 null。</param>
public sealed record StaticRecordsResponse(
    string TableId,
    IReadOnlyList<StaticRecordItem> Items,
    long TotalCount,
    int Offset,
    int Take,
    string? Info = null);

public sealed record StaticLocateRequest(string TableId, string RecordId);
public sealed record StaticReadRecordRequest(string TableId, string RecordId);
public sealed record StaticReadRecordResponse(string? Json);

public sealed record StaticEditRecordRequest(string TableId, string RecordId, string Json);
public sealed record StaticExportStaticmodRequest(string TargetDirectory);

// ── 维基编辑 → 导出 ────────────────────────────────────────────────

/// <summary>获取编辑计划：哪些编辑可导出、哪些不可、为什么。</summary>
public sealed record WikiGetEditPlanRequest(IReadOnlyList<WikiEditItem> Edits);

/// <summary>单条维基编辑。</summary>
public sealed record WikiEditItem(
    string Id,
    string Title,
    string? Content,
    WikiWritableSource? WritableSource,
    string? ReplacementPath,
    string? NewValue);

/// <summary>可写出处（三类）。</summary>
public sealed record WikiWritableSource(string Kind, string Value);

/// <summary>编辑计划响应。</summary>
public sealed record WikiGetEditPlanResponse(
    IReadOnlyList<WikiExportableEdit> Exportable,
    IReadOnlyList<WikiNonExportableEdit> NonExportable);

/// <summary>可导出的编辑。</summary>
public sealed record WikiExportableEdit(
    string Id,
    string AssetId,
    string Method);

/// <summary>不可导出的编辑。</summary>
public sealed record WikiNonExportableEdit(
    string Id,
    string Reason);

/// <summary>应用单条维基编辑。</summary>
public sealed record WikiApplyEditRequest(
    string Id,
    string Title,
    string? Content,
    WikiWritableSource? WritableSource,
    string? ReplacementPath,
    string? NewValue);

/// <summary>应用编辑响应。</summary>
public sealed record WikiApplyEditResponse(
    bool Ok,
    string? AssetId,
    string? Method,
    string? Error);

// ── 维基页面数据 ────────────────────────────────────────────────

/// <summary>首页数据请求（无参数）。</summary>
public sealed record WikiHomeRequest();

/// <summary>分类索引请求。</summary>
public sealed record WikiCategoryIndexRequest(string Category);

/// <summary>页面加载请求。</summary>
public sealed record WikiPageLoadRequest(string PageId);

/// <summary>搜索请求。</summary>
public sealed record WikiSearchRequest(
    string Keyword,
    string? Category = null,
    int Offset = 0,
    int Limit = 20);

/// <summary>保存页面请求。</summary>
public sealed record WikiPageSaveRequest(WikiPageDto Page);

/// <summary>
/// 保存内容编辑请求。
///
/// <para><c>Content</c> 是后加的可选字段：维基页前端（WikiEntityPage）发的是
/// <c>{pageId, sectionId, content}</c>，契约「只加不删」，所以新增同义字段而不是改
/// <c>NewValue</c> 的语义。服务端按 <c>NewValue ?? Content</c> 取值。</para>
/// </summary>
public sealed record WikiSaveContentRequest(
    string PageId,
    string? SectionId,
    string? EntryId,
    string? Field,
    string? OldValue,
    string? NewValue,
    string? Content = null);

// ── 维基页面生成（G-01 缺失的生产链路）────────────────────────────

/// <summary>
/// 生成维基页面请求。<paramref name="OperationId"/> 用于 <c>cancel</c>（契约 §3.2），
/// 也是 <c>progress</c> 事件里的 <c>operationId</c>。
/// </summary>
public sealed record WikiGenerateRequest(string? OperationId = null);

/// <summary>一个类别的生成结果（页面数）。</summary>
public sealed record WikiCategoryCountDto(string Category, string Label, int PageCount);

/// <summary>生成响应：真实计数 + 库路径（都来自 <c>cache/wiki-pages.db</c>）。</summary>
public sealed record WikiGenerateResponse(
    bool Ok,
    string Message,
    int Pages,
    int SubPages,
    int Entries,
    int Bindings,
    int WrittenEntries,
    int RevisedPreserved,
    int UnknownSourceEntries,
    long ElapsedMs,
    string DatabasePath,
    IReadOnlyList<WikiCategoryCountDto> Categories);

/// <summary>生成状态响应：库在不在、有多少内容（前端据此决定要不要提示「生成页面」）。</summary>
public sealed record WikiGenerateStatusResponse(
    bool Ready,
    bool DatabaseExists,
    string DatabasePath,
    int Pages,
    int SubPages,
    int Entries,
    int Bindings,
    int RevisedEntries,
    IReadOnlyList<WikiCategoryCountDto> Categories);

/// <summary>维基页面 DTO（与前端 WikiPage 对应）。</summary>
public sealed record WikiPageDto(
    string Id,
    string Title,
    string Category,
    string? Subtitle,
    IReadOnlyList<WikiSectionDto> Sections,
    IReadOnlyList<WikiRelatedPageDto> RelatedPages,
    IReadOnlyList<WikiInfoboxRowDto> Infobox,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WikiGalleryItemDto> Gallery,
    string? Cover);

/// <summary>信息框的一行（label → value）。取不到真值就不给这一行。</summary>
public sealed record WikiInfoboxRowDto(string Label, string Value);

/// <summary>画廊的一项。地址走 <c>lme.data</c> 虚拟主机（禁止 base64）；拿不到真实地址时 Url 为 null。</summary>
public sealed record WikiGalleryItemDto(string? Url, string Caption, string Kind);

/// <summary>分节 DTO：一个分节下挂若干条目，条目下挂若干资源绑定。</summary>
public sealed record WikiSectionDto(
    string Id,
    string Title,
    string Content,
    bool Collapsible,
    bool Editable,
    IReadOnlyList<WikiEntryDto> Entries,
    IReadOnlyList<WikiBindingDto> Bindings);

/// <summary>条目 DTO（分节下的可读内容项）。</summary>
public sealed record WikiEntryDto(
    string Id,
    string Title,
    string Body,
    string? Authority,
    string? Confidence,
    string? SourceDetail);

/// <summary>
/// 资源绑定 DTO。地址只在<b>本地确实有可用地址</b>时给出（<c>lme.data</c> 虚拟主机），
/// 否则为 null —— 前端据此降级，不占位、不编造。
/// <para><c>MediaUrl</c> = 图片/预览地址；<c>AudioUrl</c> / <c>SkeletonUrl</c> /
/// <c>AtlasUrl</c> / <c>TextureUrls</c> 是音频与 Spine 各自的地址
/// （<c>TextureUrls</c> 为图集页名 → 地址）。本轮只有图片会真的给出地址。</para>
/// </summary>
public sealed record WikiBindingDto(
    string RefKey,
    string Kind,
    string? Display,
    string? MediaKind,
    double? DurationSec,
    string? MediaUrl,
    string? AudioUrl = null,
    string? SkeletonUrl = null,
    string? AtlasUrl = null,
    IReadOnlyDictionary<string, string>? TextureUrls = null);

/// <summary>相关页面 DTO。</summary>
public sealed record WikiRelatedPageDto(
    string Id,
    string Title,
    string Category,
    string Url);

/// <summary>首页数据响应。</summary>
public sealed record WikiHomeResponse(
    IReadOnlyList<WikiCategoryCardDto> Categories,
    IReadOnlyList<WikiRecentPageDto> RecentPages,
    WikiStatsDto Stats);

/// <summary>分类卡片 DTO。</summary>
public sealed record WikiCategoryCardDto(
    string Category,
    string Label,
    string Icon,
    int PageCount,
    IReadOnlyList<CategoryPageRefDto> FeaturedPages);

/// <summary>分类页面引用 DTO。</summary>
public sealed record CategoryPageRefDto(
    string Id,
    string Title,
    string? Subtitle);

/// <summary>最近页面 DTO。</summary>
public sealed record WikiRecentPageDto(
    string Id,
    string Title,
    string Category,
    string LastModified);

/// <summary>统计 DTO。</summary>
public sealed record WikiStatsDto(
    int TotalPages,
    int TotalEntries,
    int TotalBindings,
    int UserEdits);

/// <summary>分类索引响应。</summary>
public sealed record WikiCategoryIndexResponse(
    string Category,
    string Label,
    IReadOnlyList<CategoryPageRefDto> Pages);

/// <summary>搜索结果 DTO。</summary>
public sealed record WikiSearchResultDto(
    string PageId,
    string Title,
    string Category,
    string Snippet,
    string Url);

/// <summary>搜索响应。</summary>
public sealed record WikiSearchResponse(
    int Total,
    IReadOnlyList<WikiSearchResultDto> Results);

/// <summary>保存响应。</summary>
public sealed record WikiSaveResponse(bool Ok, string? Error);
