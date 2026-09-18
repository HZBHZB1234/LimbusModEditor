using LimbusModEditor.Application.Build;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// 导出的一套 Spine 三件套在磁盘上的形态（相对导出目录的路径 + 字节数）。
///
/// <para>路径一律用 <c>/</c> 分隔且相对 <c>outputDirectory</c>，前端可以直接拼显示，
/// 不必知道宿主磁盘布局。</para>
/// </summary>
/// <param name="RelativePath">相对导出目录的路径（如 <c>10103_gacksung/10103_gacksung.json</c>）。</param>
/// <param name="Role">文件角色：<c>skeleton</c> / <c>atlas</c> / <c>texture</c>。</param>
/// <param name="Bytes">实际落盘字节数（写完 <c>stat</c> 得来，不是内存里的估算）。</param>
public sealed record SpineExportedFile(string RelativePath, string Role, long Bytes);

/// <summary>一条挂点（refKey）的导出结果。<b>失败也是结果</b>，带中文原因，不中断整批。</summary>
/// <param name="RefKey">容器路径（取数键）。</param>
/// <param name="Name">骨架名（导出子目录名，也是文件名主干）。</param>
/// <param name="Ok">这条是否成功写出至少一件套。</param>
/// <param name="OutputDirectory">这条自己的输出目录（<c>refKey</c> 取不到时为 null）。</param>
/// <param name="Files">逐文件清单（相对路径 + 字节数）。</param>
/// <param name="SkippedFiles">被「不覆盖」策略跳过的文件相对路径。</param>
/// <param name="Reason">失败时的中文原因；成功时为 null。</param>
public sealed record SpineExportItemResult(
    string RefKey,
    string Name,
    bool Ok,
    string? OutputDirectory,
    IReadOnlyList<SpineExportedFile> Files,
    IReadOnlyList<string> SkippedFiles,
    string? Reason);

/// <summary>一次导出的汇总。</summary>
/// <param name="Ok">整批是否没有任何失败（部分失败时为 false，但已成功的那几条<b>照常留在磁盘上</b>）。</param>
/// <param name="OutputDirectory">本次导出根目录（<b>绝对路径</b>）。</param>
/// <param name="Items">逐条结果（顺序与请求一致）。</param>
/// <param name="Overwrite">本次采用的覆盖策略（回显，便于页面提示用户）。</param>
/// <param name="Info">一句话中文汇总。</param>
/// <param name="Cancelled">是否被取消（取消时已写出的文件保留，未开始的那几条标为取消）。</param>
public sealed record SpineExportResult(
    bool Ok,
    string OutputDirectory,
    IReadOnlyList<SpineExportItemResult> Items,
    string Overwrite,
    string Info,
    bool Cancelled)
{
    /// <summary>成功条数。</summary>
    public int SucceededCount => Items.Count(i => i.Ok);

    /// <summary>失败条数。</summary>
    public int FailedCount => Items.Count(i => !i.Ok);

    /// <summary>写出文件总数。</summary>
    public int FileCount => Items.Sum(i => i.Files.Count);

    /// <summary>写出文件总字节数。</summary>
    public long TotalBytes => Items.Sum(i => i.Files.Sum(f => f.Bytes));
}

/// <summary>
/// 覆盖策略：既有文件怎么办。默认<b>不覆盖</b>（导出的东西不该悄悄盖掉用户已有的产物）。
/// </summary>
public enum SpineExportOverwrite
{
    /// <summary>跳过已存在的文件，在结果里如实列出（默认）。</summary>
    Skip,

    /// <summary>覆盖已存在的文件（仍走临时文件 + 移动，不会留下半个文件）。</summary>
    Overwrite,
}

/// <summary>
/// <c>spine.export</c> 的落盘实现：把某个 refKey 解析出的三件套<b>按原始文件形态</b>写到磁盘。
///
/// <para><b>语义（方案 A：单条 + 可选批量）</b>：请求给一个 <c>targetDirectory</c> 与若干
/// <c>refKey</c>；每条 refKey 在目标目录下写一个<b>以骨架名为名的子目录</b>，里面是
/// <c>&lt;骨架名&gt;.json</c> / <c>&lt;骨架名&gt;.atlas.txt</c> / <c>&lt;页名&gt;.png</c>。
/// 这正是 Spine 运行时与 spine-unity 期望的磁盘形态 —— 用户拿到就能直接喂给编辑器。</para>
///
/// <para><b>为什么要子目录</b>：不同挂点可能解出同名骨架（全库 653 套里同名不同 bundle 的情形
/// 真实存在），平铺写进一个目录会互相覆盖，而且用户也分不清哪几个文件是一套。
/// 一个 refKey 一个目录是幂等的：<b>同一条导出两次，产出的目录树完全一致</b>。</para>
///
/// <para><b>只读游戏数据</b>：这里只从 <see cref="ISpineDataGateway"/> 取<b>已经在内存里</b>的
/// 字节（网关只读 bundle，从不写），唯一被写的是调用方给的 <paramref name="targetDirectory"/>。</para>
///
/// <para><b>不做的事</b>：不重写解析（三件套一律走网关，含 prefab 引用链与结果缓存）、
/// 不造假文件（取不到就带中文原因记一条失败，绝不写空文件凑数）、
/// 不自己实现原子写（一律走既有 <see cref="AtomicOutput"/>：临时文件 + 移动）。</para>
/// </summary>
public sealed class SpineExportService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ISpineDataGateway _gateway;

    public SpineExportService(ISpineDataGateway gateway)
        => _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    /// <summary>
    /// 导出若干条 Spine 三件套到 <paramref name="targetDirectory"/>。
    ///
    /// <para><b>单条失败不中断整批</b>：逐条记结果，最后统一返回。取消时也返回已写出的部分，
    /// 剩下没轮到的条目标为「已取消」，<b>不</b>谎报成功。</para>
    /// </summary>
    /// <param name="refKeys">容器路径列表（来自 <c>spine.catalog</c> 的 <c>refKey</c>）。</param>
    /// <param name="targetDirectory">导出根目录（由调用方经文件夹选择给定）。</param>
    /// <param name="overwrite">覆盖策略（默认跳过已存在）。</param>
    /// <param name="progress">进度回调（每开始一条报一次，供 <c>progress</c> 事件）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SpineExportResult> ExportAsync(
        IReadOnlyList<string> refKeys,
        string targetDirectory,
        SpineExportOverwrite overwrite = SpineExportOverwrite.Skip,
        IProgress<SpineExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var overwriteLabel = Describe(overwrite);
        if (refKeys is null || refKeys.Count == 0)
            return Failed(string.Empty, [], overwriteLabel, "没有给任何 refKey，无从导出。");

        if (string.IsNullOrWhiteSpace(targetDirectory))
            return Failed(string.Empty, [], overwriteLabel, "没有指定导出目录。");

        string root;
        try
        {
            root = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn(ex, "创建导出目录失败：{0}", targetDirectory);
            return Failed(targetDirectory, [], overwriteLabel, $"无法创建导出目录 \"{targetDirectory}\"：{ex.Message}");
        }

        var items = new List<SpineExportItemResult>();
        var cancelled = false;
        var index = 0;
        foreach (var refKey in refKeys)
        {
            index++;
            progress?.Report(new SpineExportProgress(index, refKeys.Count, refKey, "正在取三件套…"));

            if (cancellationToken.IsCancellationRequested)
            {
                // 取消后不再动盘：剩下的如实标成「已取消」，不假装成功也不假装失败。
                cancelled = true;
                items.Add(new SpineExportItemResult(refKey, DisplayNameOf(refKey), false, null, [], [],
                    "已取消：本次导出被中断，这一条没有开始。"));
                continue;
            }

            items.Add(await ExportOneAsync(refKey, root, overwrite, cancellationToken).ConfigureAwait(false));
        }

        var result = new SpineExportResult(
            items.All(i => i.Ok),
            root,
            items,
            overwriteLabel,
            Summarize(items, cancelled),
            cancelled);

        Log.Info("Spine 导出完成：{0} → {1}", result.Info, root);
        return result;
    }

    /// <summary>写一条：取数（走网关缓存）→ 逐文件原子写。</summary>
    private async Task<SpineExportItemResult> ExportOneAsync(
        string refKey, string root, SpineExportOverwrite overwrite, CancellationToken cancellationToken)
    {
        SpineRawData? data;
        string? error;
        try
        {
            (data, error) = await _gateway.GetSpineDataByPathAsync(refKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "导出时取 Spine 数据失败：{0}", refKey);
            return new SpineExportItemResult(refKey, DisplayNameOf(refKey), false, null, [], [],
                $"取 Spine 数据时出错：{ex.Message}");
        }

        if (data is null)
            // 网关给的中文原因已经区分了「bundle 不在本机」/「不是 Spine」/「骨架或图集缺失」，
            // 这里原样透出，不再包一层自己的措辞（否则两处措辞会各自漂移）。
            return new SpineExportItemResult(refKey, DisplayNameOf(refKey), false, null, [], [],
                string.IsNullOrWhiteSpace(error) ? "没能取到 Spine 三件套。" : error);

        var name = SafeName(data.Label);
        var directory = Path.Combine(root, name);
        var written = new List<SpineExportedFile>();
        var skipped = new List<string>();

        try
        {
            Directory.CreateDirectory(directory);

            // ① 骨架：扩展名跟着真实格式走（json / binary 的 .skel），不硬编 .json。
            var skeletonName = name + (data.SkeletonFormat == "binary" ? ".skel" : ".json");
            await WriteAsync(directory, root, skeletonName, data.SkeletonBytes, "skeleton",
                overwrite, written, skipped, cancellationToken).ConfigureAwait(false);

            // ② 图集：固定 .atlas.txt（与素材原始形态一致）。
            var atlasName = name + ".atlas.txt";
            await WriteAsync(directory, root, atlasName,
                new System.Text.UTF8Encoding(false).GetBytes(data.AtlasText), "atlas",
                overwrite, written, skipped, cancellationToken).ConfigureAwait(false);

            // ③ 纹理页：按图集页序（materials 顺序）命名，页名即图集里写的名字（如 back.png）。
            foreach (var page in data.PageBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteAsync(directory, root, PageFileName(page.Key), page.Value, "texture",
                    overwrite, written, skipped, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 已写出的文件保留（原子写保证每个文件要么完整要么不存在），这一条如实记为取消。
            return new SpineExportItemResult(refKey, name, false, directory, written, skipped,
                "已取消：这一条写到一半被中断（已写出的文件保留在磁盘上）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error(ex, "Spine 导出写盘失败：{0} → {1}", refKey, directory);
            return new SpineExportItemResult(refKey, name, false, directory, written, skipped,
                $"写盘失败：{ex.Message}");
        }

        return new SpineExportItemResult(refKey, name, true, directory, written, skipped, null);
    }

    /// <summary>原子写一个文件并记进清单；已存在且策略为「跳过」时不落盘、如实记 skipped。</summary>
    private static async Task WriteAsync(
        string directory, string root, string fileName, byte[] bytes, string role,
        SpineExportOverwrite overwrite, List<SpineExportedFile> written, List<string> skipped,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(directory, fileName);
        var relative = Path.GetRelativePath(root, target).Replace('\\', '/');

        if (overwrite == SpineExportOverwrite.Skip && File.Exists(target))
        {
            skipped.Add(relative);
            return;
        }

        // 走既有原子写：临时文件 + 移动。任何异常都留给调用方逐条记结果，不中断整批。
        await AtomicOutput.WriteAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        written.Add(new SpineExportedFile(relative, role, new FileInfo(target).Length));
    }

    /// <summary>
    /// 纹理页文件名：页名来自图集正文（<b>顺序即 <c>materials</c> 顺序</b>），通常已经是
    /// <c>back.png</c> 这种带扩展名的形式；没带就补 <c>.png</c>（我们落盘的就是 PNG 编码结果）。
    /// </summary>
    private static string PageFileName(string pageName)
    {
        var clean = SafeName(pageName);
        return clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".png";
    }

    /// <summary>
    /// 把任意素材里的名字变成安全文件名/目录名：<b>只做字符净化，不改语义</b>
    /// （不截断、不哈希 —— 目录名要认得出来是哪一套）。
    /// </summary>
    private static string SafeName(string? raw)
    {
        var name = string.IsNullOrWhiteSpace(raw) ? "spine" : raw.Trim();

        // 路径分隔与非法字符一律换成下划线：素材名来自图集正文与 prefab 名，不能假设它干净
        // （含 <c>/</c> 或 <c>:</c> 会直接写出目录之外，这是必须挡住的一条）。
        var chars = name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        name = new string(chars).Trim(' ', '.');

        return string.IsNullOrWhiteSpace(name) ? "spine" : name;
    }

    /// <summary>名字兜底（取不到素材时也要有名字可显示）：容器路径的文件名去 <c>.prefab</c>。</summary>
    private static string DisplayNameOf(string refKey)
    {
        var name = SpinePathRules.FileNameOf(refKey ?? string.Empty);
        return name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? name[..^".prefab".Length] : name;
    }

    private static string Describe(SpineExportOverwrite overwrite) => overwrite switch
    {
        SpineExportOverwrite.Overwrite => "覆盖同名文件",
        _ => "跳过已存在的文件",
    };

    private static SpineExportResult Failed(
        string outputDirectory, IReadOnlyList<SpineExportItemResult> items, string overwrite, string info)
        => new(false, outputDirectory, items, overwrite, info, false);

    private static string Summarize(IReadOnlyList<SpineExportItemResult> items, bool cancelled)
    {
        var ok = items.Count(i => i.Ok);
        var files = items.Where(i => i.Ok).Sum(i => i.Files.Count);
        var bytes = items.Where(i => i.Ok).Sum(i => i.Files.Sum(f => f.Bytes));
        var skipped = items.Sum(i => i.SkippedFiles.Count);
        var head = cancelled ? "导出已取消" : "导出完成";
        var detail = $"成功 {ok}/{items.Count} 条 · 写出 {files} 个文件 · 合计 {bytes:N0} 字节";
        if (skipped > 0) detail += $" · 跳过 {skipped} 个已存在的文件";
        if (ok < items.Count) detail += $" · 失败 {items.Count - ok} 条（原因见逐条结果）";
        return $"{head}：{detail}";
    }
}

/// <summary>导出进度（走既有 <c>progress</c> 事件；每条开始前报一次）。</summary>
/// <param name="Current">当前是第几条（1 起）。</param>
/// <param name="Total">总条数。</param>
/// <param name="RefKey">正在处理的容器路径。</param>
/// <param name="Message">中文说明。</param>
public sealed record SpineExportProgress(int Current, int Total, string RefKey, string Message);
