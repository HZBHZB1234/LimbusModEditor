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
public sealed record StaticRecordItem(string RecordId, string Summary);
public sealed record StaticRecordsResponse(string TableId, IReadOnlyList<StaticRecordItem> Items, long TotalCount, int Offset, int Take);

public sealed record StaticLocateRequest(string TableId, string RecordId);
public sealed record StaticReadRecordRequest(string TableId, string RecordId);
public sealed record StaticReadRecordResponse(string? Json);

public sealed record StaticEditRecordRequest(string TableId, string RecordId, string Json);
public sealed record StaticExportStaticmodRequest(string TargetDirectory);
