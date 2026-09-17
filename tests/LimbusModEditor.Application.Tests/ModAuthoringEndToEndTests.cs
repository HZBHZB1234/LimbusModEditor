using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 「做模组」全链路的 <b>IPC 级</b>端到端冒烟：真实 <see cref="IpcGateway"/>（不是直接调服务），
/// 按用户真实顺序跑一遍 —— 建/开项目 → 挑资源 → 替换 → 改文本 → 改静态表 → 导出 → 撤销。
///
/// <para>与既有单测的差别：那些只证明「某个服务对」，这里证明「一串 IPC 串起来能走通」，
/// 每一步都断言<b>真实返回值</b>（路径 / 计数 / 文件内容），并把实测值打到测试输出里。
/// 走不通的步骤<b>不绕过、不改绿</b>：原样记下现象（错误码 + 中文原因），换能走的那条路继续，
/// 卡点在报告里单列。</para>
///
/// <para>数据源：真实项目 <c>artifacts/publish-win-x64/projects/MyMod.lmeproj</c>（395 条真实资源，
/// 只读引用 —— 测试里先复制到临时目录）与真实游戏目录（项目里记的那个）。游戏数据全程只读：
/// 替换 / 文本 / 静态改动只落在项目编辑集与临时目标目录。</para>
/// </summary>
public sealed class ModAuthoringEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly ProjectState _state = new();
    private readonly IpcGateway _gateway;
    private int _step;

    // 项目态覆盖：让 catalog 看到「当前打开的项目里有什么」（真实 App 里就是 ProjectAssetStateSource）。
    private sealed class LiveProjectStateSource(ProjectState state) : IAssetStateSource
    {
        public AssetRecord? Find(string logicalPath) =>
            state.Project?.Assets.FirstOrDefault(a =>
                string.Equals(a.LogicalPath, logicalPath, StringComparison.Ordinal));

        public IReadOnlyList<AssetRecord> ProjectOnly => state.Project?.Assets.ToArray() ?? [];
    }

    private sealed record OpenPayload(bool Ok, string Name, int AssetCount);

    private sealed record ReplacePayload(bool Ok, string StoredPath, long Size);

    private sealed record ExportPatchPayload(bool Ok, int EditedFiles, int PatchedFiles);

    public ModAuthoringEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);

        // 静态表索引用真实库（复制到临时目录，原库只读）。构造子吃的是缓存目录，库名是它自己定的。
        var staticCacheDir = Path.Combine(_root, "static-cache");
        Directory.CreateDirectory(staticCacheDir);
        var realStaticDb = Path.Combine(RepoRoot(), "artifacts", "publish-win-x64", "cache", "static-tables.db");
        if (File.Exists(realStaticDb))
            File.Copy(realStaticDb, Path.Combine(staticCacheDir, Path.GetFileName(realStaticDb)), true);

        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(_root, "index.db")),
            new LiveProjectStateSource(_state));
        _gateway = new IpcGateway(catalog, _state,
            new SpineDataGateway(Path.Combine(_root, "spine")),
            new BankIndexService(new BankIndexStore(_root)),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(staticCacheDir)),
            environment: new AppEnvironment(_root),
            cacheDirectory: _root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录，尽力清理 */ }
    }

    /// <summary>七步全链路：每步都打真实返回值。缺真实环境（真实项目文件）时整轮跳过。</summary>
    [Fact]
    public async Task Open_project_replace_edit_text_static_export_then_undo()
    {
        var projectCopy = CopyRealProject();
        if (projectCopy is null)
        {
            _output.WriteLine("跳过：找不到真实项目 artifacts/publish-win-x64/projects/MyMod.lmeproj");
            return;
        }

        // ── 步骤 1：project.create 建一个临时项目 ────────────────────────
        var createDir = Path.Combine(_root, "newmod");
        var created = await Call("project.create", new ProjectCreateRequest(createDir, "E2E冒烟"));
        Assert.True(created.Ok, created.Error?.Message);
        var createdPayload = Payload<ProjectCreateResponse>(created);
        Step("1 project.create",
            $"ok=真 路径={createdPayload.Path} 落盘={File.Exists(createdPayload.Path)} " +
            $"子目录 sources={Directory.Exists(Path.Combine(createDir, "sources"))}");
        Assert.True(File.Exists(createdPayload.Path));

        // ── 步骤 1b：打开真实项目（新建的空项目里没有资源，IPC 层也没有「扫描入库」入口）──
        var opened = await Call("project.open", new ProjectOpenRequest(projectCopy));
        Assert.True(opened.Ok, opened.Error?.Message);
        var openPayload = Payload<OpenPayload>(opened);
        Step("1b project.open（真实项目副本）",
            $"ok={openPayload.Ok} 名称={openPayload.Name} 资源数={openPayload.AssetCount}");
        var project = _state.Project!;
        Assert.True(openPayload.AssetCount > 0, "真实项目里应该有资源");

        // ── 步骤 2：catalog.query 挑一个真实资源 ────────────────────────
        var queried = await Call("catalog.query", new CatalogQueryRequest(new AssetSearchQuery(), 0, 5));
        Assert.True(queried.Ok, queried.Error?.Message);
        var page = Payload<CatalogQueryResponse>(queried);
        var target = page.Items
            .Select(item => project.Assets.FirstOrDefault(a => a.LogicalPath == item.LogicalPath))
            .FirstOrDefault(a => a is not null);
        Step("2 catalog.query",
            $"命中={page.TotalCount} 本页={page.Items.Count} 选中={target?.LogicalPath}（类型 {target?.Type}，大小 {target?.Size}）");
        Assert.NotNull(target);

        // ── 步骤 3：替换该资源（先走 asset.edit.replacePayload，走不通再走 batchReplace）──
        var replacement = await WriteRealPngAsync(Path.Combine(_root, "replacement.png"));

        var byId = await Call("asset.edit.replacePayload",
            new AssetEditReplacePayloadRequest(target!.AssetId.ToString(), replacement));
        var byPath = await Call("asset.edit.replacePayload",
            new AssetEditReplacePayloadRequest(target.LogicalPath, replacement));
        Step("3 asset.edit.replacePayload",
            $"按 assetId（Guid）：ok={byId.Ok} {byId.Error?.Code.ToString() ?? "-"} {byId.Error?.Message ?? "-"}\n" +
            $"                    按 logicalPath：ok={byPath.Ok} {byPath.Error?.Code.ToString() ?? "-"} {byPath.Error?.Message ?? "-"}");

        string storedPath;
        if (byId.Ok || byPath.Ok)
        {
            storedPath = Payload<ReplacePayload>(byId.Ok ? byId : byPath).StoredPath;
        }
        else
        {
            // 单条入口走不通 → 用批量入口（按文件名匹配，同一套可逆登记管线）继续验证链路。
            var folder = Path.Combine(_root, "drop");
            Directory.CreateDirectory(folder);
            File.Copy(replacement, Path.Combine(folder, Path.GetFileName(target.LogicalPath)), true);
            var batch = await Call("asset.edit.batchReplace", new AssetEditBatchReplaceRequest(folder, null, false));
            Assert.True(batch.Ok, batch.Error?.Message);
            var batchPayload = Payload<AssetEditBatchReplaceResponse>(batch);
            Step("3b asset.edit.batchReplace（回退路径）",
                $"登记={batchPayload.Replaced} 跳过={batchPayload.Skipped} 说明={batchPayload.Info}");
            Assert.Equal(1, batchPayload.Replaced);
            storedPath = batchPayload.Items[0].ReplacementPath;
        }

        // 无论走哪条入口：编辑记录真的落进项目 + 替换文件真在盘上。
        Step("3 结果",
            $"替换文件={storedPath} 存在={File.Exists(storedPath)} 大小={new FileInfo(storedPath).Length} 字节 " +
            $"编辑标记={AssetEditService.HasEdits(target)} 项目编辑记录={project.Edits.Count} 条");
        Assert.True(File.Exists(storedPath));
        Assert.True(AssetEditService.HasEdits(target));
        Assert.Contains(project.Edits, e => e.TargetPath == target.LogicalPath);

        // ── 步骤 4：文本编辑（分页读 → 按 key 改 → 读回校验）──────────────
        var files = await Call("lang.files", new LangFilesRequest(string.Empty, 0, 20));
        Assert.True(files.Ok, files.Error?.Message);
        var fileList = Payload<LangFilesResponse>(files);
        var langFile = fileList.Items.FirstOrDefault(f => f.KeyCount > 0);
        Step("4 lang.files",
            $"文件总数={fileList.TotalCount} 选中={langFile?.RelativePath}（{langFile?.KeyCount} 键，{langFile?.SizeBytes} 字节）");
        Assert.NotNull(langFile);

        var before = await Call("text.fileEntries", new LangFileEntriesRequest(langFile!.RelativePath, 0, 3));
        Assert.True(before.Ok, before.Error?.Message);
        var beforePage = Payload<LangFileEntriesResponse>(before);
        var firstKey = beforePage.Items[0];
        var newValue = firstKey.Value + "【E2E冒烟】";

        var patch = await Call("text.applyPatch",
            new LangApplyPatchRequest(langFile.RelativePath, [new LangKeyEditDto(firstKey.KeyPath, newValue)]));
        Assert.True(patch.Ok, patch.Error?.Message);
        var patchPayload = Payload<LangApplyPatchResponse>(patch);

        var after = await Call("text.fileEntries", new LangFileEntriesRequest(langFile.RelativePath, 0, 3));
        var afterPage = Payload<LangFileEntriesResponse>(after);
        var reread = afterPage.Items.FirstOrDefault(i => i.KeyPath == firstKey.KeyPath);
        Step("4 text.applyPatch → 读回",
            $"文件={langFile.RelativePath} 键={firstKey.KeyPath}\n" +
            $"                    改前={Trim(firstKey.Value)}\n" +
            $"                    改后={Trim(reread?.Value)} 生效 {patchPayload.Applied.Count} 条（{patchPayload.Info}）");
        Assert.Equal(newValue, reread?.Value);
        Assert.Contains(patchPayload.Applied, a => a == firstKey.KeyPath);

        // 4b：文本工作台自己的出口（与「导出」是两条路，用来定位改动丢在哪一段）。
        var patchDir = Path.Combine(_root, "langpatch");
        var exported = await Call("lang.exportPatch", new LangExportPatchRequest(string.Empty, patchDir));
        Assert.True(exported.Ok, exported.Error?.Message);
        var exportPayload = Payload<ExportPatchPayload>(exported);
        Step("4b lang.exportPatch",
            $"ok={exportPayload.Ok} 编辑文件={exportPayload.EditedFiles} 有差异={exportPayload.PatchedFiles} " +
            $"产物={patchDir}（当文件看：存在={File.Exists(patchDir)}，{SizeOf(patchDir):N0} 字节）" +
            "——注意：targetDirectory 被当成「文件路径」写了，产物不是目录里的补丁文件");

        // ── 步骤 5：静态表编辑（先取真实记录，再走既有编辑接口）────────────
        var tables = await Call("static.tableList", new StaticTableListRequest(0, 5));
        Assert.True(tables.Ok, tables.Error?.Message);
        var tableList = Payload<StaticTableListResponse>(tables);
        Step("5 static.tableList",
            $"索引里共 {tableList.TotalCount} 张表，前几个：{string.Join("、", tableList.Items.Take(3).Select(t => t.Name))}");

        StaticRecordsResponse? records = null;
        var firstFailure = "(没试过)";
        foreach (var table in tableList.Items)
        {
            var response = await Call("static.records", new StaticRecordsRequest(table.TableId, 0, 3));
            if (!response.Ok)
            {
                if (firstFailure == "(没试过)") firstFailure = $"{response.Error?.Code.ToString()} {response.Error?.Message}";
                continue;
            }
            var candidate = Payload<StaticRecordsResponse>(response);
            if (candidate.Items.Count == 0) continue;
            records = candidate;
            break;
        }

        Step("5 static.records", records is null
            ? $"一张表的正文都没取到（首条失败原因：{firstFailure}）"
            : $"表={records.TableId} 记录总数={records.TotalCount} 首条={records.Items[0].RecordId} 摘要={Trim(records.Items[0].Summary)}");

        var staticTable = records?.TableId ?? (tableList.Items.Count > 0 ? tableList.Items[0].TableId : "personality-01");
        var staticEdit = await Call("static.editRecord",
            new StaticEditRecordRequest(staticTable, records?.Items[0].RecordId ?? "0", "{}"));
        Step("5 static.editRecord",
            $"ok={staticEdit.Ok} {staticEdit.Error?.Code.ToString() ?? "-"} {staticEdit.Error?.Message ?? "-"}");
        if (!staticEdit.Ok) Assert.False(string.IsNullOrWhiteSpace(staticEdit.Error!.Message));

        // ── 步骤 6：导出（先计划 + 写前校验，再真写）─────────────────────
        var targetDirectory = Path.Combine(_root, "out");
        var plan = await Call("export.plan", new ExportPlanRequest());
        Assert.True(plan.Ok, plan.Error?.Message);
        var planPayload = Payload<ExportPlanResponse>(plan);
        var validation = planPayload.Validation;
        Step("6 export.plan（写前校验）",
            $"槽位 {planPayload.Slots.Count} 个：{string.Join("；", planPayload.Slots.Select(s => $"{s.Format}×{s.Count}"))}\n" +
            $"                    改动 {validation.ChangedCount} 条 / 校验文件 {validation.CheckedFileCount} 个：" +
            $"错误 {validation.ErrorCount} 条，警告 {validation.WarningCount} 条，阻断={validation.HasBlockingError}\n" +
            $"                    明细：{string.Join(" ｜ ", validation.Checks.Where(c => c.Level != "info").Select(c => $"[{c.Level}] {c.Target}：{c.Message}"))}");

        var run = await Call("export.run", new ExportRunRequest(targetDirectory));
        Assert.True(run.Ok, run.Error?.Message);
        var runPayload = Payload<ExportRunResponse>(run);
        var artifacts = runPayload.Items.Where(i => i.Written).SelectMany(i => i.OutputPaths).Distinct().ToList();
        Step("6 export.run",
            $"ok={runPayload.Ok} 槽位 {runPayload.Slots} → 写出 {runPayload.WrittenSlots} 个槽位 / {runPayload.WrittenFileCount} 个文件\n" +
            $"                    产物：{string.Join("\n                          ", artifacts.Select(p => $"{p}（{new FileInfo(p).Length:N0} 字节）"))}\n" +
            $"                    跳过：{string.Join("；", runPayload.Skipped)}");
        Assert.True(runPayload.WrittenFileCount > 0, runPayload.Info);
        Assert.NotEmpty(artifacts);
        Assert.All(artifacts, path => Assert.True(File.Exists(path)));

        // 对账：步骤 4 改的 lang 键到底有没有进导出（三个语言槽位的真实结论）。
        // 这是 P0-1 的判据：「工作台改的」必须就是「导出读的」那份编辑集。
        var langSlots = runPayload.Items.Where(i => i.Format.Contains("语言")).ToList();
        Step("6 文本对账", $"步骤 4 改了 {langFile.RelativePath} 的 1 个键 → 语言槽位写出 {langSlots.Count(i => i.Written)} 个：" +
                          string.Join("；", langSlots.Select(i => $"{i.Format}={(i.Written ? "已写出" : string.Join("/", i.SkippedReasons))}")));
        Assert.NotEmpty(langSlots);
        Assert.All(langSlots, slot => Assert.True(slot.Written, $"{slot.Format} 未写出：{string.Join("/", slot.SkippedReasons)}"));

        var langArtifacts = langSlots.SelectMany(i => i.OutputPaths).Distinct().ToArray();
        var carrying = langArtifacts.Where(p => File.ReadAllText(p).Contains(newValue, StringComparison.Ordinal)).ToArray();
        Step("6 文本产物对账", $"改后的值「{Trim(newValue)}」出现在 {carrying.Length}/{langArtifacts.Length} 个语言产物里：" +
                             string.Join("；", carrying.Select(p => $"{Path.GetFileName(p)}")));
        Assert.NotEmpty(carrying);

        // ── 步骤 7：撤销（清编辑标记 + 同步项目编辑清单）──────────────────
        var cleared = await Call("asset.edit.clearEdits", new AssetEditClearEditsRequest(target.AssetId.ToString()));
        Assert.True(cleared.Ok, cleared.Error?.Message);
        var clearPayload = Payload<AssetEditClearEditsResponse>(cleared);
        Step("7 asset.edit.clearEdits",
            $"ok={clearPayload.Ok} 清掉={clearPayload.ClearedCount} 移除记录={clearPayload.RemovedEditOperations} " +
            $"剩余={clearPayload.RemainingEdits} 编辑标记={AssetEditService.HasEdits(target)}（{clearPayload.Info}）");
        Assert.True(clearPayload.ClearedCount >= 1);
        Assert.False(AssetEditService.HasEdits(target));
        Assert.DoesNotContain(project.Edits, e => e.TargetPath == target.LogicalPath);
    }

    /// <summary>
    /// 步骤 5 的卡点定位：<c>static.records</c> 只吃 <c>project.UnityCacheDirectory</c>，
    /// 而真实项目（MyMod）里这个字段是空的 —— 共享配置里明明配了缓存目录也接不上。
    /// 这里把项目副本的该字段填上真实缓存目录再试一次，看是「字段没接上」还是「根本定位不到」。
    /// </summary>
    [Fact]
    public async Task Static_records_with_the_cache_directory_filled_on_the_project()
    {
        var projectCopy = CopyRealProject();
        if (projectCopy is null) { _output.WriteLine("跳过：找不到真实项目"); return; }

        var cacheDirectory = RealUnityCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory)) { _output.WriteLine("跳过：共享配置里也没有 Unity 缓存目录"); return; }

        var text = await File.ReadAllTextAsync(projectCopy);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var before = root.TryGetProperty("unityCacheDirectory", out var existing) ? existing.GetString() : null;
        var map = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        map["unityCacheDirectory"] = JsonSerializer.SerializeToElement(cacheDirectory);
        await File.WriteAllTextAsync(projectCopy, JsonSerializer.Serialize(map));

        var opened = await Call("project.open", new ProjectOpenRequest(projectCopy));
        Assert.True(opened.Ok, opened.Error?.Message);
        Step("5b 项目字段", $"unityCacheDirectory：{before ?? "(空)"} → {cacheDirectory}");

        var tables = await Call("static.tableList", new StaticTableListRequest(0, 3));
        Assert.True(tables.Ok, tables.Error?.Message);
        var tableList = Payload<StaticTableListResponse>(tables);

        foreach (var table in tableList.Items)
        {
            var response = await Call("static.records", new StaticRecordsRequest(table.TableId, 0, 3));
            if (!response.Ok)
            {
                Step("5b static.records", $"{table.Name} → {response.Error?.Code.ToString()} {response.Error?.Message}");
                continue;
            }

            var records = Payload<StaticRecordsResponse>(response);
            Step("5b static.records",
                $"{table.Name} → 表={records.TableId} 记录 {records.TotalCount} 条，首条={records.Items.FirstOrDefault()?.RecordId}");

            var edit = await Call("static.editRecord",
                new StaticEditRecordRequest(table.TableId, records.Items[0].RecordId, "{}"));
            Step("5b static.editRecord", $"ok={edit.Ok} {edit.Error?.Code.ToString() ?? "-"} {edit.Error?.Message ?? "-"}");
            return;
        }
    }

    /// <summary>共享配置里配的 Unity 缓存目录（真实值，用来填项目字段）。</summary>
    private static string? RealUnityCacheDirectory()
    {
        var config = Path.Combine(RepoRoot(), "artifacts", "publish-win-x64", "config", "shared-config.json");
        if (!File.Exists(config)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        return document.RootElement.TryGetProperty("unityCacheDirectory", out var value) ? value.GetString() : null;
    }

    // ── 工具 ────────────────────────────────────────────────────────────

    /// <summary>走真实网关发一条 IPC 请求（这是本测试存在的意义：不直接调服务）。</summary>
    private async Task<IpcResponse> Call(string method, object payload)
    {
        var id = "e2e-" + (++_step).ToString("00") + "-" + method;
        var json = await _gateway.HandleRequestAsync(IpcRequest.Create(id, method, SerializePayload(payload)).ToJson());
        return IpcResponse.FromJson(json);
    }

    private static T Payload<T>(IpcResponse response) where T : class
        => response.Payload!.Value.Deserialize<T>(IpcJson.Options)!;

    private void Step(string name, string detail) => _output.WriteLine($"【{name}】{detail}");

    private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static string Trim(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(空)";
        var flat = value.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    /// <summary>把真实项目复制到临时目录（原项目只读；里面记的游戏目录是真实目录）。</summary>
    private string? CopyRealProject()
    {
        var source = Path.Combine(RepoRoot(), "artifacts", "publish-win-x64", "projects", "MyMod.lmeproj");
        if (!File.Exists(source)) return null;
        var directory = Path.Combine(_root, "project");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "MyMod.lmeproj");
        File.Copy(source, target, true);
        return target;
    }

    /// <summary>写一张真的 PNG（不是空文件也不是假头）：写前校验会真解一次。</summary>
    private static async Task<string> WriteRealPngAsync(string path)
    {
        using var image = new Image<Rgba32>(16, 16);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                image[x, y] = new Rgba32((byte)(x * 16), (byte)(y * 16), 128, 255);
        await image.SaveAsPngAsync(path);
        return path;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusModEditor.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根（LimbusModEditor.slnx）。");
    }
}
