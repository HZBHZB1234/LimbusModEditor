using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
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

/// <summary>relation.links 请求载荷。</summary>
public sealed record RelationLinksRequest(string SubjectId);

// ── 2.7 配置 / UI 状态 ──────────────────────────────────────────

/// <summary>config.read / config.write 载荷。</summary>
public sealed record ConfigReadRequest(string Key);
public sealed record ConfigWriteRequest(string Key, string Value);
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

// ── 2.6 项目 / 导出 ──────────────────────────────────────────────

/// <summary>project.open / project.save 载荷。</summary>
public sealed record ProjectOpenRequest(string ProjectFile);
public sealed record ProjectSaveRequest(string ProjectFile);

/// <summary>export.plan 请求载荷。</summary>
public sealed record ExportPlanRequest;

/// <summary>export.plan 响应载荷。</summary>
public sealed record ExportPlanResponse(
    IReadOnlyList<ExportSlot> Slots,
    IReadOnlyList<string> SkippedReasons);

/// <summary>导出槽位。</summary>
public sealed record ExportSlot(
    string Format,
    string Container,
    int Count);

/// <summary>export.run 请求载荷。</summary>
public sealed record ExportRunRequest(string TargetDirectory);

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
public sealed record LangExportPatchRequest(string Language, string TargetDirectory);

// ── 静态数据工作台 ────────────────────────────────────────────────

public sealed record StaticTableListRequest(int Offset = 0, int Take = 200);
public sealed record StaticTableItem(string TableId, string Name, int RecordCount);
public sealed record StaticTableListResponse(IReadOnlyList<StaticTableItem> Items, long TotalCount, int Offset, int Take);

public sealed record StaticRecordsRequest(string TableId, int Offset = 0, int Take = 200);
public sealed record StaticRecordItem(string RecordId, string Summary, string? RawJson = null);
public sealed record StaticRecordsResponse(string TableId, IReadOnlyList<StaticRecordItem> Items, long TotalCount, int Offset, int Take);

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
/// 资源绑定 DTO。<c>MediaUrl</c> 只在<b>本地确实有可用地址</b>时给出（<c>lme.data</c> 虚拟主机），
/// 否则为 null —— 前端据此降级，不占位、不编造。
/// </summary>
public sealed record WikiBindingDto(
    string RefKey,
    string Kind,
    string? Display,
    string? MediaKind,
    double? DurationSec,
    string? MediaUrl);

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
