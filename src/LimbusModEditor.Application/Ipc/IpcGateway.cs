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
using LimbusModEditor.Domain.Assets;
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
        ProjectService? projects = null)
    {
        return new IpcGateway(catalog, projectState, spineData, bankIndex, langText, staticIndex,
            assetEdits, exportPlan, exportService, projects);
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
        ProjectService? projects = null)
    {
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
            "asset.preview" => HandleAssetPreview(request),
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
            "project.save" => await HandleProjectSaveAsync(request),
            "export.plan" => HandleExportPlan(request),
            "export.run" => await HandleExportRunAsync(request),

            // ── 2.7 配置 / UI 状态 ────────────────────────────────────────
            "config.read" => HandleConfigRead(request),
            "config.write" => HandleConfigWrite(request),
            "uiState.read" => HandleUiStateRead(request),
            "uiState.write" => HandleUiStateWrite(request),

            // ── 音频工作台 ────────────────────────────────────────────────
            "bank.list" => HandleBankList(request),
            "bank.samples" => HandleBankSamples(request),
            "bank.preview" => HandleBankPreview(request),
            "bank.exportRebank" => await HandleBankExportRebankAsync(request),

            // ── 文本工作台 ────────────────────────────────────────────────
            "lang.activeLanguages" => HandleLangActiveLanguages(request),
            "lang.files" => HandleLangFiles(request),
            "lang.search" => HandleLangSearch(request),
            "lang.readEntry" => HandleLangReadEntry(request),
            "lang.editEntry" => HandleLangEditEntry(request),
            "lang.exportPatch" => HandleLangExportPatch(request),

            // ── 静态数据工作台 ────────────────────────────────────────────
            "static.tableList" => HandleStaticTableList(request),
            "static.records" => HandleStaticRecords(request),
            "static.locate" => HandleStaticLocate(request),
            "static.readRecord" => HandleStaticReadRecord(request),
            "static.editRecord" => HandleStaticEditRecord(request),
            "static.exportStaticmod" => HandleStaticExportStaticmod(request),

            // ── 维基页面数据 ────────────────────────────────────────────
            "wiki.home" => HandleWikiHome(request),
            "wiki.categoryIndex" => HandleWikiCategoryIndex(request),
            "wiki.category.load" => HandleWikiCategoryIndex(request),
            // 收敛为一个方法名：历史上 wiki.getPage 与 wiki.page.load 是同一处理器的两个别名。
            "wiki.getPage" => HandleWikiPageLoad(request),
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

    private IpcResponse HandleAssetPreview(IpcRequest request)
    {
        var req = DeserializePayload<AssetPreviewRequest>(request);
        // TODO W2: 从索引定位资源 → 取容器路径 → 拼 Virtual Host URL
        return IpcResponse.Success(request.Id, new AssetPreviewResponse(
            "unknown", Array.Empty<PropertyRow>(), null));
    }

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
        // TODO W2: 需 RelationStore
        return IpcResponse.Success(request.Id, new { subjects = Array.Empty<object>() });
    }

    private IpcResponse HandleRelationLinks(IpcRequest request)
    {
        // TODO W2: 需 RelationStore
        return IpcResponse.Success(request.Id, new { links = Array.Empty<object>() });
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

        var plan = _exportPlan.Plan(project, req.TargetDirectory, langEdits, staticEdits);
        var projectRoot = _projectState.ProjectFile != null ? Path.GetDirectoryName(_projectState.ProjectFile)! : string.Empty;
        var result = await _exportService.ExportAsync(project, projectRoot, plan, new ModExportPlanContext(),
            new Progress<string>(msg => Log.Info("导出进度: {0}", msg)), CancellationToken.None);
        return IpcResponse.Success(request.Id, new { ok = true, root = result.RootDirectory, slots = result.Slots.Count });
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

    private IpcResponse HandleBankPreview(IpcRequest request)
    {
        // 试听需要宿主原生 FMOD 解码 + 播放能力（WPF MediaPlayer）
        return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, "音频试听需要宿主原生播放能力（需 App 侧 FMOD 解码 + MediaPlayer）");
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

    private IpcResponse HandleLangActiveLanguages(IpcRequest request)
    {
        var langRoot = _langText.ResolveLangRoot(null);
        var activeLang = langRoot is not null ? _langText.ReadActiveLanguage(langRoot) : null;
        var languages = new List<string>();
        if (activeLang is not null) languages.Add(activeLang);
        return IpcResponse.Success(request.Id, new LangActiveLanguagesResponse(languages));
    }

    private IpcResponse HandleLangFiles(IpcRequest request)
    {
        var req = DeserializePayload<LangFilesRequest>(request);
        var langRoot = _langText.ResolveLangRoot(null);
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
        var langRoot = _langText.ResolveLangRoot(null);
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

    private IpcResponse HandleStaticRecords(IpcRequest request)
    {
        // 静态表记录需要 bundle 定位（W3 实现）
        return IpcResponse.Failure(request.Id, IpcErrorCode.Unsupported, "静态表记录查询需要 bundle 定位（W3 实现）");
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

    private IpcResponse HandleWikiPageLoad(IpcRequest request)
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
            sub.Entries.SelectMany(entry => entry.Bindings).Select(binding => new WikiBindingDto(
                binding.RefKey,
                binding.Kind,
                binding.Display,
                binding.MediaKind,
                binding.DurationSec,
                MediaUrlOf(binding))).ToList())).ToList();

        // 画廊：只收图片绑定；拿不到真实地址的项 Url 为 null，前端按降级处理。
        var gallery = detail.SubPages.SelectMany(sub => sub.Entries)
            .SelectMany(entry => entry.Bindings)
            .Where(binding => string.Equals(binding.Kind, "Image", StringComparison.OrdinalIgnoreCase))
            .Select(binding => new WikiGalleryItemDto(
                MediaUrlOf(binding), binding.Display ?? binding.RefKey, binding.Kind))
            .Take(MaxGalleryItems)
            .ToList();

        // 信息框：只写本地确实拿得到的字段（类别名/标题/副标题/分节数/条目数），不编造。
        var infobox = new List<WikiInfoboxRowDto>();
        if (!string.IsNullOrWhiteSpace(page.CategoryLabel))
            infobox.Add(new WikiInfoboxRowDto("类别", page.CategoryLabel));
        if (!string.IsNullOrWhiteSpace(page.Subtitle))
            infobox.Add(new WikiInfoboxRowDto("副标题", page.Subtitle));
        infobox.Add(new WikiInfoboxRowDto("分节", sections.Count.ToString(CultureInfo.InvariantCulture)));
        infobox.Add(new WikiInfoboxRowDto("条目",
            sections.Sum(s => s.Entries.Count).ToString(CultureInfo.InvariantCulture)));

        return IpcResponse.Success(request.Id, new WikiPageDto(
            page.PageId, page.Title, page.Category, page.Subtitle, sections,
            Array.Empty<WikiRelatedPageDto>(),
            infobox,
            string.IsNullOrWhiteSpace(page.CategoryLabel) ? Array.Empty<string>() : [page.CategoryLabel],
            gallery,
            CoverUrlOf(page.CoverRef)));
    }

    /// <summary>画廊最多给多少项（超出截断，避免整页塞满同名静态图）。</summary>
    private const int MaxGalleryItems = 24;

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
