using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using NLog;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetReplacementResult(Guid AssetId, string StoredPath, long Size, string Hash);

/// <summary>批量替换里登记成功的一条：资源、逻辑路径、落到项目内的暂存文件。</summary>
public sealed record BatchReplacementItem(Guid AssetId, string LogicalPath, string ReplacementPath);

/// <summary>Outcome of a batch replacement registration (P3.3 工作流).</summary>
public sealed record BatchReplacementReport(
    int Matched,
    IReadOnlyList<string> FilesWithoutAsset,
    IReadOnlyList<string> AssetsWithoutFile,
    int SkippedAlreadyReplaced,
    IReadOnlyList<BatchReplacementItem> Items,
    int SkippedByPattern = 0)
{
    public string Describe() =>
        $"匹配并登记 {Matched} 个替换；{FilesWithoutAsset.Count} 个文件没有对应的资源；" +
        $"{AssetsWithoutFile.Count} 个资源没有提供文件；跳过已替换 {SkippedAlreadyReplaced} 个" +
        (SkippedByPattern > 0 ? $"；文件名不匹配被跳过 {SkippedByPattern} 个" : string.Empty) + "。";
}

/// <summary>
/// Stores user replacement files inside the project workspace and records a
/// reversible edit operation. Format-specific writers consume the stored file
/// later, so the UI does not need to know whether the target is Carra or Bank.
/// </summary>
public sealed class AssetEditService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>资源是否存在「可用的替换文件」：元数据记了 replacementPath 且文件还在。
    /// 替换文件是用户自己的松散文件，优先级高于资源自身来源。</summary>
    public static bool TryGetReplacementFile(AssetRecord asset, out string path)
    {
        ArgumentNullException.ThrowIfNull(asset);
        path = string.Empty;
        if (!asset.Metadata.TryGetValue("replacementPath", out var candidate) || string.IsNullOrWhiteSpace(candidate))
            return false;
        if (!File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }

    /// <summary>批量登记替换：matches files in a folder to project assets by
    /// file name (case-insensitive) and registers every match through the same
    /// reversible pipeline as single replacement.</summary>
    /// <param name="namePattern">可选的文件名通配符（<c>*</c> / <c>?</c>，大小写不敏感）。
    /// 给了就只处理命中的文件，其余计入 <see cref="BatchReplacementReport.SkippedByPattern"/>
    /// ——它们本来就不在本次范围内，不算「没匹配上资源」。</param>
    public async Task<BatchReplacementReport> BatchReplaceFromDirectoryAsync(
        ModProject project,
        string folder,
        string projectDirectory,
        bool onlyUnreplaced = true,
        string? namePattern = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"替换文件夹不存在：{folder}");
        var pattern = BuildNamePatternRegex(namePattern);
        var byFileName = project.Assets
            .GroupBy(x => Path.GetFileName(x.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        var skipped = 0;
        var skippedByPattern = 0;
        var filesWithoutAsset = new List<string>();
        var items = new List<BatchReplacementItem>();
        var matchedAssets = new HashSet<Guid>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith(".", StringComparison.Ordinal)) continue; // skip sidecar/temp files
            if (pattern is not null && !pattern.IsMatch(fileName)) { skippedByPattern++; continue; }
            if (!byFileName.TryGetValue(fileName, out var asset)) { filesWithoutAsset.Add(fileName); continue; }
            if (onlyUnreplaced && asset.Metadata.ContainsKey("replacementPath")) { skipped++; continue; }
            var replacement = await ReplaceFromFileAsync(project, asset, file, projectDirectory, cancellationToken);
            matchedAssets.Add(asset.AssetId);
            items.Add(new BatchReplacementItem(asset.AssetId, asset.LogicalPath, replacement.StoredPath));
            matched++;
        }
        var assetsWithoutFile = project.Assets
            .Where(x => !matchedAssets.Contains(x.AssetId) &&
                        (!onlyUnreplaced || !x.Metadata.ContainsKey("replacementPath")))
            .Select(x => x.LogicalPath)
            .ToList();
        return new BatchReplacementReport(matched, filesWithoutAsset, assetsWithoutFile, skipped, items, skippedByPattern);
    }

    /// <summary>把用户给的 <c>*</c> / <c>?</c> 通配串编译成正则；空串/null 表示不过滤（返回 null）。</summary>
    private static Regex? BuildNamePatternRegex(string? namePattern)
    {
        if (string.IsNullOrWhiteSpace(namePattern)) return null;
        var escaped = Regex.Escape(namePattern.Trim());
        return new Regex("^" + escaped.Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>按 AssetId 在项目里找资源。**这是编辑热路径上的一处 O(N) 陷阱**：
    /// 百万级索引（真实规模 1,275,623 条）下每点一次「替换 / 编辑文本 / 撤销」
    /// 都要线性扫一遍全表。调用方几乎都已经持有 <see cref="AssetRecord"/>，
    /// 应当优先走各方法的实体重载；本方法只为「手上只有 Guid」的旧调用点
    /// （与既有测试的 <see cref="KeyNotFoundException"/> 契约）保留。</summary>
    private static AssetRecord FindAsset(ModProject project, Guid assetId)
        => project.Assets.FirstOrDefault(x => x.AssetId == assetId)
           ?? throw new KeyNotFoundException($"未找到资源: {assetId}");

    public Task<AssetReplacementResult> ReplaceFromBytesAsync(
        ModProject project, Guid assetId, ReadOnlyMemory<byte> data,
        string suggestedExtension, string projectDirectory,
        CancellationToken cancellationToken = default)
        => ReplaceFromBytesAsync(project, FindAsset(project, assetId), data, suggestedExtension,
            projectDirectory, cancellationToken);

    /// <summary>实体重载：调用方已持有资源对象，不需要在全项目里线性查找。</summary>
    public Task<AssetReplacementResult> ReplaceFromBytesAsync(
        ModProject project, AssetRecord asset, ReadOnlyMemory<byte> data,
        string suggestedExtension, string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(data);
        var extension = string.IsNullOrWhiteSpace(suggestedExtension) ? ".bin" : suggestedExtension;
        if (!extension.StartsWith('.')) extension = "." + extension;
        var temporaryRoot = Path.Combine(Path.GetFullPath(projectDirectory), "edits", "assets");
        Directory.CreateDirectory(temporaryRoot);
        var source = Path.Combine(temporaryRoot, $"{asset.AssetId:N}{extension}");
        return ReplaceFromBytesCoreAsync(project, asset, data, source, cancellationToken);
    }

    public Task<byte[]> ReadCurrentBytesAsync(
        ModProject project, Guid assetId, CancellationToken cancellationToken = default)
        => ReadCurrentBytesAsync(project, FindAsset(project, assetId), cancellationToken);

    /// <summary>实体重载：见 <see cref="FindAsset"/> 的 O(N) 说明。</summary>
    public async Task<byte[]> ReadCurrentBytesAsync(
        ModProject project, AssetRecord asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        if (TryGetReplacementFile(asset, out var replacement))
        {
            Log.Debug("读取资源当前内容（替换文件）：资源={0}，替换文件={1}（{2} 字节）",
                asset.LogicalPath ?? "-", replacement, new FileInfo(replacement).Length);
            return await File.ReadAllBytesAsync(replacement, cancellationToken);
        }
        // 关键防线（2026-09 卡死事故）：bundle 内对象的 SourcePath 是**整个 AssetBundle
        // 容器**（<外层键>/<内层键>/__data），不是这个对象自己的正文。把容器字节当正文
        // 返回，下游就会把几百 KB~MB 级二进制塞进 WPF 文本框（实测 2.26 MB → 排版
        // 26.5 秒 → 界面被 Windows 判「停止交互」后强杀）。这里必须拦住，不允许
        // 「退化成读容器」这种兜底。
        if (PreviewRead.IsBundleAsset(asset))
        {
            Log.Warn("拒绝按文件读取容器字节：资源「{0}」的正文在 AssetBundle 容器里（SourcePath「{1}」，pathId {2}）——容器 ≠ 资源正文。",
                asset.LogicalPath ?? "-", asset.SourcePath ?? "-", asset.UnityPathId?.ToString() ?? "-");
            throw new NotSupportedException(
                "该资源的内容在 AssetBundle 容器里，不能按文件直接读取："
                + "请用预览查看内容，或用「替换选中资源…」登记自己的文件。");
        }
        var path = asset.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("资源没有可读取的本地文件。", path);
        Log.Debug("读取资源当前内容（松散文件）：资源={0}，文件={1}（{2} 字节）",
            asset.LogicalPath ?? "-", path, new FileInfo(path).Length);
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public Task<AssetReplacementResult> ReplaceFromFileAsync(
        ModProject project,
        Guid assetId,
        string replacementPath,
        string projectDirectory,
        CancellationToken cancellationToken = default)
        => ReplaceFromFileAsync(project, FindAsset(project, assetId), replacementPath, projectDirectory,
            cancellationToken);

    /// <summary>实体重载：见 <see cref="FindAsset"/> 的 O(N) 说明。</summary>
    public async Task<AssetReplacementResult> ReplaceFromFileAsync(
        ModProject project,
        AssetRecord asset,
        string replacementPath,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var source = Path.GetFullPath(replacementPath);
        if (!File.Exists(source)) throw new FileNotFoundException("替换文件不存在。", source);

        var editsRoot = Path.Combine(Path.GetFullPath(projectDirectory), "edits", "assets");
        Directory.CreateDirectory(editsRoot);
        var extension = Path.GetExtension(source);
        var stored = Path.Combine(editsRoot, asset.AssetId.ToString("N") + extension);
        var temporary = stored + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, stored, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var info = new FileInfo(stored);
        await using var hashInput = File.OpenRead(stored);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, cancellationToken));
        asset.ModifiedHash = hash;
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        CaptureOriginalSize(asset);
        asset.Size = info.Length;
        asset.Metadata["replacementPath"] = stored;
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            SourcePath = stored,
            BeforeHash = asset.OriginalHash,
            AfterHash = hash
        });
        return new(asset.AssetId, stored, info.Length, hash);
    }

    private async Task<AssetReplacementResult> ReplaceFromBytesCoreAsync(
        ModProject project, AssetRecord asset, ReadOnlyMemory<byte> data,
        string source, CancellationToken cancellationToken)
    {
        var temporary = source + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
                await output.WriteAsync(data, cancellationToken);
            File.Move(temporary, source, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return await RecordReplacementAsync(project, asset, source, cancellationToken);
    }

    private async Task<AssetReplacementResult> RecordReplacementAsync(
        ModProject project, AssetRecord asset, string stored, CancellationToken cancellationToken)
    {
        var info = new FileInfo(stored);
        await using var hashInput = File.OpenRead(stored);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, cancellationToken));
        asset.ModifiedHash = hash;
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        CaptureOriginalSize(asset);
        asset.Size = info.Length;
        asset.Metadata["replacementPath"] = stored;
        project.Edits.Add(new EditOperation { Kind = EditOperationKind.ReplaceAsset, AssetId = asset.AssetId, TargetPath = asset.LogicalPath, SourcePath = stored, BeforeHash = asset.OriginalHash, AfterHash = hash });
        return new(asset.AssetId, stored, info.Length, hash);
    }

    /// <summary>首次替换时记下原始大小，撤销修改时还原列表显示。</summary>
    private static void CaptureOriginalSize(AssetRecord asset)
    {
        if (!asset.Metadata.ContainsKey("originalSize"))
            asset.Metadata["originalSize"] = asset.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>资源上是否存在任何可导出的编辑（替换文件 / Unity 字段 /
    /// Sprite 元数据）。这是导出与「已修改」判定的唯一口径。</summary>
    public static bool HasEdits(AssetRecord asset) =>
        asset.Metadata.ContainsKey("replacementPath") ||
        asset.Metadata.ContainsKey("unityFieldEdits") ||
        asset.Metadata.ContainsKey("spriteMetadata");

    /// <summary>撤销一个资源上的全部编辑：清除替换文件 / Unity 字段 /
    /// Sprite 元数据三类编辑标记并把资源还原为 Unchanged。替换的暂存文件
    /// （位于项目 edits/assets 下）会被删除；替换前的显示大小由
    /// originalSize 还原。资源本身没有编辑时返回 false。</summary>
    public bool ClearEdits(ModProject project, Guid assetId, string? projectDirectory = null)
        => ClearEdits(project, FindAsset(project, assetId), projectDirectory);

    /// <summary>实体重载：见 <see cref="FindAsset"/> 的 O(N) 说明。</summary>
    public bool ClearEdits(ModProject project, AssetRecord asset, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        if (!HasEdits(asset)) return false;

        if (asset.Metadata.TryGetValue("replacementPath", out var stored) && !string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                var fullStored = Path.GetFullPath(stored);
                var editsRoot = string.IsNullOrWhiteSpace(projectDirectory)
                    ? null
                    : Path.GetFullPath(Path.Combine(projectDirectory, "edits", "assets"))
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // 只删项目 edits/assets 内的暂存文件；用户手选的外部文件不动。
                if (editsRoot is null || fullStored.StartsWith(editsRoot, StringComparison.OrdinalIgnoreCase))
                    if (File.Exists(fullStored)) File.Delete(fullStored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉暂存文件不阻断撤销：编辑标记已清除，导出不会再带上它。
            }
        }

        asset.Metadata.Remove("replacementPath");
        asset.Metadata.Remove("unityFieldEdits");
        asset.Metadata.Remove("spriteMetadata");
        if (asset.Metadata.TryGetValue("originalSize", out var original) &&
            long.TryParse(original, out var size))
            asset.Size = size;
        asset.Metadata.Remove("originalSize");
        asset.ModifiedHash = null;
        asset.EditState = AssetEditState.Unchanged;
        return true;
    }
}
