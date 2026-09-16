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

    /// <summary>处理一条来自页面的请求 JSON，返回响应 JSON。</summary>
    public string HandleRequest(string requestJson)
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
            var response = Dispatch(request);
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

    private IpcResponse Dispatch(IpcRequest request) => request.Method switch
    {
        // ── 2.1 资源目录 ──────────────────────────────────────────────
        "catalog.query" => HandleCatalogQuery(request),
        "catalog.count" => HandleCatalogCount(request),
        "catalog.locate" => HandleCatalogLocate(request),
        "catalog.containerRoots" => HandleContainerRoots(request),
        "catalog.containerChildren" => HandleContainerChildren(request),

        // ── 2.2 资产预览/读取 ────────────────────────────────────────
        "asset.preview" => HandleAssetPreview(request),
        "asset.readText" => HandleAssetReadText(request),

        // ── 2.3 资产编辑 ──────────────────────────────────────────────
        "asset.edit.replacePayload" => HandleAssetEditReplacePayload(request),
        "asset.edit.fieldEdit" => HandleAssetEditFieldEdit(request),
        "asset.edit.spriteMetadata" => HandleAssetEditSpriteMetadata(request),

        // ── 2.4 Spine（服务已迁移至 SpineData，见 t24）──────────────
        "spine.locate" => HandleSpineLocate(request),
        "spine.export" => HandleSpineExport(request),

        // ── 2.5 关联与精确跳转 ────────────────────────────────────────
        "relation.reveal" => HandleRelationReveal(request),
        "relation.describe" => HandleRelationDescribe(request),
        "relation.links" => HandleRelationLinks(request),

        // ── 2.6 项目 / 导出 ──────────────────────────────────────────
        "project.open" => HandleProjectOpen(request),
        "project.save" => HandleProjectSave(request),
        "export.plan" => HandleExportPlan(request),
        "export.run" => HandleExportRun(request),

        // ── 2.7 配置 / UI 状态 ────────────────────────────────────────
        "config.read" => HandleConfigRead(request),
        "config.write" => HandleConfigWrite(request),
        "uiState.read" => HandleUiStateRead(request),
        "uiState.write" => HandleUiStateWrite(request),

        // ── 音频工作台 ────────────────────────────────────────────────
        "bank.list" => HandleBankList(request),
        "bank.samples" => HandleBankSamples(request),
        "bank.preview" => HandleBankPreview(request),
        "bank.exportRebank" => HandleBankExportRebank(request),

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

        // ── 取消 ──────────────────────────────────────────────────────
        "cancel" => HandleCancel(request),

        _ => IpcResponse.Failure(request.Id, IpcErrorCode.Internal, "未知方法：" + request.Method)
    };

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

    private IpcResponse HandleAssetReadText(IpcRequest request)
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
        var text = UnityCacheMaterializationService.MaterializeForEditingAsync(project, asset, projectRoot).GetAwaiter().GetResult();
        if (req.CharLimit is { } limit && text.Length > limit)
            text = text[..limit];
        return IpcResponse.Success(request.Id, new AssetReadTextResponse(text));
    }

    // ── 2.3 资产编辑 ──────────────────────────────────────────────

    private IpcResponse HandleAssetEditReplacePayload(IpcRequest request)
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
        var result = _assetEdits.ReplaceFromFileAsync(project, Guid.Parse(req.AssetId), req.ReplacementPath, projectDir).GetAwaiter().GetResult();
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

    private IpcResponse HandleSpineLocate(IpcRequest request)
    {
        var req = DeserializePayload<SpineLocateRequest>(request);
        var (data, error) = _spineData.GetSpineDataByPathAsync(req.AssetId).GetAwaiter().GetResult();
        if (data is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, error ?? "未找到 Spine 素材");

        var files = new SpineFiles(
            Skeleton: "skeleton.json",
            Atlas: "atlas.txt",
            Pages: data.AvailablePages.ToArray());
        return IpcResponse.Success(request.Id, new SpineLocateResponse(data.Label, files, data.AvailablePages));
    }

    private IpcResponse HandleSpineExport(IpcRequest request)
    {
        var req = DeserializePayload<SpineExportRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var assetId in req.AssetIds)
        {
            var (data, error) = _spineData.GetSpineDataByPathAsync(assetId).GetAwaiter().GetResult();
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

    private IpcResponse HandleProjectOpen(IpcRequest request)
    {
        var req = DeserializePayload<ProjectOpenRequest>(request);
        if (!File.Exists(req.ProjectFile))
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"项目文件不存在：{req.ProjectFile}");

        var project = _projects.LoadAsync(req.ProjectFile).GetAwaiter().GetResult();
        _projectState.SetProject(project, req.ProjectFile);
        return IpcResponse.Success(request.Id, new { ok = true, name = project.Name, assetCount = project.Assets.Count });
    }

    private IpcResponse HandleProjectSave(IpcRequest request)
    {
        var req = DeserializePayload<ProjectSaveRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var projectFile = _projectState.ProjectFile ?? req.ProjectFile;

        _projects.SaveAsync(project, projectFile).GetAwaiter().GetResult();
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

    private IpcResponse HandleExportRun(IpcRequest request)
    {
        var req = DeserializePayload<ExportRunRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");
        var langEdits = _projectState.LangEdits ?? new LangEditSession();
        var staticEdits = _projectState.StaticEdits ?? new StaticEditSession();

        var plan = _exportPlan.Plan(project, req.TargetDirectory, langEdits, staticEdits);
        var projectRoot = _projectState.ProjectFile != null ? Path.GetDirectoryName(_projectState.ProjectFile)! : string.Empty;
        var result = _exportService.ExportAsync(project, projectRoot, plan, new ModExportPlanContext(),
            new Progress<string>(msg => Log.Info("导出进度: {0}", msg)), CancellationToken.None).GetAwaiter().GetResult();
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

    private IpcResponse HandleBankExportRebank(IpcRequest request)
    {
        var req = DeserializePayload<BankExportRebankRequest>(request);
        var project = _projectState.Project;
        if (project is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.InvalidQuery, "请先打开项目");

        var asset = project.Assets.FirstOrDefault(a => a.LogicalPath == req.BankId);
        if (asset is null)
            return IpcResponse.Failure(request.Id, IpcErrorCode.NotFound, $"未找到 Bank：{req.BankId}");

        var fsb = _bankAudio.ReadFsbAsync(asset).GetAwaiter().GetResult();
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
        return IpcResponse.Success(request.Id, new { offset = 0, page = 0 });
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
}
