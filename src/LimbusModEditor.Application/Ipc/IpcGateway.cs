using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Application.Wiki;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Unity;
using NLog;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Ipc;

/// <summary>IPC 网关的常量与取消令牌存储。</summary>
public static class IpcGatewayConstants
{
    /// <summary>默认页大小（WEB-IPC-CONTRACT §2.1 统一分页）。</summary>
    public const int DefaultPageSize = 200;
}

/// <summary>
/// 取消令牌存储：跟踪进行中的操作，cancel 请求能中断它们（WEB-IPC-CONTRACT §3.2）。
/// </summary>
public static class CancellationTokenStore
{
    private static readonly Dictionary<string, CancellationTokenSource> Operations = new();

    public static CancellationToken Register(string operationId)
    {
        var cts = new CancellationTokenSource();
        lock (Operations) { Operations[operationId] = cts; }
        return cts.Token;
    }

    public static bool Cancel(string operationId)
    {
        lock (Operations)
        {
            if (Operations.TryGetValue(operationId, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
                Operations.Remove(operationId);
                return true;
            }
        }
        return false;
    }

    public static void Complete(string operationId)
    {
        lock (Operations)
        {
            if (Operations.TryGetValue(operationId, out var cts))
            {
                cts.Dispose();
                Operations.Remove(operationId);
            }
        }
    }
}

/// <summary>
/// IPC 网关：把 WebMessage 的 JSON 字符串解析为强类型请求，派发到对应服务。
/// 无 WPF 依赖——铁律 §3-10：值得测的逻辑下沉 Application。
/// </summary>
public sealed partial class IpcGateway
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly AssetCatalog _catalog;
    private readonly ProjectState _projectState;
    private readonly AssetEditService _assetEdits;
    private readonly UnityFieldEditService _unityFieldEdits;
    private readonly SpriteMetadataEditService _spriteMetaEdits;
    private readonly ModExportPlanService _exportPlan;
    private readonly ModPackExportService _exportService;
    private readonly ProjectService _projects;
    private readonly ISpineDataGateway _spineData;
    private readonly BankAudioService _bankAudio;
    private readonly BankIndexService _bankIndex;
    private readonly LangTextWorkbenchService _langText;
    private readonly LangTextPatchService _langPatch;
    private readonly StaticModService _staticMod;
    private readonly StaticIndexService _staticIndex;
    private readonly WikiMediaResolver _wikiMedia;
    private readonly RelationQueryService _relations;
    private readonly string _cacheDirectory;
    private readonly AppEnvironment _environment;

    /// <summary>组合根：从既有服务构造完整网关（由 App 宿主调用）。</summary>
    public static IpcGateway Create(
        AssetCatalog catalog,
        ProjectState projectState,
        ISpineDataGateway spineData,
        BankIndexService bankIndex,
        LangTextWorkbenchService langText,
        StaticIndexService staticIndex,
        AssetEditService? assetEdits = null,
        ModExportPlanService? exportPlan = null,
        ModPackExportService? exportService = null,
        ProjectService? projects = null,
        RelationQueryService? relations = null,
        string? cacheDirectory = null,
        AppEnvironment? environment = null)
    {
        return new IpcGateway(catalog, projectState, spineData, bankIndex, langText, staticIndex,
            assetEdits, exportPlan, exportService, projects, relations, cacheDirectory, environment);
    }

    public IpcGateway(
        AssetCatalog catalog,
        ProjectState projectState,
        ISpineDataGateway spineData,
        BankIndexService bankIndex,
        LangTextWorkbenchService langText,
        StaticIndexService staticIndex,
        AssetEditService? assetEdits = null,
        ModExportPlanService? exportPlan = null,
        ModPackExportService? exportService = null,
        ProjectService? projects = null,
        RelationQueryService? relations = null,
        string? cacheDirectory = null,
        AppEnvironment? environment = null)
    {
        _environment = environment ?? AppEnvironment.Current;
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? AppEnvironment.Current.CacheDirectory
            : cacheDirectory;
        _catalog = catalog;
        _projectState = projectState;
        _spineData = spineData ?? throw new ArgumentNullException(nameof(spineData));
        _bankAudio = new BankAudioService();
        _bankIndex = bankIndex ?? throw new ArgumentNullException(nameof(bankIndex));
        _langText = langText ?? throw new ArgumentNullException(nameof(langText));
        _langPatch = new LangTextPatchService();
        _staticMod = new StaticModService();
        _staticIndex = staticIndex ?? throw new ArgumentNullException(nameof(staticIndex));
        _assetEdits = assetEdits ?? new AssetEditService();
        _unityFieldEdits = new UnityFieldEditService();
        _spriteMetaEdits = new SpriteMetadataEditService();
        _exportPlan = exportPlan ?? new ModExportPlanService();
        _exportService = exportService ?? new ModPackExportService();
        _projects = projects ?? new ProjectService();
        // 维基资源绑定给的是容器路径，要落成真地址只能靠解析器（无 DI，就地组合）。
        _wikiMedia = new WikiMediaResolver(_catalog, _bankIndex, _spineData, _bankAudio);
        // 关联图是派生缓存（cache/relation-index.db），目录沿用 AppEnvironment，不新造路径。
        _relations = relations ?? new RelationQueryService(new RelationStore(_cacheDirectory));
    }

    /// <summary>处理一条来自页面的请求 JSON，返回响应 JSON（async 贯通）。</summary>
    public async Task<string> HandleRequestAsync(string requestJson)
    {
        IpcRequest request;
        try
        {
            request = IpcRequest.FromJson(requestJson);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "IPC 请求解析失败：{0}", requestJson[..Math.Min(200, requestJson.Length)]);
            return IpcResponse.Failure("parse-error", IpcErrorCode.InvalidQuery, "请求格式错误：" + ex.Message).ToJson();
        }

        if (request.Kind != "request")
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"无效的消息种类：{request.Kind}（只接受 request）").ToJson();
        }

        try
        {
            var response = await DispatchAsync(request);
            return response.ToJson();
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.Cancelled, "操作已取消").ToJson();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC 请求处理异常：method={0}, id={1}", request.Method, request.Id);
            return IpcResponse.Failure(request.Id, IpcErrorCode.Internal,
                "内部错误：" + ex.Message + "（详见 logs/current.log）").ToJson();
        }
    }

    private static T DeserializePayload<T>(IpcRequest request) where T : class
    {
        return request.Payload.Deserialize<T>(Options)
            ?? throw new ArgumentException($"无法将 payload 反序列化为 {typeof(T).Name}。");
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request)
    {
        return request.Method switch
        {
            // ── 2.1 资源目录 ──────────────────────────────────────────────
            "catalog.query" => HandleCatalogQuery(request),
            "catalog.count" => HandleCatalogCount(request),
            "catalog.locate" => HandleCatalogLocate(request),
            "catalog.containerRoots" => HandleContainerRoots(request),
            "catalog.containerChildren" => HandleContainerChildren(request),

            // ── 2.2 资产预览/读取 ────────────────────────────────────────
            "asset.preview" => await HandleAssetPreviewAsync(request),
            "asset.readText" => await HandleAssetReadTextAsync(request),

            // ── 2.3 资产编辑 ──────────────────────────────────────────────
            "asset.edit.replacePayload" => await HandleAssetEditReplacePayloadAsync(request),
            "asset.edit.fieldEdit" => HandleAssetEditFieldEdit(request),
            "asset.edit.spriteMetadata" => HandleAssetEditSpriteMetadata(request),

            // ── 2.4 Spine（服务已迁移至 SpineData，见 t24）──────────────
            "spine.locate" => await HandleSpineLocateAsync(request),
            "spine.export" => await HandleSpineExportAsync(request),

            // ── 2.5 关联与精确跳转 ────────────────────────────────────────
            "relation.reveal" => HandleRelationReveal(request),
            "relation.describe" => HandleRelationDescribe(request),
            "relation.links" => HandleRelationLinks(request),

            // ── 2.6 项目 / 导出 ──────────────────────────────────────────
            "project.open" => await HandleProjectOpenAsync(request),
            "project.create" => await HandleProjectCreateAsync(request),
            "project.recent" => HandleProjectRecent(request),
            "project.save" => await HandleProjectSaveAsync(request),
            "export.plan" => HandleExportPlan(request),
            "export.run" => await HandleExportRunAsync(request),

            // ── 2.7 配置 / UI 状态 ────────────────────────────────────────
            "config.read" => HandleConfigRead(request),
            "config.write" => HandleConfigWrite(request),
            "config.autoDetect" => HandleConfigAutoDetect(request),
            "uiState.read" => HandleUiStateRead(request),
            "uiState.write" => HandleUiStateWrite(request),

            // ── 音频工作台 ────────────────────────────────────────────────
            "bank.list" => HandleBankList(request),
            "bank.samples" => HandleBankSamples(request),
            "bank.preview" => await HandleBankPreviewAsync(request),
            "bank.exportRebank" => await HandleBankExportRebankAsync(request),

            // ── 文本工作台 ────────────────────────────────────────────────
            "lang.activeLanguages" => HandleLangActiveLanguages(request),
            "lang.files" => HandleLangFiles(request),
            "lang.search" => HandleLangSearch(request),
            "lang.readEntry" => HandleLangReadEntry(request),
            "lang.editEntry" => HandleLangEditEntry(request),
            "lang.exportPatch" => HandleLangExportPatch(request),
            "lang.fileEntries" => HandleLangFileEntries(request),
            // 前端历史名（text.*）与 lang.* 是同一处理器的别名，收敛到一处实现。
            "text.fileEntries" => HandleLangFileEntries(request),
            "text.fileTreeChildren" => HandleLangFileTreeChildren(request),
            "lang.fileTreeChildren" => HandleLangFileTreeChildren(request),
            "lang.applyPatch" => HandleLangApplyPatch(request),
            "text.applyPatch" => HandleLangApplyPatch(request),

            // ── 静态数据工作台 ────────────────────────────────────────────
            "static.tableList" => HandleStaticTableList(request),
            "static.records" => await HandleStaticRecordsAsync(request),
            "static.locate" => HandleStaticLocate(request),
            "static.readRecord" => HandleStaticReadRecord(request),
            "static.editRecord" => HandleStaticEditRecord(request),
            "static.exportStaticmod" => HandleStaticExportStaticmod(request),

            // ── 维基页面数据 ────────────────────────────────────────────
            "wiki.home" => HandleWikiHome(request),
            "wiki.categoryIndex" => HandleWikiCategoryIndex(request),
            "wiki.category.load" => HandleWikiCategoryIndex(request),
            // 收敛为一个方法名：历史上 wiki.getPage 与 wiki.page.load 是同一处理器的两个别名。
            "wiki.getPage" => await HandleWikiPageLoadAsync(request),
            "wiki.search" => HandleWikiSearch(request),
            "wiki.page.save" => HandleWikiPageSave(request),
            "wiki.saveContent" => HandleWikiSaveContent(request),
            "wiki.getEditPlan" => HandleWikiGetEditPlan(request),
            "wiki.applyEdit" => HandleWikiApplyEdit(request),
            "wiki.generate" => await HandleWikiGenerateAsync(request),
            "wiki.generateStatus" => HandleWikiGenerateStatus(request),

            // ── 取消 ──────────────────────────────────────────────────────
            "cancel" => HandleCancel(request),

            _ => IpcResponse.Failure(request.Id, IpcErrorCode.Internal, "未知方法：" + request.Method)
        };
    }

    // ── 2.1 资源目录（统一分页）────────────────────────────────────

    private IpcResponse HandleCatalogQuery(IpcRequest request)
    {
        var req = DeserializePayload<CatalogQueryRequest>(request);
        var page = _catalog.Page(req.Query, req.Offset, req.Take);
        return IpcResponse.Success(request.Id, new CatalogQueryResponse(
            page.Items, page.TotalCount, page.Offset, page.Take));
    }

    private IpcResponse HandleCatalogCount(IpcRequest request)
    {
        var req = DeserializePayload<CatalogCountRequest>(request);
        return IpcResponse.Success(request.Id, new CatalogCountResponse(_catalog.Count(req.Query)));
    }

    private IpcResponse HandleCatalogLocate(IpcRequest request)
    {
        var req = DeserializePayload<CatalogLocateRequest>(request);
        var location = _catalog.Locate(req.AssetId);
        if (location is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");
        var offset = location.Rank / IpcGatewayConstants.DefaultPageSize * IpcGatewayConstants.DefaultPageSize;
        return IpcResponse.Success(request.Id, new CatalogLocateResponse(offset));
    }

    private IpcResponse HandleContainerRoots(IpcRequest request)
    {
        // TODO W2: 性能优化 - 当前对全量数据（127 万）构建容器树耗时 >50s
        var query = new AssetSearchQuery();
        var totalCount = _catalog.Count(query);
        if (totalCount > 100_000)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"资源数量过多（{totalCount:N0} 条），请先使用搜索过滤缩小范围后再加载目录树。");
        }
        var tree = new AssetCatalogTree(_catalog, query);
        var roots = tree.Roots();
        return IpcResponse.Success(request.Id, new ContainerTreeResponse(
            roots.Select(n => new ContainerTreeNode(n.Name, n.Path, n.IsLeaf, (int)n.Count,
                n.IsLeaf && n.Asset is not null ? new[] { n.Asset.LogicalPath } : Array.Empty<string>())).ToList()));
    }

    private IpcResponse HandleContainerChildren(IpcRequest request)
    {
        var req = DeserializePayload<CatalogContainerChildrenRequest>(request);
        var totalCount = _catalog.Count(new AssetSearchQuery());
        if (totalCount > 100_000)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"资源数量过多（{totalCount:N0} 条），请先使用搜索过滤缩小范围后再加载目录树。");
        }
        var tree = new AssetCatalogTree(_catalog, new AssetSearchQuery());
        var parent = tree.Roots().FirstOrDefault(n => n.Path == req.ParentPath);
        if (parent is null)
            return IpcResponse.Success(request.Id, new ContainerTreeResponse(Array.Empty<ContainerTreeNode>()));
        return IpcResponse.Success(request.Id, new ContainerTreeResponse(
            parent.Expand().Select(n => new ContainerTreeNode(n.Name, n.Path, n.IsLeaf, (int)n.Count,
                n.IsLeaf && n.Asset is not null ? new[] { n.Asset.LogicalPath } : Array.Empty<string>())).ToList()));
    }

    // ── 2.2 资产预览/读取 ────────────────────────────────────────

    /// <summary>
    /// asset.preview：<b>属性行 + 预览形态 + 可展示地址</b>。
    ///
    /// <para>属性行走既有 <see cref="AssetPropertyService"/>（与旧界面属性面板同一口径：
    /// 纹理尺寸/格式、Sprite 九宫格、AudioClip 采样率、文本编码……），单项读不出来
    /// 只降级为「（不可读：原因）」，不整块失败。</para>
    ///
    /// <para>图像地址复用维基那条「容器路径 → 纹理/精灵 → PNG → <c>lme.data</c>」链路
    /// （<see cref="WikiMediaResolver.ResolveImageAsync"/>），解不出来给 null（前端降级，不占位）。</para>
    /// </summary>
    private async Task<IpcResponse> HandleAssetPreviewAsync(IpcRequest request)
    {
        var req = DeserializePayload<AssetPreviewRequest>(request);
        if (string.IsNullOrWhiteSpace(req.AssetId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少资源标识（assetId）");

        var asset = ResolveAsset(req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        var rows = new AssetPropertyService().Describe(asset)
            .Select(r => new PropertyRow(r.Label, r.Value))
            .ToList();
        var kind = PreviewKindOf(asset.Type);
        string? binaryUrl = null;
        if (kind is "Image" or "SpriteComposite")
        {
            var resolution = await _wikiMedia.ResolveImageAsync(ImageRefOf(asset));
            binaryUrl = resolution.MediaUrl;
        }
        return IpcResponse.Success(request.Id, new AssetPreviewResponse(kind, rows, binaryUrl));
    }

    /// <summary>图像解析用的容器路径：优先 <c>containerEntry</c>（Unity 容器内路径），
    /// 没有就用 <c>LogicalPath</c>（资源列表行的稳定键）。</summary>
    private static string ImageRefOf(AssetRecord asset)
        => asset.Metadata.TryGetValue("containerEntry", out var entry) && !string.IsNullOrWhiteSpace(entry)
            ? entry
            : asset.LogicalPath;

    /// <summary>资源类型 → 预览形态（与前端 AssetPreviewKind 的取值一致）。</summary>
    private static string PreviewKindOf(AssetType type) => type switch
    {
        AssetType.Texture => "Image",
        AssetType.Sprite => "SpriteComposite",
        AssetType.SpriteAtlas => "Atlas",
        AssetType.Audio => "Audio",
        AssetType.Text => "Text",
        AssetType.Json => "JsonFields",
        AssetType.Material => "Material",
        AssetType.Shader => "Shader",
        AssetType.Video => "Video",
        AssetType.Binary => "Hex",
        AssetType.Unknown => "None",
        _ => "Summary",
    };

    private async Task<IpcResponse> HandleAssetReadTextAsync(IpcRequest request)
    {
        var req = DeserializePayload<AssetReadTextRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        var projectRoot = _projectState.ProjectFile != null
            ? Path.GetDirectoryName(_projectState.ProjectFile)
            : null;
        var text = await UnityCacheMaterializationService.MaterializeForEditingAsync(project, asset, projectRoot);
        if (req.CharLimit is { } limit && text.Length > limit)
            text = text[..limit];
        return IpcResponse.Success(request.Id, new AssetReadTextResponse(text));
    }

    // ── 2.3 资产编辑 ──────────────────────────────────────────────

    private async Task<IpcResponse> HandleAssetEditReplacePayloadAsync(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditReplacePayloadRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");
        if (!File.Exists(req.ReplacementPath))
            return IpcResponse.Failure(request.Id, IpcErrorCode.IoError, $"替换文件不存在：{req.ReplacementPath}");

        var projectDir = _projectState.ProjectFile != null
            ? Path.GetDirectoryName(_projectState.ProjectFile)!
            : Environment.CurrentDirectory;
        var result = await _assetEdits.ReplaceFromFileAsync(project, Guid.Parse(req.AssetId), req.ReplacementPath, projectDir);
        return IpcResponse.Success(request.Id, new { ok = true, storedPath = result.StoredPath, size = result.Size });
    }

    private IpcResponse HandleAssetEditFieldEdit(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditFieldEditRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        var drafts = req.Edits.Edits.Select(d => new UnityFieldEditDraft(d.Path, d.Value));
        _unityFieldEdits.Set(project, asset, Array.Empty<UnityFieldNode>(), drafts);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    private IpcResponse HandleAssetEditSpriteMetadata(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditSpriteMetadataRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        var metadata = new UnitySpriteMetadata(
            new UnitySpriteRect(req.Rect.X, req.Rect.Y, req.Rect.Width, req.Rect.Height),
            new UnitySpriteVector2(req.Pivot.X, req.Pivot.Y),
            new UnitySpriteBorder(req.Border.Left, req.Border.Top, req.Border.Right, req.Border.Bottom),
            req.Ppu);
        _spriteMetaEdits.Set(project, asset, metadata);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    // ── 2.4 Spine ────────────────────────────────────────────────

    private async Task<IpcResponse> HandleSpineLocateAsync(IpcRequest request)
    {
        var req = DeserializePayload<SpineLocateRequest>(request);
        var (data, error) = await _spineData.GetSpineDataByPathAsync(req.AssetId);
        if (data is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, error ?? "未找到 Spine 素材");

        var files = new SpineFiles(
            Skeleton: "skeleton.json",
            Atlas: "atlas.txt",
            Pages: data.AvailablePages.ToArray());
        return IpcResponse.Success(request.Id, new SpineLocateResponse(data.Label, files, data.AvailablePages));
    }

    private async Task<IpcResponse> HandleSpineExportAsync(IpcRequest request)
    {
        var req = DeserializePayload<SpineExportRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var assetId in req.AssetIds)
        {
            var (data, error) = await _spineData.GetSpineDataByPathAsync(assetId);
            if (data is null)
            {
                skipped.Add($"{assetId}：{error ?? "未找到"}");
                continue;
            }
            // TODO: 写盘到 req.TargetDirectory（需 AtomicOutput 纪律）
            written.Add(assetId);
        }
        return IpcResponse.Success(request.Id, new SpineExportResponse(written, skipped));
    }

    // ── 2.5 关联与精确跳转 ──────────────────────────────────────────

    private IpcResponse HandleRelationReveal(IpcRequest request)
    {
        var req = DeserializePayload<RelationRevealRequest>(request);
        bool located = false;
        try
        {
            var parts = RelationDeepLink.Decode(req.Payload);
            if (parts.Count > 0)
            {
                var loc = _catalog.Locate(parts[0]);
                located = loc is not null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "relation.reveal 定位失败：payload={0}", req.Payload);
        }
        return IpcResponse.Success(request.Id, new RelationRevealResponse(located));
    }

    private IpcResponse HandleRelationDescribe(IpcRequest request)
    {
        var req = DeserializePayload<RelationDescribeRequest>(request);
        var asset = ResolveAsset(req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        // 库没建好 / 资源没有容器路径 / 没命中：服务返回空列表 + 中文原因，不抛、不编造。
        var hits = _relations.DescribeHitsForAsset(asset);
        var rows = new List<RelationDescribeSubjectDto>(hits.Hits.Count);
        var existingPages = hits.Hits.Count == 0 ? null : ReadExistingWikiPageIds();
        foreach (var hit in hits.Hits)
        {
            rows.Add(new RelationDescribeSubjectDto(
                hit.SubjectId,
                hit.DisplayName,
                hit.Category,
                hit.CategoryLabel,
                hit.Subtitle,
                hit.Character,
                hit.Kind,
                hit.KindLabel,
                hit.Display,
                hit.Detail,
                // 页面 id 就是对象 id，但只有维基里真的有这一页才给，避免前端点了跳 404。
                existingPages?.Contains(hit.SubjectId) == true ? hit.SubjectId : null));
        }
        return IpcResponse.Success(request.Id, new RelationDescribeResponse(rows, hits.Info));
    }

    private IpcResponse HandleRelationLinks(IpcRequest request)
    {
        var req = DeserializePayload<RelationLinksRequest>(request);
        if (string.IsNullOrWhiteSpace(req.SubjectId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少对象 id（subjectId）");
        if (!_relations.IsReady)
            return IpcResponse.Success(request.Id, new RelationLinksResponse([],
                "关联图还没建立（启动扫描会自动分析四个索引库）。"));

        var links = _relations.Links(req.SubjectId);
        var rows = links.Select(link => new RelationLinkDto(
            link.SubjectId,
            link.Category,
            link.Kind.ToString(),
            link.RefKey,
            link.Display,
            link.Detail,
            link.PreviewText,
            link.PreviewKind.ToString(),
            link.MediaKind,
            link.DurationSec,
            link.RefPath,
            link.DeepLink,
            link.TargetSubjectId,
            link.SizeBytes)).ToList();
        return IpcResponse.Success(request.Id, new RelationLinksResponse(rows,
            links.Count == 0 ? $"对象 {req.SubjectId} 没有登记任何关联资源。" : $"共 {links.Count} 条关联资源。"));
    }

    /// <summary>
    /// 维基里已生成过的页面 id 集合（一次读全表，避免每条命中各查一次）。
    /// 库不存在时返回空集合（= 前端不画跳转，而不是假装页面存在）。
    /// </summary>
    private HashSet<string>? ReadExistingWikiPageIds()
    {
        var pages = new WikiPageStore(_cacheDirectory);
        if (!pages.Exists) return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages.ReadPages()) ids.Add(page.PageId);
        return ids;
    }

    /// <summary>
    /// 按 <c>assetId</c> 取资源：先按 LogicalPath（资源列表行的稳定键），
    /// 再按 Unity 容器路径（维基绑定 / 目录树挑的是这个口径）。取不到返回 null。
    /// </summary>
    private AssetRecord? ResolveAsset(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return null;
        var byPath = _catalog.Resolve(new[] { assetId });
        if (byPath.Count > 0) return byPath[0];
        var byContainer = _catalog.FindByContainerEntry(assetId);
        return byContainer.Count > 0 ? byContainer[0] : null;
    }

    // ── 2.6 项目 / 导出 ──────────────────────────────────────────

    private async Task<IpcResponse> HandleProjectOpenAsync(IpcRequest request)
    {
        var req = DeserializePayload<ProjectOpenRequest>(request);
        if (!File.Exists(req.ProjectFile))
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"项目文件不存在：{req.ProjectFile}");

        var project = await _projects.LoadAsync(req.ProjectFile);
        _projectState.SetProject(project, req.ProjectFile);
        return IpcResponse.Success(request.Id, new { ok = true, name = project.Name, assetCount = project.Assets.Count });
    }

    /// <summary>
    /// 新建项目：落盘 + 置为当前项目 + 登记最近项目。
    /// <b>同名项目文件已存在时报错退出，不覆盖</b>（覆盖会毁掉用户已有模组）。
    /// </summary>
    private async Task<IpcResponse> HandleProjectCreateAsync(IpcRequest request)
    {
        var req = DeserializePayload<ProjectCreateRequest>(request);
        var name = string.IsNullOrWhiteSpace(req.Name) ? "未命名模组" : req.Name!.Trim();
        var root = string.IsNullOrWhiteSpace(req.Directory)
            ? Path.Combine(_environment.ProjectsDirectory, ProjectService.SanitizeFileName(name))
            : Path.GetFullPath(req.Directory!);
        var projectFile = Path.Combine(root, ProjectService.SanitizeFileName(name) + ".lmeproj");
        if (File.Exists(projectFile))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"同名项目已存在，未覆盖：{projectFile}（请换一个名称或目录）");

        var project = await _projects.CreateAsync(root, name);
        _projectState.SetProject(project, projectFile);
        _environment.RegisterRecentProject(projectFile, project.Name);
        return IpcResponse.Success(request.Id, new ProjectCreateResponse(projectFile, project.Name, root));
    }

    /// <summary>
    /// 最近项目：读共享配置里登记的条目（程序目录 config/shared-config.json）。
    /// 配置里没有（或文件已被删掉）→ 空列表 + 中文说明，不编造记录。
    /// </summary>
    private IpcResponse HandleProjectRecent(IpcRequest request)
    {
        var recorded = _environment.Config.RecentProjects;
        var alive = recorded.Where(x => File.Exists(x.Path)).ToList();
        var dropped = recorded.Count - alive.Count;
        var items = alive
            .OrderByDescending(x => x.LastOpenedAt)
            .Select(x => new ProjectRecentItem(x.Name, x.Path, x.LastOpenedAt.ToString("O")))
            .ToList();
        var info = items.Count == 0
            ? recorded.Count == 0
                ? "还没有最近项目记录（新建或打开一个项目后会出现在这里）"
                : "最近项目记录里的文件都已不存在（可能已被移动或删除）"
            : dropped > 0
                ? $"共 {items.Count} 个可打开的项目（另有 {dropped} 条记录的文件已不存在，已隐藏）"
                : $"共 {items.Count} 个最近项目";
        return IpcResponse.Success(request.Id, new ProjectRecentResponse(items, info));
    }

    private async Task<IpcResponse> HandleProjectSaveAsync(IpcRequest request)
    {
        var req = DeserializePayload<ProjectSaveRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var projectFile = _projectState.ProjectFile ?? req.ProjectFile;

        await _projects.SaveAsync(project, projectFile);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    private IpcResponse HandleExportPlan(IpcRequest request)
    {
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var langEdits = _projectState.LangEdits ?? new LangEditSession();
        var staticEdits = _projectState.StaticEdits ?? new StaticEditSession();

        var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LME_Export");
        var plan = _exportPlan.Plan(project, targetDir, langEdits, staticEdits);
        var slots = plan.Items.Select(i => new ExportSlot(i.Descriptor.DisplayName, i.Directory, i.ArtifactCount)).ToList();
        var skipped = plan.Items.Where(i => !i.Planned).Select(i => i.SkipReason ?? "未知原因").ToList();
        return IpcResponse.Success(request.Id, new ExportPlanResponse(slots, skipped));
    }

    private async Task<IpcResponse> HandleExportRunAsync(IpcRequest request)
    {
        var req = DeserializePayload<ExportRunRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var langEdits = _projectState.LangEdits ?? new LangEditSession();
        var staticEdits = _projectState.StaticEdits ?? new StaticEditSession();

        var context = new ModExportPlanContext(
            _environment.EffectiveUnityCacheDirectory(project),
            _environment.EffectiveFmodLibraryDirectory(project));
        var plan = _exportPlan.Plan(project, req.TargetDirectory, langEdits, staticEdits, context);
        var projectRoot = _projectState.ProjectFile != null ? Path.GetDirectoryName(_projectState.ProjectFile)! : string.Empty;
        var result = await _exportService.ExportAsync(project, projectRoot, plan, context,
            new Progress<string>(msg => Log.Info("导出进度: {0}", msg)), CancellationToken.None);

        // 计划与结果按槽位顺序一一对应（写出阶段就是按 plan.Items 逐条走的），
        // 直接同位拼接，不重新算跳过原因。
        var items = new List<ExportRunItem>();
        var skipped = new List<string>();
        for (var i = 0; i < result.Slots.Count; i++)
        {
            var slot = result.Slots[i];
            var planned = i < plan.Items.Count ? plan.Items[i] : null;
            var warnings = planned?.Warnings ?? [];
            if (slot.Written)
            {
                // 写出成功时 slot.Diagnostics 是「产物怎么用」的提示，不是错误：并进 warnings 展示。
                items.Add(new ExportRunItem(slot.Descriptor.DisplayName, true, slot.Directory,
                    slot.ArtifactCount, slot.OutputPaths, [], warnings.Concat(slot.Diagnostics).ToArray()));
                continue;
            }
            var reasons = slot.Diagnostics.Count > 0 ? slot.Diagnostics
                : planned?.SkipReason is { } reason ? [reason]
                : ["没有可导出的修改"];
            items.Add(new ExportRunItem(slot.Descriptor.DisplayName, false, slot.Directory,
                0, [], reasons, warnings));
            skipped.Add($"{slot.Descriptor.DisplayName}：{reasons[0]}");
        }

        var info = result.WrittenSlotCount == 0
            ? $"没有写出任何产物（{result.Slots.Count} 个槽位全部跳过）→ {result.RootDirectory}"
            : $"已写出 {result.WrittenSlotCount} 个槽位 / {result.WrittenFileCount} 个产物 → {result.RootDirectory}";
        return IpcResponse.Success(request.Id, new ExportRunResponse(true, result.RootDirectory, result.ModName,
            items.Count, result.WrittenSlotCount, result.WrittenFileCount, items, skipped, info));
    }

    // ── 2.7 配置 / UI 状态 ──────────────────────────────────────────

    private IpcResponse HandleConfigRead(IpcRequest request)
    {
        var req = DeserializePayload<ConfigReadRequest>(request);
        var value = SharedConfigKeyValue.Read(req.Key);
        return IpcResponse.Success(request.Id, new ConfigReadResponse(value));
    }

    private IpcResponse HandleConfigWrite(IpcRequest request)
    {
        var req = DeserializePayload<ConfigWriteRequest>(request);
        SharedConfigKeyValue.Write(req.Key, req.Value);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    /// <summary>
    /// 目录自动探测：共享配置 → 当前项目（旧字段）→ 自动发现。
    /// 走的是 AppEnvironment 既有那一套（<see cref="AppEnvironment.ApplyAutoConfigure"/>），
    /// 不新写一份探测；探不到的项给 null，<b>不猜路径</b>。
    /// </summary>
    private IpcResponse HandleConfigAutoDetect(IpcRequest request)
    {
        var project = _projectState.Project;
        var report = _environment.ApplyAutoConfigure(project);
        var game = _environment.EffectiveGameDirectory(project);
        var cache = _environment.EffectiveUnityCacheDirectory(project);
        var mod = _environment.EffectiveModDirectory(project);
        var fmod = _environment.EffectiveFmodLibraryDirectory(project);

        var missing = new List<string>();
        if (game is null) missing.Add("游戏目录");
        if (cache is null) missing.Add("Unity 缓存目录");
        if (mod is null) missing.Add("模组目录");
        if (fmod is null) missing.Add("FMOD DLL 目录");
        var info = missing.Count == 0
            ? $"四个目录都已就绪（本次自动配置：{report.Describe()}）"
            : $"本次自动配置：{report.Describe()}；仍未找到：{string.Join("、", missing)}（可用 config.write 手动指定）";
        return IpcResponse.Success(request.Id, new ConfigAutoDetectResponse(game, cache, mod, fmod, info));
    }

    private IpcResponse HandleUiStateRead(IpcRequest request)
    {
        var req = DeserializePayload<UiStateReadRequest>(request);
        var width = req.ColumnWidth is { } w ? Math.Clamp(w, 260, 2000) : 360;
        return IpcResponse.Success(request.Id, new { pageKey = req.PageKey, columnWidth = width });
    }

    private IpcResponse HandleUiStateWrite(IpcRequest request)
    {
        var req = DeserializePayload<UiStateWriteRequest>(request);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    private IpcResponse HandleCancel(IpcRequest request)
    {
        var req = DeserializePayload<CancelPayload>(request);
        var cancelled = CancellationTokenStore.Cancel(req.OperationId);
        Log.Info("IPC 取消请求：operationId={0}，结果={1}", req.OperationId, cancelled ? "已取消" : "未找到");
        return IpcResponse.Success(request.Id, new { cancelled });
    }

    // ── 音频工作台 ────────────────────────────────────────────────

    private IpcResponse HandleBankList(IpcRequest request)
    {
        var req = DeserializePayload<BankListRequest>(request);
        var entries = _bankIndex.Store.ReadEntries();
        var items = entries.Skip(req.Offset).Take(req.Take)
            .Select(e => new BankListItem(e.Path, e.FileName, e.Samples.Count, e.SizeBytes)).ToList();
        return IpcResponse.Success(request.Id, new BankListResponse(items, entries.Count, req.Offset, req.Take));
    }

    private IpcResponse HandleBankSamples(IpcRequest request)
    {
        var req = DeserializePayload<BankSamplesRequest>(request);
        var samples = _bankIndex.Store.ReadSamples(req.BankId);
        var items = samples.Select(s => new SampleListItem(s.Name, s.DurationSeconds ?? 0, s.CodecName, s.Channels, s.SampleRate)).ToList();
        return IpcResponse.Success(request.Id, new BankSamplesResponse(req.BankId, items));
    }

    /// <summary>
    /// bank.preview：<b>一条样本 → 一个可播放地址</b>。
    ///
    /// <para>链路与维基语音试听<b>完全同一条</b>（<see cref="WikiMediaResolver"/>）：
    /// <c>bank 路径 + 样本名 → bank 索引行 → FSB 分片 → FMOD 解码为 WAV → 落到
    /// wwwroot/data/wiki → https://lme.data/wiki/{hash}.wav</c>。不另写一套解码，
    /// 同一条样本在维基页与音频工作台拿到的是同一个地址（缓存与复用也一并继承）。</para>
    ///
    /// <para>解不出来（bank 索引未就绪 / 索引里没这条样本 / FSB 取不到 / FMOD DLL 不可用）
    /// 一律 <see cref="IpcErrorCode.Unsupported"/> + 中文原因，不返回空地址、不编造。</para>
    /// </summary>
    private async Task<IpcResponse> HandleBankPreviewAsync(IpcRequest request)
    {
        var req = DeserializePayload<BankPreviewRequest>(request);
        if (string.IsNullOrWhiteSpace(req.BankId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少 bank（bankId）");
        if (string.IsNullOrWhiteSpace(req.SampleName))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少样本名（sampleName）");

        // 复用解析器的音频入口：它就吃「bank 路径 + '\0' + 样本名」这份深链载荷。
        var binding = new WikiResourceBinding("bank-preview", "bank-preview", req.BankId, "Audio", req.SampleName, 0)
        {
            DeepLink = RelationDeepLink.ForAudio(req.BankId, req.SampleName),
        };

        WikiMediaResolution resolution;
        try
        {
            resolution = await _wikiMedia.ResolveAsync(binding, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "bank.preview 解码失败：bank={0} sample={1}", req.BankId, req.SampleName);
            return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, $"读取 bank 失败：{ex.Message}");
        }

        if (resolution.AudioUrl is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported,
                "这条样本暂时给不出可播放地址：bank 索引未就绪 / 索引里没有这条样本 / FSB 取不到 / FMOD 解码器不可用（详见 logs/current.log）。");

        return IpcResponse.Success(request.Id, new BankPreviewResponse(req.BankId, req.SampleName, resolution.AudioUrl));
    }

    private async Task<IpcResponse> HandleBankExportRebankAsync(IpcRequest request)
    {
        var req = DeserializePayload<BankExportRebankRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.BankId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到 Bank：{req.BankId}");

        var fsb = await _bankAudio.ReadFsbAsync(asset);
        return IpcResponse.Success(request.Id, new { ok = true, bytesWritten = fsb.Length });
    }

    // ── 文本工作台 ────────────────────────────────────────────────

    /// <summary>
    /// 定位 lang 根：<b>共享配置 → 当前项目</b>（<see cref="AppEnvironment.EffectiveGameDirectory"/>）。
    ///
    /// <para>为什么不能直接把 null 交给服务：它只认显式游戏目录（传 null 恒返回 null），
    /// 那样文本工作台会一律得到「未配置游戏目录」。</para>
    /// </summary>
    private string? LangRoot() => _langText.ResolveLangRoot(AppEnvironment.Current.EffectiveGameDirectory(_projectState.Project));

    private IpcResponse HandleLangActiveLanguages(IpcRequest request)
    {
        var langRoot = LangRoot();
        var activeLang = langRoot is not null ? _langText.ReadActiveLanguage(langRoot) : null;
        var languages = new List<string>();
        if (activeLang is not null) languages.Add(activeLang);
        return IpcResponse.Success(request.Id, new LangActiveLanguagesResponse(languages));
    }

    private IpcResponse HandleLangFiles(IpcRequest request)
    {
        var req = DeserializePayload<LangFilesRequest>(request);
        var langRoot = LangRoot();
        if (langRoot is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "未配置游戏目录");
        var files = _langText.EnumerateFiles(langRoot);
        var items = files.Skip(req.Offset).Take(req.Take)
            .Select(f => new LangFileInfoItem(f.RelativePath, f.SizeBytes, f.KeyCount, f.IsUtf8)).ToList();
        return IpcResponse.Success(request.Id, new LangFilesResponse(items, files.Count, req.Offset, req.Take));
    }

    private IpcResponse HandleLangSearch(IpcRequest request)
    {
        var req = DeserializePayload<LangSearchRequest>(request);
        var langRoot = LangRoot();
        if (langRoot is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "未配置游戏目录");
        var files = _langText.EnumerateFiles(langRoot);
        var hits = _langText.Search(req.Keyword, files, req.Take, IpcGatewayConstants.DefaultPageSize);
        var items = hits.Select(h => new LangSearchHitItem(h.RelativePath, h.Kind.ToString(), h.KeyPath, h.Snippet)).ToList();
        return IpcResponse.Success(request.Id, new LangSearchResponse(items, hits.Count, 0, req.Take));
    }

    private IpcResponse HandleLangReadEntry(IpcRequest request)
    {
        var req = DeserializePayload<LangReadEntryRequest>(request);
        var text = _langText.TryGetModifiedText(req.RelativePath) ?? _langText.TryGetVanillaText(req.RelativePath);
        return IpcResponse.Success(request.Id, new LangReadEntryResponse(text));
    }

    private IpcResponse HandleLangEditEntry(IpcRequest request)
    {
        var req = DeserializePayload<LangEditEntryRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        _langText.BeginEdit(req.RelativePath);
        _langText.SetModified(req.RelativePath, req.Value);
        return IpcResponse.Success(request.Id, new { ok = true });
    }

    /// <summary>
    /// lang.fileEntries：一个 lang 文件的<b>键值分页</b>（深链 <c>?file=&amp;key=</c> 用它在页内滚动定位）。
    ///
    /// <para>键路径口径与搜索命中完全一致（同一份叶子遍历），所以搜索结果里的 keyPath
    /// 一定能在这里翻到。文本取「编辑集里的当前文本」，没有则读原文。</para>
    /// </summary>
    private IpcResponse HandleLangFileEntries(IpcRequest request)
    {
        var req = DeserializePayload<LangFileEntriesRequest>(request);
        if (string.IsNullOrWhiteSpace(req.RelativePath))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少文件路径（relativePath）");

        var text = _langText.TryReadText(req.RelativePath);
        if (text is null)
        {
            var langRoot = LangRoot();
            if (langRoot is null)
                return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "未配置游戏目录");
            _langText.AttachLangRoot(langRoot);
            text = _langText.TryReadText(req.RelativePath);
        }
        if (text is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"读不到该文件：{req.RelativePath}（请先调用 lang.files 定位 lang 根；文件需是合法 UTF-8 JSON）");

        var entries = LangTextWorkbenchService.Entries(text);
        var take = Math.Clamp(req.Take <= 0 ? IpcGatewayConstants.DefaultPageSize : req.Take, 1, 500);
        var offset = Math.Max(0, req.Offset);
        var items = entries.Skip(offset).Take(take)
            .Select(e => new LangFileEntryItem(e.KeyPath, e.Value)).ToList();
        return IpcResponse.Success(request.Id,
            new LangFileEntriesResponse(req.RelativePath, items, entries.Count, offset, take));
    }

    /// <summary>
    /// text.fileTreeChildren：目录树懒加载（一次一层）。
    /// 数据源是 <see cref="LangTextWorkbenchService.EnumerateFiles"/> 同一套相对路径
    /// （相对活动语言目录），不另走一遍磁盘、不另造枚举口径。
    /// </summary>
    private IpcResponse HandleLangFileTreeChildren(IpcRequest request)
    {
        var req = DeserializePayload<LangFileTreeRequest>(request);
        var langRoot = LangRoot();
        if (langRoot is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "未配置游戏目录，列不出文本目录树");

        var files = _langText.EnumerateFiles(langRoot);
        var nodes = LangFileTree.ChildrenOf(files.Select(f => f.RelativePath), req.ParentPath);
        var where = string.IsNullOrWhiteSpace(req.ParentPath) ? "根目录" : req.ParentPath;
        var info = nodes.Count == 0
            ? $"{where}下没有子节点（lang 根：{langRoot}）"
            : $"{where}：{nodes.Count(x => !x.IsLeaf)} 个子目录 / {nodes.Count(x => x.IsLeaf)} 个文件（共 {files.Count} 个 lang 文件）";
        return IpcResponse.Success(request.Id, new LangFileTreeResponse(nodes, info));
    }

    /// <summary>
    /// lang.applyPatch：<b>按 key 写回</b>——只改给定键，文件里其它内容原样保留。
    ///
    /// <para>为什么不能复用 lang.editEntry：后者是「整份文本替换」，逐条调用会互相覆盖
    /// （第二条把第一条的改动冲掉）。这里先取当前文本、按 key 打补丁、再整体写回编辑集。</para>
    ///
    /// <para>写回仍走既有编辑集（<c>BeginEdit</c> 留官方基线 + <c>SetModified</c>），
    /// 并向当前项目的 <c>Edits</c> 追加一条 PatchJson 记录，导出清单才看得到。</para>
    /// </summary>
    private IpcResponse HandleLangApplyPatch(IpcRequest request)
    {
        var req = DeserializePayload<LangApplyPatchRequest>(request);
        if (string.IsNullOrWhiteSpace(req.RelativePath))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少文件路径（relativePath）");
        if (req.Edits is null || req.Edits.Count == 0)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "没有要应用的改动（edits 为空）");

        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        string baseText;
        try
        {
            var langRoot = LangRoot();
            if (langRoot is null)
                return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "未配置游戏目录");
            _langText.AttachLangRoot(langRoot);
            baseText = _langText.TryGetModifiedText(req.RelativePath) ?? _langText.BeginEdit(req.RelativePath);
        }
        catch (FileNotFoundException ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"lang 文件不存在：{ex.FileName}");
        }
        catch (InvalidDataException ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, ex.Message);
        }

        var edits = req.Edits.Select(e => new LangKeyEdit(e.KeyPath, e.Value)).ToList();
        LangPatchOutcome outcome;
        try
        {
            outcome = LangTextWorkbenchService.ApplyEdits(baseText, edits);
        }
        catch (JsonException ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"该文件不是合法 JSON，无法按 key 打补丁：{req.RelativePath}（{ex.Message}）");
        }

        if (outcome.Applied.Count == 0)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"一条都没生效：{req.RelativePath} 里找不到这些键 —— {string.Join("、", edits.Select(e => e.KeyPath).Take(5))}");

        _langText.SetModified(req.RelativePath, outcome.Text);
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.PatchJson,
            TargetPath = req.RelativePath,
        });

        var info = $"已按 key 写入 {req.RelativePath}：生效 {outcome.Applied.Count} 条";
        if (outcome.Missing.Count > 0) info += $"，找不到 {outcome.Missing.Count} 条";
        if (outcome.Rejected.Count > 0) info += $"，拒绝 {outcome.Rejected.Count} 条（数组元素不支持删除）";
        return IpcResponse.Success(request.Id, new LangApplyPatchResponse(
            req.RelativePath, outcome.Applied, outcome.Missing, outcome.Rejected, info));
    }

    private IpcResponse HandleLangExportPatch(IpcRequest request)
    {
        var req = DeserializePayload<LangExportPatchRequest>(request);
        var report = _langText.ExportPatch(req.TargetDirectory);
        return IpcResponse.Success(request.Id, new { ok = true, editedFiles = report.EditedFileCount, patchedFiles = report.PatchedFileCount });
    }

    // ── 静态数据工作台 ────────────────────────────────────────────

    private IpcResponse HandleStaticTableList(IpcRequest request)
    {
        var req = DeserializePayload<StaticTableListRequest>(request);
        var entries = _staticIndex.Store.ReadEntries();
        var items = entries.Skip(req.Offset).Take(req.Take)
            .Select(e => new StaticTableItem(e.ContainerEntry, e.Name, 0)).ToList();
        return IpcResponse.Success(request.Id, new StaticTableListResponse(items, entries.Count, req.Offset, req.Take));
    }

    /// <summary>
    /// static.records：一张静态表的记录分页。
    ///
    /// <para>链路全部复用既有能力：<see cref="StaticIndexService.LocateForReads"/> 定位 bundle →
    /// 索引里按 <c>tableId</c>（容器路径或表名）取表元数据 →
    /// <see cref="StaticIndexService.LoadDocumentAsync"/> 读正文（<c>documents</c> 缓存优先）→
    /// <see cref="StaticTableRows.Parse"/> 解析行集合 → 按 offset/take 取一页。</para>
    ///
    /// <para>前提缺失一律中文错误、不返回空列表充数（未开项目 / 定位不到 bundle /
    /// 索引里没这张表 / 正文读不到）。</para>
    /// </summary>
    private async Task<IpcResponse> HandleStaticRecordsAsync(IpcRequest request)
    {
        var req = DeserializePayload<StaticRecordsRequest>(request);
        if (string.IsNullOrWhiteSpace(req.TableId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少表 id（tableId）");

        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var location = StaticIndexService.LocateForReads(_staticIndex.Store, project.GameDirectory,
            StaticIndexService.CacheRoots(project.UnityCacheDirectory));
        if (location is null || !location.IsCached)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                "无法定位静态数据 bundle（运行时 catalog 与 Unity 缓存均未命中）。请确认游戏目录与 Unity 缓存目录已配置。");

        var entries = _staticIndex.Store.ReadEntries();
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.ContainerEntry, req.TableId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, req.TableId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"静态表索引里没有这张表：{req.TableId}（索引共 {entries.Count} 张表）");

        var document = await _staticIndex.LoadDocumentAsync(location, entry).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(document.Text))
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"表 {entry.Name} 的正文读不到（非 UTF-8，或该条目已不在当前 bundle 里）。");

        var rows = StaticTableRecords.Rows(document.Text);
        if (rows.Count == 0)
            return IpcResponse.Success(request.Id, new StaticRecordsResponse(req.TableId, [], 0, 0, 0,
                $"表 {entry.Name} 的正文没能解析出记录（正文 {entry.SizeLabel}，不是 list/数组/单对象形态）。"));

        var take = req.Take <= 0 ? IpcGatewayConstants.DefaultPageSize : req.Take;
        var offset = Math.Max(0, req.Offset);
        var items = StaticTableRecords.Page(rows, offset, take)
            .Select(r => new StaticRecordItem(r.RecordId, r.Summary, r.RawJson)).ToList();
        return IpcResponse.Success(request.Id,
            new StaticRecordsResponse(req.TableId, items, rows.Count, offset, take));
    }

    private IpcResponse HandleStaticLocate(IpcRequest request)
    {
        var req = DeserializePayload<StaticLocateRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        // 使用既有 StaticBundleLocator 定位静态数据 bundle（运行时 catalog → 安装目录 catalog）
        var gameDir = project.GameDirectory;
        var cacheRoots = new[] { project.UnityCacheDirectory }.Where(s => !string.IsNullOrWhiteSpace(s));
        var location = StaticBundleLocator.Locate(gameDir, cacheRoots);

        if (location is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"无法定位静态数据 bundle（运行时 catalog 与安装目录 catalog 均未命中）。请确认游戏目录与 Unity 缓存目录已配置。");

        // 在静态表索引中查找目标表的偏移
        var entries = _staticIndex.Store.ReadEntries();
        var index = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].ContainerEntry == req.TableId || entries[i].Name == req.TableId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"在静态表索引中未找到表：{req.TableId}。共 {entries.Count} 张表。");

        // 计算页偏移（每页 200 条）
        var offset = index / IpcGatewayConstants.DefaultPageSize * IpcGatewayConstants.DefaultPageSize;
        var tableEntry = entries[index];

        return IpcResponse.Success(request.Id, new
        {
            offset,
            page = offset / IpcGatewayConstants.DefaultPageSize,
            tableName = tableEntry.Name,
            tableIndex = index,
            bundleFound = location.IsCached,
            bundleName = location.BundleName
        });
    }

    private IpcResponse HandleStaticReadRecord(IpcRequest request)
    {
        // 静态表读取需要 bundle 定位 + entry（W3 实现）
        return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, "静态表读取需要 bundle 定位（W3 实现）");
    }

    private IpcResponse HandleStaticEditRecord(IpcRequest request)
    {
        return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, "静态数据编辑需要文档加载（W3 实现）");
    }

    private IpcResponse HandleStaticExportStaticmod(IpcRequest request)
    {
        var req = DeserializePayload<StaticExportStaticmodRequest>(request);
        var staticEdits = _projectState.StaticEdits ?? new StaticEditSession();
        var entries = staticEdits.Snapshot().Select(e => (
            DataClass: e.Entry.DataClass,
            File: e.Entry.FileName,
            Container: (string?)e.Entry.ContainerEntry,
            OfficialJsonPath: e.OfficialText,
            ModifiedJsonPath: e.ModifiedText
        )).ToList();
        var package = _staticMod.CreateJsonPatchPackage("mod", "1.0.0", "", entries);
        var outputPath = Path.Combine(req.TargetDirectory, "mod.staticmod");
        Directory.CreateDirectory(req.TargetDirectory);
        _staticMod.Write(package, outputPath);
        return IpcResponse.Success(request.Id, new { ok = true, written = package.Patches.Count, skipped = 0, outputPath });
    }

    // ── 维基页面数据处理器 ────────────────────────────────────────

    private static readonly Dictionary<string, string> WikiCategoryLabels = new()
    {
        ["persona"] = "人格",
        ["enemy"] = "敌方单位",
        ["abnormality"] = "异想体",
        ["ego"] = "E.G.O 装备",
        ["ego_gift"] = "E.G.O 饰品",
        ["announcer"] = "播报员",
        ["story"] = "剧情",
        ["stage"] = "关卡",
        ["item"] = "物品",
        ["mechanism"] = "机制",
        ["keyword"] = "关键词",
    };

    private IpcResponse HandleWikiHome(IpcRequest request)
    {
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        if (!store.Exists)
            return IpcResponse.Success(request.Id, new WikiHomeResponse(
                Array.Empty<WikiCategoryCardDto>(), Array.Empty<WikiRecentPageDto>(),
                new WikiStatsDto(0, 0, 0, 0)));

        var categories = WikiCategoryLabels.Select(cat =>
        {
            var count = store.CountByCategory(cat.Key);
            return new WikiCategoryCardDto(cat.Key, cat.Value, "", count, Array.Empty<CategoryPageRefDto>());
        }).ToList();

        var recent = store.ReadPages().Take(5).Select(p =>
            new WikiRecentPageDto(p.PageId, p.Title, p.Category, "")).ToList();

        var (auto, revised) = store.SourceStatistics();
        var stats = new WikiStatsDto(store.ReadPageCount(), store.ReadEntryCount(), store.ReadBindingCount(), revised);

        return IpcResponse.Success(request.Id, new WikiHomeResponse(categories, recent, stats));
    }

    private IpcResponse HandleWikiCategoryIndex(IpcRequest request)
    {
        var req = DeserializePayload<WikiCategoryIndexRequest>(request);
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        if (!store.Exists || !WikiCategoryLabels.TryGetValue(req.Category, out var label))
            return IpcResponse.Success(request.Id, new WikiCategoryIndexResponse(req.Category, req.Category, Array.Empty<CategoryPageRefDto>()));

        var pages = store.ReadPages().Where(p => p.Category == req.Category).Select(p =>
            new CategoryPageRefDto(p.PageId, p.Title, null)).ToList();

        return IpcResponse.Success(request.Id, new WikiCategoryIndexResponse(req.Category, label, pages));
    }

    private async Task<IpcResponse> HandleWikiPageLoadAsync(IpcRequest request)
    {
        var req = DeserializePayload<WikiPageLoadRequest>(request);
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        if (!store.Exists)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"页面不存在：{req.PageId}");

        var service = new WikiPageQueryService(store);
        var detail = service.GetPageDetail(req.PageId);
        if (detail is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"页面不存在：{req.PageId}");

        var page = detail.Page;

        // 绑定给的是 Unity 容器路径，先整页解析一遍（按 ref_key 去重、带配额）。
        // 分节、画廊、封面共用这一份结果 —— 同一张图不解码两次。
        var allBindings = detail.SubPages.SelectMany(sub => sub.Entries)
            .SelectMany(entry => entry.Bindings).ToList();
        var resolutions = await _wikiMedia.ResolveManyAsync(allBindings).ConfigureAwait(false);
        var byBinding = new Dictionary<WikiResourceBinding, WikiMediaResolution>(allBindings.Count);
        for (var i = 0; i < allBindings.Count; i++) byBinding.TryAdd(allBindings[i], resolutions[i]);

        WikiMediaResolution ResolutionOf(WikiResourceBinding binding)
            => byBinding.TryGetValue(binding, out var resolution) ? resolution : WikiMediaResolution.None;

        // 分节 = sub_pages，条目挂分节下，绑定挂条目下（不再是「一条 entry 假装一个分节」）。
        var sections = detail.SubPages.Select(sub => new WikiSectionDto(
            sub.SubPage.SubPageId,
            sub.SubPage.Title,
            string.Empty,
            Collapsible: true,
            Editable: false,
            sub.Entries.Select(entry => new WikiEntryDto(
                entry.Entry.EntryId,
                entry.Entry.Title,
                entry.Entry.Body,
                entry.Entry.Authority,
                entry.Entry.Confidence,
                entry.Entry.SourceDetail)).ToList(),
            sub.Entries.SelectMany(entry => entry.Bindings).Select(binding =>
            {
                var resolution = ResolutionOf(binding);
                return new WikiBindingDto(
                    binding.RefKey,
                    binding.Kind,
                    binding.Display,
                    binding.MediaKind,
                    binding.DurationSec,
                    resolution.MediaUrl ?? MediaUrlOf(binding),
                    resolution.AudioUrl,
                    resolution.SkeletonUrl,
                    resolution.AtlasUrl,
                    resolution.TextureUrls);
            }).ToList())).ToList();

        // 画廊：只收图片绑定；拿不到真实地址的项 Url 为 null，前端按降级处理。
        var gallery = allBindings
            .Where(binding => string.Equals(binding.Kind, "Image", StringComparison.OrdinalIgnoreCase))
            .Select(binding => new WikiGalleryItemDto(
                ResolutionOf(binding).MediaUrl ?? MediaUrlOf(binding),
                binding.Display ?? binding.RefKey, binding.Kind))
            .Take(MaxGalleryItems)
            .ToList();

        var cover = await _wikiMedia.ResolveImageAsync(page.CoverRef).ConfigureAwait(false);

        // 信息框：只写本地确实拿得到的字段（类别名/标题/副标题/分节数/条目数），不编造。
        // 人格页另外把「数据」分节的条目补成真实字段（静态表行级数据生成，
        // 见 PersonaBaseDataSections）：条目标题当标签、正文首行当值；缺数据就不补。
        var infobox = new List<WikiInfoboxRowDto>();
        if (!string.IsNullOrWhiteSpace(page.CategoryLabel))
            infobox.Add(new WikiInfoboxRowDto("类别", page.CategoryLabel));
        if (!string.IsNullOrWhiteSpace(page.Subtitle))
            infobox.Add(new WikiInfoboxRowDto("副标题", page.Subtitle));
        if (string.Equals(page.Category, RelationCategories.Persona, StringComparison.Ordinal))
        {
            var dataSection = sections.FirstOrDefault(s =>
                string.Equals(s.Title, "数据", StringComparison.Ordinal));
            if (dataSection is not null)
            {
                foreach (var entry in dataSection.Entries.Take(MaxInfoboxDataRows))
                {
                    if (string.IsNullOrWhiteSpace(entry.Title)) continue;
                    var value = InfoboxValueOf(entry.Body);
                    if (value is null) continue;
                    infobox.Add(new WikiInfoboxRowDto(entry.Title, value));
                }
            }
        }
        infobox.Add(new WikiInfoboxRowDto("分节", sections.Count.ToString(CultureInfo.InvariantCulture)));
        infobox.Add(new WikiInfoboxRowDto("条目",
            sections.Sum(s => s.Entries.Count).ToString(CultureInfo.InvariantCulture)));

        return IpcResponse.Success(request.Id, new WikiPageDto(
            page.PageId, page.Title, page.Category, page.Subtitle, sections,
            Array.Empty<WikiRelatedPageDto>(),
            infobox,
            string.IsNullOrWhiteSpace(page.CategoryLabel) ? Array.Empty<string>() : [page.CategoryLabel],
            gallery,
            cover.MediaUrl ?? CoverUrlOf(page.CoverRef)));
    }

    /// <summary>画廊最多给多少项（超出截断，避免整页塞满同名静态图）。</summary>
    private const int MaxGalleryItems = 24;

    /// <summary>信息框最多从「数据」分节补多少条真实字段。</summary>
    private const int MaxInfoboxDataRows = 12;

    /// <summary>信息框字段值 = 正文首行（多行条目只取第一行，避免把整段塞进信息框）。</summary>
    private static string? InfoboxValueOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var firstLine = body.Split('\n')[0].Trim();
        return firstLine.Length == 0 ? null : firstLine;
    }

    /// <summary>
    /// 资源的可展示地址。二进制走 <c>lme.data</c> 虚拟主机（禁止 base64）；
    /// <c>deep_link</c> / <c>ref_key</c> 是**容器路径不是 URL**，据此拼地址会造出不存在的链接，
    /// 所以这里只在它本身就是地址时才给，否则返回 null（前端降级、不占位）。
    /// </summary>
    private static string? MediaUrlOf(WikiResourceBinding binding)
    {
        var candidate = binding.DeepLink;
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        return IsUrl(candidate) ? candidate : null;
    }

    private static string? CoverUrlOf(string? coverRef)
        => !string.IsNullOrWhiteSpace(coverRef) && IsUrl(coverRef) ? coverRef : null;

    private static bool IsUrl(string value)
        => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("lme.", StringComparison.OrdinalIgnoreCase);

    private IpcResponse HandleWikiSearch(IpcRequest request)
    {
        var req = DeserializePayload<WikiSearchRequest>(request);
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        if (!store.Exists)
            return IpcResponse.Success(request.Id, new WikiSearchResponse(0, Array.Empty<WikiSearchResultDto>()));

        var (total, entries) = new WikiPageQueryService(store).SearchEntries(req.Keyword, req.Offset, req.Limit);
        return IpcResponse.Success(request.Id, new WikiSearchResponse(
            total,
            entries.Select(r => new WikiSearchResultDto(r.SubPageId, r.Title, "", r.Body.Length > 100 ? r.Body[..100] + "…" : r.Body, $"/wiki/page/{r.SubPageId}")).ToList()));
    }

    private IpcResponse HandleWikiPageSave(IpcRequest request)
    {
        var req = DeserializePayload<WikiPageSaveRequest>(request);
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        var page = new WikiPage(req.Page.Id, req.Page.Title, req.Page.Category, req.Page.Subtitle ?? "", req.Page.Id);
        store.InsertPageIfNotExists(page);
        return IpcResponse.Success(request.Id, new WikiSaveResponse(true, null));
    }

    private IpcResponse HandleWikiSaveContent(IpcRequest request)
    {
        var req = DeserializePayload<WikiSaveContentRequest>(request);

        // 前端发的是 {pageId, sectionId, content}，sectionId 里装的其实是条目 id
        // （wiki.page.load 的 WikiSectionDto.Id 就是 entry_id）。两个口径都收，不再造孤儿行。
        var entryId = FirstNonEmpty(req.EntryId, req.SectionId);
        if (string.IsNullOrWhiteSpace(entryId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少条目 id：无法定位要保存的内容项。");

        var value = req.NewValue ?? req.Content;
        if (value is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少要保存的内容（newValue / content 都为空）。");

        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        var existing = store.ReadEntry(entryId!);
        if (existing is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"页面库里没有这个条目：{entryId}");

        // 保持条目挂在原分节下，只改正文 + 打上 revised（生成时据此跳过、不覆盖）
        store.SaveEntry(existing with { Body = value, Source = WikiEntrySources.Revised });
        return IpcResponse.Success(request.Id, new WikiSaveResponse(true, null));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    /// <summary>
    /// 生成维基页面（G-01 的生产链路入口）。
    /// 走 <see cref="WikiAutoGenerationService"/>：读本地事实源 → 权威引擎 → 编排 → 落
    /// <c>cache/wiki-pages.db</c>。进度按契约 §4 推 <c>progress</c> 事件。
    /// </summary>
    private async Task<IpcResponse> HandleWikiGenerateAsync(IpcRequest request)
    {
        var req = DeserializePayload<WikiGenerateRequest>(request);
        var operationId = string.IsNullOrWhiteSpace(req.OperationId) ? "wiki-generate" : req.OperationId!;
        var cancellationToken = CancellationTokenStore.Register(operationId);
        try
        {
            var service = new WikiAutoGenerationService(AppEnvironment.Current);
            var result = await service.GenerateAsync(
                _projectState.Project, CreateProgressSink(operationId), cancellationToken);

            if (!result.Ok)
                return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, result.Message);

            return IpcResponse.Success(request.Id, new WikiGenerateResponse(
                true,
                result.Message,
                result.PageCount,
                result.SubPageCount,
                result.EntryCount,
                result.BindingCount,
                result.WrittenEntries,
                result.RevisedPreserved,
                result.UnknownSourceEntries,
                (long)result.Elapsed.TotalMilliseconds,
                service.CreateStore().DatabasePath,
                result.Categories.Select(c => new WikiCategoryCountDto(c.Category, c.Label, c.PageCount)).ToList()));
        }
        finally
        {
            CancellationTokenStore.Complete(operationId);
        }
    }

    /// <summary>维基页库的当前状态（前端据此决定要不要提示「生成页面」）。</summary>
    private IpcResponse HandleWikiGenerateStatus(IpcRequest request)
    {
        var store = new WikiPageStore(AppEnvironment.Current.CacheDirectory);
        var exists = store.Exists;
        var query = new WikiPageQueryService(store);
        var (_, revised) = store.SourceStatistics();
        var categories = new List<WikiCategoryCountDto>();
        foreach (var (category, label) in WikiCategoryLabels)
        {
            var count = store.CountByCategory(category);
            if (count > 0) categories.Add(new WikiCategoryCountDto(category, label, count));
        }
        return IpcResponse.Success(request.Id, new WikiGenerateStatusResponse(
            query.IsReady, exists, store.DatabasePath,
            store.ReadPageCount(), store.ReadSubPageCount(), store.ReadEntryCount(),
            store.ReadBindingCount(), revised, categories));
    }

    /// <summary>
    /// 把生成进度转成一个节流的 <see cref="IProgress{T}"/>：首条与末条必达，中间 200 ms 节流
    /// （契约 §4：避免淹掉 WebView2 消息队列）。
    /// </summary>
    private IProgress<WikiGenerationProgress> CreateProgressSink(string operationId)
    {
        var stopwatch = Stopwatch.StartNew();
        long lastSent = -1;
        return new Progress<WikiGenerationProgress>(step =>
        {
            var isEdge = step.Current == 0 || step.Current >= step.Total;
            var elapsed = stopwatch.ElapsedMilliseconds;
            if (!isEdge && lastSent >= 0 && elapsed - lastSent < 200) return;
            lastSent = elapsed;
            EventSink?.Invoke(IpcEvent.Create("progress", IpcJson.SerializePayload(
                new ProgressPayload(operationId, step.Phase, step.Current, step.Total, step.Message))));
        });
    }

    /// <summary>
    /// 宿主推送事件的出口（WEB-IPC-CONTRACT §1/§4 的 <c>{kind:"event"}</c> 消息）。
    /// 由 App 宿主用 <c>PostWebMessageAsString</c> 接上；未接时事件静默丢弃（不影响请求链路）。
    /// </summary>
    public Action<IpcEvent>? EventSink { get; set; }

    private IpcResponse HandleWikiGetEditPlan(IpcRequest request)
    {
        var req = DeserializePayload<WikiGetEditPlanRequest>(request);
        var exportable = new List<WikiExportableEdit>();
        foreach (var edit in req.Edits)
        {
            exportable.Add(new WikiExportableEdit(edit.Id, edit.Title, "wiki.saveContent"));
        }
        return IpcResponse.Success(request.Id, new WikiGetEditPlanResponse(exportable, Array.Empty<WikiNonExportableEdit>()));
    }

    /// <summary>
    /// 应用一条维基编辑（G-02：此前原样回执、什么也没做，用户看到「保存成功」但数据没变）。
    ///
    /// <para>现在走既有 <see cref="WikiEditService"/> 真正登记进项目编辑集
    /// （lang → <c>LangEditSession</c>、静态 → <c>StaticEditSession</c>、资源 → 资源编辑集），
    /// 之后 <c>export.plan</c> / <c>export.run</c> 才能带上它。</para>
    ///
    /// <para><b>失败一律如实回</b>：没有可写出处、lang 文件找不到、JSON 格式错、
    /// 没打开项目 —— 各自返回中文原因，<b>不再回「成功」</b>。</para>
    /// </summary>
    private IpcResponse HandleWikiApplyEdit(IpcRequest request)
    {
        var req = DeserializePayload<WikiApplyEditRequest>(request);

        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                "请先打开项目：维基编辑要登记进项目编辑集才能导出为模组。");

        var service = new WikiEditService(
            project,
            _projectState.LangEdits ?? new LangEditSession(),
            _projectState.StaticEdits ?? new StaticEditSession(),
            _staticIndex.Store);

        var mapped = service.TryMapToWritableSource(new WikiEditItem(
            req.Id, req.Title, req.Content, req.WritableSource, req.ReplacementPath, req.NewValue));

        if (!mapped.CanExport)
            return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported,
                mapped.Reason ?? "该编辑没有可写出处，无法登记为模组改动。");

        Log.Info("维基编辑已登记：id={0} · 目标={1} · 方式={2}", req.Id, mapped.AssetId, mapped.Method);
        return IpcResponse.Success(request.Id,
            new WikiApplyEditResponse(true, mapped.AssetId, mapped.Method, null));
    }
}
