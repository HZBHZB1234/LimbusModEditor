using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Debugging;
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

    /// <summary>
    /// lang.exportPatch 在 <c>targetDirectory</c> 里用的补丁文件名（字段本意是目录，
    /// 所以文件名由网关定，完整路径从响应的 <c>outputPath</c> 取）。
    /// </summary>
    public const string LangPatchFileName = "langpatch.json";
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
    // 游戏资源扫描（Unity 缓存 → 项目资产 / 资源索引库）：整个网关共用一份实例，
    // 免得两个实例同时写同一个 unity-cache-index.db（服务注释里点明的坑）。
    private readonly UnityCacheScanService _cacheScan;
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
        _cacheScan = new UnityCacheScanService(
            Path.Combine(_cacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));
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
            "asset.edit.batchReplace" => await HandleAssetEditBatchReplaceAsync(request),
            "asset.edit.clearEdits" => HandleAssetEditClearEdits(request),

            // ── 2.4 Spine（服务已迁移至 SpineData，见 t24）──────────────
            "spine.locate" => await HandleSpineLocateAsync(request),
            "spine.export" => await HandleSpineExportAsync(request),
            "spine.catalog" => await HandleSpineCatalogAsync(request),
            "spine.resolve" => await HandleSpineResolveAsync(request),

            // ── 2.5 关联与精确跳转 ────────────────────────────────────────
            "relation.reveal" => HandleRelationReveal(request),
            "relation.describe" => HandleRelationDescribe(request),
            "relation.links" => HandleRelationLinks(request),

            // ── 2.6 项目 / 导出 ──────────────────────────────────────────
            "project.open" => await HandleProjectOpenAsync(request),
            "project.create" => await HandleProjectCreateAsync(request),
            "project.recent" => HandleProjectRecent(request),
            "project.save" => await HandleProjectSaveAsync(request),
            // 扫描入库：新建项目后把它接出到 IPC（此前只有宿主启动时才扫，项目里一条资源都没有）。
            "scan.run" => await HandleScanRunAsync(request),
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
            "static.readRecord" => await HandleStaticReadRecordAsync(request),
            "static.editRecord" => await HandleStaticEditRecordAsync(request),
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

    /// <summary>
    /// 按 <c>assetId</c> 在当前项目里定位资源：<b>两种口径都收</b> ——
    /// 资源的 <see cref="AssetRecord.AssetId"/>（Guid）与 <see cref="AssetRecord.LogicalPath"/>
    /// （资源列表行的稳定键，维基绑定 / 目录树也是这个口径）。
    ///
    /// <para>为什么不能只认一种：列表行给的是 LogicalPath，而旧代码用 LogicalPath 找到资源后
    /// 又把它交给 <c>Guid.Parse</c>（登记阶段）——两条口径各错一半，单条替换入口因此完全不可用。
    /// 取不到返回 null，由调用方给中文 NotFound，不再抛格式异常。</para>
    /// </summary>
    private AssetRecord? ResolveProjectAsset(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return null;
        var assets = _projectState.Project?.Assets;
        if (assets is null) return null;
        if (Guid.TryParse(assetId, out var guid))
        {
            var byId = assets.FirstOrDefault(a => a.AssetId == guid);
            if (byId is not null) return byId;
        }
        return assets.FirstOrDefault(a => string.Equals(a.LogicalPath, assetId, StringComparison.Ordinal));
    }

    private async Task<IpcResponse> HandleAssetEditReplacePayloadAsync(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditReplacePayloadRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var asset = ResolveProjectAsset(req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"未找到资源：{req.AssetId}（assetId 传资源的 AssetId 或 LogicalPath；当前项目共 {project.Assets.Count} 条资源）");
        if (!File.Exists(req.ReplacementPath))
            return IpcResponse.Failure(request.Id, IpcErrorCode.IoError, $"替换文件不存在：{req.ReplacementPath}");

        var projectDir = _projectState.ProjectFile != null
            ? Path.GetDirectoryName(_projectState.ProjectFile)!
            : Environment.CurrentDirectory;
        var result = await _assetEdits.ReplaceFromFileAsync(project, asset, req.ReplacementPath, projectDir);
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

    /// <summary>从一个目录里按文件名批量登记替换（源目录只读，文件复制进项目
    /// edits/assets）。逐条给出登记结果与跳过原因，匹配不到的不动。</summary>
    private async Task<IpcResponse> HandleAssetEditBatchReplaceAsync(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditBatchReplaceRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        if (string.IsNullOrWhiteSpace(req.SourceDirectory))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少替换目录：sourceDirectory");
        if (!Directory.Exists(req.SourceDirectory))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, $"替换目录不存在：{req.SourceDirectory}");

        var projectDir = ProjectDirectory();
        var report = await _assetEdits.BatchReplaceFromDirectoryAsync(
            project, req.SourceDirectory, projectDir, req.OnlyUnreplaced, req.NamePattern);

        var items = report.Items
            .Select(x => new AssetBatchReplaceItem(x.AssetId.ToString(), x.LogicalPath, x.ReplacementPath))
            .ToList();
        var warnings = new List<string>();
        foreach (var file in report.FilesWithoutAsset)
            warnings.Add($"目录里的「{file}」在项目里没有同名资源，未登记");
        if (report.SkippedAlreadyReplaced > 0)
            warnings.Add($"已有替换标记、未覆盖 {report.SkippedAlreadyReplaced} 个资源（先撤销再批量）");
        if (report.SkippedByPattern > 0)
            warnings.Add($"文件名不匹配「{req.NamePattern}」，未处理 {report.SkippedByPattern} 个文件");
        if (report.AssetsWithoutFile.Count > 0)
            warnings.Add($"{report.AssetsWithoutFile.Count} 个资源在目录里没有提供文件（未改动）");

        return IpcResponse.Success(request.Id, new AssetEditBatchReplaceResponse(
            items.Count,
            report.SkippedAlreadyReplaced + report.SkippedByPattern,
            items, warnings, report.Describe()));
    }

    /// <summary>撤销一个资源的全部编辑（替换 / Unity 字段 / Sprite），并同步移除
    /// 项目编辑清单里的记录，避免「还有 X 处改动」留下幽灵计数。</summary>
    private IpcResponse HandleAssetEditClearEdits(IpcRequest request)
    {
        var req = DeserializePayload<AssetEditClearEditsRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        if (string.IsNullOrWhiteSpace(req.AssetId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少资源标识：assetId");

        var asset = ResolveAsset(project, req.AssetId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到资源：{req.AssetId}");

        var before = project.Edits.Count;
        var cleared = _assetEdits.ClearEdits(project, asset, ProjectDirectory());
        var removed = before - project.Edits.Count;
        var info = cleared
            ? $"已撤销「{asset.LogicalPath}」的编辑；项目编辑清单同步移除 {removed} 条，剩余 {project.Edits.Count} 条。"
            : $"「{asset.LogicalPath}」当前没有可撤销的编辑；项目编辑清单剩余 {project.Edits.Count} 条。";
        return IpcResponse.Success(request.Id,
            new AssetEditClearEditsResponse(true, cleared ? 1 : 0, removed, project.Edits.Count, info));
    }

    /// <summary>按 assetId 定位资源：既接受 AssetId（Guid），也接受逻辑路径
    /// ——前端两种口径都在用，认不出来就返回 null 交给调用方报 NotFound。</summary>
    private static AssetRecord? ResolveAsset(ModProject project, string assetId)
        => Guid.TryParse(assetId, out var id)
            ? project.Assets.FirstOrDefault(x => x.AssetId == id)
            : project.Assets.FirstOrDefault(x => x.LogicalPath == assetId);

    /// <summary>当前项目的工作目录（替换暂存文件落在这里）。没打开项目时回落到当前目录。</summary>
    private string ProjectDirectory()
        => _projectState.ProjectFile is not null
            ? Path.GetDirectoryName(_projectState.ProjectFile)!
            : Environment.CurrentDirectory;

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

    /// <summary>
    /// <c>spine.export</c>：把 refKey 解析出的 Spine 三件套<b>真正写到磁盘</b>
    /// （此前是 TODO：只把 refKey 记进 <c>written</c>，一个字节都没落盘）。
    ///
    /// <para><b>语义（方案 A：单条为主，批量是同一个循环）</b>：请求给一个 <c>targetDirectory</c>
    /// 与若干 <c>refKey</c>，每条在目标目录下落一个<b>以骨架名为名的子目录</b>：
    /// <c>&lt;骨架名&gt;.json</c> / <c>&lt;骨架名&gt;.atlas.txt</c> / <c>&lt;页名&gt;.png</c>
    /// （纹理页按图集 <c>materials</c> 顺序命名，如 <c>back.png</c>）。这正是 Spine 运行时
    /// 期望的磁盘形态，用户拿到即可直接用于模组制作。</para>
    ///
    /// <para><b>复用而非重写</b>：三件套解析一律走 <see cref="ISpineDataGateway"/>（含 prefab
    /// 引用链与按 refKey 的结果缓存），落盘一律走 <see cref="AtomicOutput"/>（临时文件 + 移动）。</para>
    ///
    /// <para><b>不造假</b>：取不到就带中文原因记一条失败，<b>不写空文件凑数</b>；
    /// 逐条失败<b>不中断整批</b>；不覆盖已有文件（除非请求显式要求覆盖）。</para>
    ///
    /// <para><b>可中断 + 有进度</b>：走 <c>cancel</c> 那条既有的取消通道
    /// （<see cref="CancellationTokenStore"/>），并按契约 §4 推 <c>progress</c> 事件。</para>
    /// </summary>
    private async Task<IpcResponse> HandleSpineExportAsync(IpcRequest request)
    {
        var req = DeserializePayload<SpineExportRequest>(request);

        // 不给操作 id 时不注册取消令牌：本次导出就不可单独取消，返回后立刻释放，不漏令牌。
        var operationId = string.IsNullOrWhiteSpace(req.OperationId) ? null : req.OperationId!;
        var cancellationToken = operationId is null
            ? CancellationToken.None
            : CancellationTokenStore.Register(operationId);
        try
        {
            var exporter = new SpineExportService(_spineData);
            var overwrite = req.Overwrite ? SpineExportOverwrite.Overwrite : SpineExportOverwrite.Skip;
            var result = await exporter.ExportAsync(
                req.AssetIds ?? [],
                req.TargetDirectory,
                overwrite,
                CreateSpineExportProgressSink(operationId),
                cancellationToken);

            var files = result.Items.SelectMany(i => i.Files).ToList();
            return IpcResponse.Success(request.Id, new SpineExportResponse(
                result.Items.Where(i => i.Ok).Select(i => i.RefKey).ToList(),
                result.Items.Where(i => !i.Ok).Select(i => $"{i.RefKey}：{i.Reason}").ToList(),
                result.OutputDirectory,
                files.Select(f => new SpineExportedFileDto(f.RelativePath, f.Role, f.Bytes)).ToList(),
                result.Items.Select(i => new SpineExportItemDto(
                    i.RefKey, i.Name, i.Ok, i.OutputDirectory,
                    i.Files.Select(f => new SpineExportedFileDto(f.RelativePath, f.Role, f.Bytes)).ToList(),
                    i.SkippedFiles, i.Reason)).ToList(),
                result.Overwrite,
                result.Info,
                result.Cancelled));
        }
        finally
        {
            if (operationId is not null) CancellationTokenStore.Complete(operationId);
        }
    }

    /// <summary>
    /// 把导出进度转成契约 §4 的 <c>progress</c> 事件（与维基生成同一个 200 ms 节流口径：
    /// 首条与末条必达）。<paramref name="operationId"/> 为空时不推事件（没有 id 前端也关联不上）。
    /// </summary>
    private IProgress<SpineExportProgress>? CreateSpineExportProgressSink(string? operationId)
    {
        if (operationId is null) return null;
        var stopwatch = Stopwatch.StartNew();
        long lastSent = -1;
        return new Progress<SpineExportProgress>(step =>
        {
            var isEdge = step.Current <= 1 || step.Current >= step.Total;
            var elapsed = stopwatch.ElapsedMilliseconds;
            if (!isEdge && lastSent >= 0 && elapsed - lastSent < 200) return;
            lastSent = elapsed;
            EventSink?.Invoke(IpcEvent.Create("progress", IpcJson.SerializePayload(
                new ProgressPayload(operationId, "spine-export", step.Current, step.Total,
                    $"{step.Message}（{step.Current}/{step.Total}）{SpineDisplayNameOf(step.RefKey)}"))));
        });
    }

    /// <summary>
    /// <c>spine.catalog</c>：<b>只列名册，一个 bundle 都不解</b>。
    ///
    /// <para>全库有几百个 Spine 挂点（<c>SpineIllustPrefab</c> + 战斗用的 <c>Prefab/SD/**</c>），
    /// 其中绝大多数从没被任何维基页面绑定过 —— 也就是用户在界面上<b>从来没见过它们</b>。
    /// 解一套实测平均 2.1 s（主因是整读一个大 bundle），一次性解完要几十分钟，
    /// 所以这里只做索引查询（毫秒级）把名册列出来；具体某一条能不能解开、
    /// 三件套地址是什么，交给 <c>spine.resolve</c> <b>按需</b>取。</para>
    /// </summary>
    private async Task<IpcResponse> HandleSpineCatalogAsync(IpcRequest request)
    {
        var req = DeserializePayload<SpineCatalogRequest>(request);
        var query = new SpineData.SpineCatalogQuery(req.Keyword, req.OnlyUnbound, req.Offset, req.Limit);
        // 概览与明细共用同一次扫描（名册要扫 127 万行，分两次调用就付两遍开销）。
        var result = await _spineData.BrowseCatalogWithSummaryAsync(query);
        var summary = result.Summary;
        var page = result.Page;

        // 归属：一次读完整张反查索引（既有分析器产出的权威归属），供「来源 / 状态 / 点进去」用。
        var owners = ReadSpineOwnersByRef();

        return IpcResponse.Success(request.Id, new SpineCatalogResponse(
            new SpineCatalogSummaryDto(summary.Total, summary.Bound, summary.Unbound, summary.BundleMissing),
            page.Total,
            page.Items.Select(i =>
            {
                var ownerPageIds = owners.TryGetValue(i.RefKey, out var list) ? list : (IReadOnlyList<string>)[];
                var status = SpineData.SpineStatusRules.CatalogStatus(i.BundlePresent, ownerPageIds.Count);
                return new SpineCatalogItemDto(
                    i.RefKey, i.Name, i.Group, i.BundlePresent, i.BoundPageCount,
                    SpineData.SpineStatusRules.Source(i.BundlePresent, i.BoundPageCount, ownerPageIds.Count),
                    status, SpineData.SpineStatusRules.Label(status), ownerPageIds);
            }).ToList()));
    }

    /// <summary>
    /// 「Spine 挂点 → 归属的维基页面 id」反查（<c>subjects_by_ref</c> 是既有分析器从 links
    /// 派生的<b>权威</b>归属，不是现推的）。只在维基里真有这个页面时才返回它 ——
    /// 否则前端会给出一个点进去 404 的链接。
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSpineOwnersByRef()
    {
        try
        {
            var existing = new HashSet<string>(
                new WikiPageStore(_cacheDirectory).ReadPages().Select(p => p.PageId), StringComparer.Ordinal);
            var map = new RelationStore(_cacheDirectory).ReadSubjectsByRefMap();
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (refKey, owners) in map)
            {
                var pages = owners.Where(existing.Contains).ToList();
                if (pages.Count > 0) result[refKey] = pages;
            }
            return result;
        }
        catch (Exception ex)
        {
            // 归属只是列表上的附加信息，读不到不影响名册本身 —— 降级为「无归属」。
            Log.Warn(ex, "读取 Spine 归属反查失败（名册仍可用，来源按未归类显示）。");
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <c>spine.resolve</c>：按 refKey 取一条的三件套地址（<b>按需</b>，带网关侧的结果缓存）。
    ///
    /// <para>复用维基那条 <see cref="WikiMediaResolver.ResolveSpineAsync"/>，落盘到同一个
    /// <c>wwwroot/data/wiki</c> 并经同一个 <c>lme.data</c> 虚拟主机出地址 ——
    /// 前端因此能用<b>同一个</b> <c>WikiSpineViewer</c>/<c>SpineRenderer</c> 渲染，
    /// 不必为浏览页另写一套。</para>
    ///
    /// <para><b>取不到时如实给中文原因</b>（如「引用链里没有骨架与图集」「bundle 不在本机」），
    /// 不给半个地址、不拿别的素材凑。</para>
    /// </summary>
    private async Task<IpcResponse> HandleSpineResolveAsync(IpcRequest request)
    {
        var req = DeserializePayload<SpineResolveRequest>(request);
        if (string.IsNullOrWhiteSpace(req.RefKey))
            return IpcResponse.Success(request.Id,
                new SpineResolveResponse(false, null, null, null, null, null, "缺少容器路径（refKey）。",
                    string.Empty, [], false, SpineData.SpineStatusRules.Failed,
                    SpineData.SpineStatusRules.Label(SpineData.SpineStatusRules.Failed)));

        // 来源 / 归属与 bundle 状态：与名册同一份判据，避免「列表说可解析、点进去说取不到」。
        var owners = ReadSpineOwnersByRef();
        var ownerPageIds = owners.TryGetValue(req.RefKey, out var list) ? list : (IReadOnlyList<string>)[];
        var bundlePresent = BundleFilePresent(req.RefKey);
        var source = SpineData.SpineStatusRules.Source(bundlePresent, 0, ownerPageIds.Count);

        var resolution = await _wikiMedia.ResolveSpineAsync(req.RefKey);
        var skeletonUrl = resolution.SkeletonUrl;
        var atlasUrl = resolution.AtlasUrl;
        if (skeletonUrl is null || atlasUrl is null)
        {
            // 摸清真实原因：是路径不对、bundle 不在、还是引用链里确实没有骨架。
            var (data, error) = await _spineData.GetSpineDataByPathAsync(req.RefKey);
            var reason = error ?? (data is null
                ? "这条路径没能解出 Spine 三件套（引用链里没有骨架与图集）。"
                : "三件套已取到，但地址没能落地（详见 logs/current.log）。");
            var status = SpineData.SpineStatusRules.ResolveStatus(false, bundlePresent, reason);
            return IpcResponse.Success(request.Id,
                new SpineResolveResponse(false, null, null, null, null, data?.Label, reason,
                    source, ownerPageIds, bundlePresent, status, SpineData.SpineStatusRules.Label(status)));
        }

        return IpcResponse.Success(request.Id, new SpineResolveResponse(
            true,
            skeletonUrl,
            atlasUrl,
            resolution.TextureUrls,
            skeletonUrl.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) ? "binary" : "json",
            SpineDisplayNameOf(req.RefKey),
            null,
            source,
            ownerPageIds,
            bundlePresent,
            SpineData.SpineStatusRules.Parsed,
            SpineData.SpineStatusRules.Label(SpineData.SpineStatusRules.Parsed)));
    }

    /// <summary>
    /// 这条 Spine 挂点所在的 bundle 文件此刻在不在本机（Unity 缓存被清 = 不在）。
    /// 查索引库的 <c>bundles.data_path</c>；查不到一律按「不在」算（不假装在）。
    /// </summary>
    private bool BundleFilePresent(string refKey)
    {
        try
        {
            foreach (var asset in _catalog.FindByContainerEntry(refKey))
            {
                var path = asset.SourcePath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "探测 Spine bundle 是否存在失败（按不在本机处理）：{0}", refKey);
        }
        return false;
    }

    /// <summary>名册/预览要显示的名字：容器路径的文件名去掉 <c>.prefab</c> 扩展名。</summary>
    private static string SpineDisplayNameOf(string refKey)
    {
        var normalized = refKey.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = slash < 0 ? normalized : normalized[(slash + 1)..];
        return name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            ? name[..^".prefab".Length]
            : name;
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
        // 传网关那份 lang 服务：打开后 lang.applyPatch 的改动要能被 export.plan/run 看见。
        _projectState.SetProject(project, req.ProjectFile, _langText);
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
        _projectState.SetProject(project, projectFile, _langText);
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

    /// <summary>
    /// scan.run：触发<b>既有</b>启动扫描服务，把游戏资源登记进当前项目
    /// （P0-4：此前扫描只在宿主启动时跑，<c>project.create</c> 出来的新项目一条资源都没有，
    /// 而 IPC 层没有任何入口能补上这一步）。
    ///
    /// <para>扫描逻辑一处不重写：只把 <see cref="StartupScanService"/> 的现有入口
    /// （<c>ScanUnityAssetsStepAsync</c> / <c>ScanAllAsync</c>）接出来，
    /// 并把它的 <see cref="StartupScanProgress"/> 转成契约里的 <c>progress</c> 事件。</para>
    ///
    /// <para>前提不成立一律中文失败：没开项目 / 只支持 assets 与 all 两种范围 /
    /// 缓存目录定位不到（项目字段 → 共享配置 → 本机规范缓存根都没有）。</para>
    /// </summary>
    private async Task<IpcResponse> HandleScanRunAsync(IpcRequest request)
    {
        var req = DeserializePayload<ScanRunRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                "请先打开或新建项目：扫描要把游戏资源登记进当前项目。");

        var scope = string.IsNullOrWhiteSpace(req.Scope) ? "assets" : req.Scope!.Trim().ToLowerInvariant();
        if (scope is not ("assets" or "all"))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"未知的扫描范围：{req.Scope}（只支持 assets = 只扫游戏资源，all = 游戏资源 + 三个工作台索引）");

        var (scan, cacheDirectory) = CreateScanService(project, req.UnityCacheDirectory, req.GameDirectory);
        if (cacheDirectory is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                "未找到 Unity 缓存目录：项目字段、共享配置与本机规范缓存根（AppData/LocalLow/Unity/ProjectMoon_LimbusCompany）"
                + "都不存在。先启动一次游戏生成缓存，或在「设置」页指定缓存目录。");

        var operationId = string.IsNullOrWhiteSpace(req.OperationId) ? "scan-" + request.Id : req.OperationId!;
        var cancellationToken = CancellationTokenStore.Register(operationId);
        var watch = Stopwatch.StartNew();

        void Emit(string label, string detail) => EventSink?.Invoke(IpcEvent.Create("progress",
            IpcJson.SerializePayload(new ProgressPayload(operationId, label, 0, 0, detail))));

        // 节流：逐 bundle 的上报能到每秒上万条，按契约 §8-5「中间 ≥200ms」聚合；
        // 首条（开始）与末条（收尾补发）必达。
        string? lastLabel = null;
        string? lastDetail = null;
        var stopwatch = Stopwatch.StartNew();
        long lastSent = -1;
        var progress = new Progress<StartupScanProgress>(p =>
        {
            lastLabel = p.Label;
            lastDetail = p.Detail;
            var elapsed = stopwatch.ElapsedMilliseconds;
            if (lastSent >= 0 && elapsed - lastSent < 200) return;
            lastSent = elapsed;
            Emit(p.Label, p.Detail);
        });
        Emit("扫描", "开始");

        try
        {
            IReadOnlyList<StartupScanStepResult> steps = scope == "all"
                ? (await scan.ScanAllAsync(project, progress, cancellationToken).ConfigureAwait(false)).Steps
                : [await scan.ScanUnityAssetsStepAsync(project, progress, cancellationToken).ConfigureAwait(false)];

            if (lastLabel is not null) Emit(lastLabel, lastDetail ?? string.Empty);

            var assetStep = steps.FirstOrDefault(s => s.Key == StartupScanService.UnityAssetsStep) ?? steps[0];
            // 跳过/失败对调用方就是「没扫成」：照实给中文原因，不回一个「成功但什么都没变」。
            if (assetStep.Status is StartupScanStepStatus.Skipped or StartupScanStepStatus.Failed)
                return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                    $"{assetStep.Label}：{assetStep.Detail}（缓存目录={cacheDirectory}）");

            watch.Stop();
            var info = steps.Count > 1 ? string.Join("；", steps.Select(s => $"{s.Label}={s.Status}")) : null;
            return IpcResponse.Success(request.Id, new ScanRunResponse(
                scope, assetStep.Status.ToString(), assetStep.Detail,
                assetStep.RowCount ?? 0,
                project.Assets.Count, watch.Elapsed.TotalSeconds, cacheDirectory,
                steps.Select(s => new ScanStepDto(s.Key, s.Label, s.Status.ToString(), s.Detail,
                    s.Elapsed.TotalSeconds)).ToList(), info));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 扫描服务本身已经逐步隔离失败；走到这里是它没兜住的（如缓存目录被拔掉）。
            Log.Error(ex, "scan.run 失败：scope={0} · cache={1}", scope, cacheDirectory);
            return IpcResponse.Failure(request.Id, IpcErrorCode.Internal, "扫描失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 组装扫描服务，并给出本次真正使用的缓存目录（回退/覆盖后到底扫哪儿，照实给）。
    /// 目录解析顺序与静态表那条回退一致：显式覆盖 → 共享配置 → 项目字段 → 本机规范缓存根。
    /// </summary>
    /// <returns>缓存目录为 null = 一处也没定位到（调用方给中文 NotFound）。</returns>
    private (StartupScanService Service, string? CacheDirectory) CreateScanService(
        ModProject project, string? cacheOverride, string? gameOverride)
    {
        var cacheDirectory = FirstExisting(cacheOverride,
            _environment.EffectiveUnityCacheDirectory(project), UnityCacheLocator.CanonicalCacheRoot());
        var gameDirectory = FirstExisting(gameOverride, _environment.EffectiveGameDirectory(project));

        // 覆盖/回退改变了生效目录时，复制一份环境给它 —— 扫描服务自己按
        // AppEnvironment 解析目录，改共享配置对象本身会污染全局（其它实例同一份）。
        AppEnvironment env = _environment;
        var config = _environment.Config;
        var changed = !SamePath(config.UnityCacheDirectory, cacheDirectory)
                      || !SamePath(config.GameDirectory, gameDirectory);
        if (changed)
        {
            env = new AppEnvironment(_environment.BaseDirectory);
            env.Config.UnityCacheDirectory = cacheDirectory;
            env.Config.GameDirectory = gameDirectory;
        }
        // StartupScanService 无状态，按次构造即可（它内部只认 env + 共用的扫描实例）。
        return (new StartupScanService(env, _cacheScan), cacheDirectory);

        static bool SamePath(string? a, string? b) =>
            string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)
            || string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取第一个「非空且目录存在」的候选；一个都没有就返回 null（不猜、不造）。</summary>
    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
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
        return IpcResponse.Success(request.Id, new ExportPlanResponse(slots, skipped, ToValidationDto(plan.Validation)));
    }

    /// <summary>写前校验 → IPC DTO（分级文本用 error / warning / info，前端直接按级分色）。</summary>
    private static ExportValidationDto ToValidationDto(ModExportValidation validation) =>
        new(validation.ChangedCount, validation.CheckedFileCount, validation.ErrorCount,
            validation.WarningCount, validation.HasBlockingError,
            validation.Checks.Select(x => new ExportCheckDto(x.LevelText, x.Target, x.Message)).ToArray(),
            validation.Info);

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
            items.Count, result.WrittenSlotCount, result.WrittenFileCount, items, skipped, info,
            ToValidationDto(plan.Validation)));
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

    /// <summary>
    /// lang.exportPatch：把文本工作台的编辑集导成一份 LCTA <c>patchs</c> 文档。
    ///
    /// <para><b>P1 修的正是这里</b>：请求字段叫 <c>targetDirectory</c>，此前却原样交给
    /// <see cref="LangTextWorkbenchService.ExportPatch"/>（它吃的是<b>文件</b>路径），
    /// 于是产物是一个叫「langpatch」的裸文件，而不是目录里的补丁文件。现在按字段本意当目录用：
    /// 目录不存在就建，文件名固定 <c>langpatch.json</c>，完整路径由响应带回。</para>
    /// </summary>
    private IpcResponse HandleLangExportPatch(IpcRequest request)
    {
        var req = DeserializePayload<LangExportPatchRequest>(request);
        if (string.IsNullOrWhiteSpace(req.TargetDirectory))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少导出目录：targetDirectory 不能为空。");

        var directory = Path.GetFullPath(req.TargetDirectory);
        var outputPath = Path.Combine(directory, IpcGatewayConstants.LangPatchFileName);
        var report = _langText.ExportPatch(outputPath);
        return IpcResponse.Success(request.Id, new
        {
            ok = true,
            editedFiles = report.EditedFileCount,
            patchedFiles = report.PatchedFileCount,
            outputPath = report.OutputPath,
        });
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
    /// 静态表读取的缓存根候选：共享配置 / 项目字段（<see cref="AppEnvironment.EffectiveUnityCacheDirectory"/>）
    /// → 本机规范缓存根（<see cref="UnityCacheLocator.CanonicalCacheRoot"/>，只在实际存在时才给）。
    ///
    /// <para><b>为什么必须回退</b>：真实项目（如 MyMod）的 <c>UnityCacheDirectory</c> 是空的，
    /// 而共享配置里明明配着缓存目录 —— 只看项目字段会一律「定位不到静态数据 bundle」，
    /// 连一张表的记录都读不出来（补上缓存目录立刻能读到真实记录，已被端到端冒烟证明）。</para>
    /// </summary>
    private IReadOnlyList<string> StaticCacheRoots(ModProject project)
    {
        var roots = new List<string?>();
        var configured = _environment.EffectiveUnityCacheDirectory(project);
        if (!string.IsNullOrWhiteSpace(configured)) roots.Add(configured);
        var canonical = UnityCacheLocator.CanonicalCacheRoot();
        if (canonical is not null) roots.Add(canonical);
        return StaticBundleLocator.WithMigratedCacheRoot(roots);
    }

    private sealed record StaticTableLoad(StaticTableEntry Entry, string Text);

    /// <summary>
    /// 静态表读取的三段式前置（<c>static.records</c> / <c>static.readRecord</c> /
    /// <c>static.editRecord</c> 共用）：定位 bundle → 索引里按 <c>tableId</c>
    /// （容器路径或表名）取表元数据 → <see cref="StaticIndexService.LoadDocumentAsync"/> 读正文。
    ///
    /// <para>前提缺失一律中文错误、不返回空结果充数（未开项目 / 定位不到 bundle /
    /// 索引里没这张表 / 正文读不到）。成功时 <c>Failure</c> 为 null。</para>
    /// </summary>
    private async Task<(StaticTableLoad? Table, IpcResponse? Failure)> LoadStaticTableAsync(
        IpcRequest request, string? tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId))
            return (null, IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少表 id（tableId）"));

        var project = _projectState.Project;
        if (project is null)
            return (null, IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目"));

        var location = StaticIndexService.LocateForReads(_staticIndex.Store, project.GameDirectory,
            StaticCacheRoots(project));
        if (location is null || !location.IsCached)
            return (null, IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                "无法定位静态数据 bundle（运行时 catalog 与 Unity 缓存均未命中）。请确认游戏目录与 Unity 缓存目录已配置。"));

        var entries = _staticIndex.Store.ReadEntries();
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.ContainerEntry, tableId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, tableId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return (null, IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"静态表索引里没有这张表：{tableId}（索引共 {entries.Count} 张表）"));

        var document = await _staticIndex.LoadDocumentAsync(location, entry).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(document.Text))
            return (null, IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"表 {entry.Name} 的正文读不到（非 UTF-8，或该条目已不在当前 bundle 里）。"));

        return (new StaticTableLoad(entry, document.Text!), null);
    }

    /// <summary>
    /// static.records：一张静态表的记录分页（前置见 <see cref="LoadStaticTableAsync"/>，
    /// 之后 <see cref="StaticTableRows.Parse"/> 解析行集合 → 按 offset/take 取一页）。
    /// </summary>
    private async Task<IpcResponse> HandleStaticRecordsAsync(IpcRequest request)
    {
        var req = DeserializePayload<StaticRecordsRequest>(request);
        var (table, failure) = await LoadStaticTableAsync(request, req.TableId).ConfigureAwait(false);
        if (failure is not null) return failure;

        var rows = StaticTableRecords.Rows(table!.Text);
        if (rows.Count == 0)
            return IpcResponse.Success(request.Id, new StaticRecordsResponse(req.TableId, [], 0, 0, 0,
                $"表 {table.Entry.Name} 的正文没能解析出记录（正文 {table.Entry.SizeLabel}，不是 list/数组/单对象形态）。"));

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

    /// <summary>
    /// static.readRecord：按「表 + 记录 id」取一条记录的原始 JSON（编辑器直接吃它建树）。
    /// 记录 id 的口径与 <c>static.records</c> 列表里的完全一致（<see cref="StaticTableRecords.IndexOf"/>）。
    ///
    /// <para>取<b>当前值</b>：这张表已在编辑集里就先读改后正文（与 <c>lang.readEntry</c>
    /// 「有修改读修改、否则读原文」同一口径），改过之后立刻能读回改后的值。</para>
    /// </summary>
    private async Task<IpcResponse> HandleStaticReadRecordAsync(IpcRequest request)
    {
        var req = DeserializePayload<StaticReadRecordRequest>(request);
        var (table, failure) = await LoadStaticTableAsync(request, req.TableId).ConfigureAwait(false);
        if (failure is not null) return failure;

        var current = _projectState.StaticEdits?.TryGetModifiedText(table!.Entry.Key) ?? table!.Text;
        foreach (var text in new[] { current, table.Text })
        {
            var rows = StaticTableRecords.Rows(text);
            var index = StaticTableRecords.IndexOf(rows, req.RecordId);
            if (index >= 0) return IpcResponse.Success(request.Id, new StaticReadRecordResponse(rows[index].GetRawText()));
        }

        var officialRows = StaticTableRecords.Rows(table.Text);
        return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
            $"表 {table.Entry.Name} 里没有记录 {req.RecordId}（共 {officialRows.Count} 条记录）");
    }

    /// <summary>
    /// static.editRecord：把一条记录的<b>改后 JSON</b> 写回<b>静态编辑集</b>
    /// （<see cref="StaticEditSession"/>），与导出链路同源 ——
    /// <c>export.plan</c> / <c>export.run</c> 读的就是 <see cref="StaticEditSession.Snapshot"/>，
    /// 因此「改了静态表」立刻出现在导出计划里，并由 <c>.staticmod</c> 槽位写出 RFC6902 补丁。
    ///
    /// <para>只改内存编辑集与项目编辑清单：<b>游戏数据与 Unity 缓存全程只读</b>，
    /// 官方基线就是刚读出来的那份正文。</para>
    ///
    /// <para>改后正文的重建：按行集合的三种形态（顶层数组 / <c>list|dataList|dataArray</c> 包一层 /
    /// 单对象表）原位替换那一行，其余内容原样保留 —— 不重新序列化整个文档以外的东西。</para>
    /// </summary>
    private async Task<IpcResponse> HandleStaticEditRecordAsync(IpcRequest request)
    {
        var req = DeserializePayload<StaticEditRecordRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var (table, failure) = await LoadStaticTableAsync(request, req.TableId).ConfigureAwait(false);
        if (failure is not null) return failure;

        if (string.IsNullOrWhiteSpace(req.RecordId))
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "缺少记录 id（recordId）");
        // 基线 = 已有修改就接着改（多次编辑可累加），否则是刚读出来的官方正文；
        // 官方基线始终留作差分的一侧，导出 diff 才不会把前一次的改动算成「官方」。
        var baseline = _projectState.StaticEdits?.TryGetModifiedText(table!.Entry.Key) ?? table!.Text;
        var rows = StaticTableRecords.Rows(baseline);
        var index = StaticTableRecords.IndexOf(rows, req.RecordId);
        if (index < 0)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound,
                $"表 {table.Entry.Name} 里没有记录 {req.RecordId}（共 {rows.Count} 条记录）");

        JsonNode? modifiedRow;
        try
        {
            modifiedRow = JsonNode.Parse(req.Json);
        }
        catch (JsonException ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"改后内容不是合法 JSON：{ex.Message}");
        }
        if (modifiedRow is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "改后内容为空（json）");

        string modifiedText;
        try
        {
            modifiedText = ReplaceRow(baseline, index, modifiedRow);
        }
        catch (JsonException ex)
        {
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery,
                $"表 {table.Entry.Name} 的正文不是合法 JSON，无法写回：{ex.Message}");
        }

        var edits = _projectState.StaticEdits;
        if (edits is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目（静态编辑集未就绪）");

        edits.Set(table.Entry.Key, table.Entry, table.Text, modifiedText);
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.PatchJson,
            TargetPath = string.IsNullOrWhiteSpace(table.Entry.ContainerEntry)
                ? table.Entry.Name
                : table.Entry.ContainerEntry,
        });

        return IpcResponse.Success(request.Id, new
        {
            ok = true,
            tableId = req.TableId,
            recordId = req.RecordId,
            officialSize = table.Text.Length,
            modifiedSize = modifiedText.Length,
            editedTables = edits.EntryCount,
            info = $"已登记 {table.Entry.Name} 的修改（记录 {req.RecordId}）；该改动会随导出写进 .staticmod 的 RFC6902 补丁"
        });
    }

    /// <summary>
    /// 把文档里第 <paramref name="index"/> 行换成 <paramref name="modifiedRow"/>，返回改后全文。
    /// 形态判定与 <see cref="StaticTableRows.Parse"/> 一致（数组 / list|dataList|dataArray / 单对象）。
    /// </summary>
    private static string ReplaceRow(string officialText, int index, JsonNode modifiedRow)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var text = officialText.TrimStart(StaticTableRecords.Bom);
        var document = JsonNode.Parse(text);
        if (document is JsonArray array)
        {
            if (index >= array.Count) throw new JsonException($"行下标 {index} 超出数组范围（{array.Count} 行）");
            array[index] = modifiedRow;
            return array.ToJsonString(options);
        }
        if (document is JsonObject table)
        {
            foreach (var name in new[] { "list", "dataList", "dataArray" })
            {
                if (table[name] is not JsonArray rows) continue;
                if (index >= rows.Count) throw new JsonException($"行下标 {index} 超出 {name} 范围（{rows.Count} 行）");
                rows[index] = modifiedRow;
                return table.ToJsonString(options);
            }
        }
        // 单对象表：整份正文就是这一行。
        return modifiedRow.ToJsonString(options);
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
