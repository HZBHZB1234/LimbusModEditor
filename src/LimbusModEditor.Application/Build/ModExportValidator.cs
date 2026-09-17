using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;
using SixLabors.ImageSharp;

namespace LimbusModEditor.Application.Build;

/// <summary>写前校验结论的分级：error = 写进游戏会坏；warning = 可疑但能写；info = 判不了/未校验。</summary>
public enum ModExportCheckLevel
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// 一条写前校验结论。<paramref name="Target"/> 是被校验的对象（资源逻辑路径或文件名），
/// <paramref name="Message"/> 是中文说明（必须给出判定依据，不写「可能有问题」这类空话）。
/// </summary>
public sealed record ModExportCheck(ModExportCheckLevel Level, string Target, string Message)
{
    /// <summary>给前端用的分级文本（error / warning / info）。</summary>
    public string LevelText => Level switch
    {
        ModExportCheckLevel.Error => "error",
        ModExportCheckLevel.Warning => "warning",
        _ => "info",
    };

    /// <summary>报告里的一行中文。</summary>
    public string Describe() => Level switch
    {
        ModExportCheckLevel.Error => $"[错误] {Target}：{Message}",
        ModExportCheckLevel.Warning => $"[警告] {Target}：{Message}",
        _ => $"[说明] {Target}：{Message}",
    };
}

/// <summary>
/// 写前校验汇总（dry-run 产物，只读不改任何文件）。
/// </summary>
/// <param name="ChangedCount">项目里有编辑标记的资源条数。</param>
/// <param name="CheckedFileCount">真正拿到替换文件并做了检查的条数。</param>
/// <param name="Checks">分级结论（只含能确定判定的结论）。</param>
public sealed record ModExportValidation(
    int ChangedCount,
    int CheckedFileCount,
    IReadOnlyList<ModExportCheck> Checks)
{
    public int ErrorCount => Checks.Count(x => x.Level == ModExportCheckLevel.Error);

    public int WarningCount => Checks.Count(x => x.Level == ModExportCheckLevel.Warning);

    /// <summary>有 error 就是「不该照现在这样写」，前端应先把这一条摆出来。</summary>
    public bool HasBlockingError => ErrorCount > 0;

    /// <summary>给用户看的一行汇总（无结论时也说清楚「没校验到什么」）。</summary>
    public string Info => ChangedCount == 0
        ? "没有改动，无需校验"
        : $"改动 {ChangedCount} 条，校验文件 {CheckedFileCount} 个：错误 {ErrorCount} 条，警告 {WarningCount} 条" +
          (HasBlockingError ? "（有错误，导出前应先处理）" : string.Empty);
}

/// <summary>
/// 导出前的<b>写前校验</b>（只读、无副作用）：对每个「待替换 / 新增的文件」做能确定判定的检查，
/// 产出分级结论 + 中文理由。
///
/// <para>检查维度：</para>
/// <list type="number">
/// <item>目标文件是否存在（元数据记了 replacementPath 但文件没了 → error）；</item>
/// <item>空文件 / 截断文件（0 字节 → error；容器头声明长度与实际不符 → error）；</item>
/// <item>能否解析（图片走 ImageSharp 真实解码；WAV 校验 RIFF/data；JSON 走解析；FSB 校验魔数 → 读不回来 = error）；</item>
/// <item>类型 / 大小量级与原资源是否一致（与原类型或原大小差一个量级 → warning，附具体数字）；</item>
/// <item>目标容器是否存在（音频改动要有原版 bank 副本，不在磁盘上 → error）。</item>
/// </list>
///
/// <para><b>底线</b>：本轮没有解析器的格式一律记 info「未校验」，<b>绝不伪报通过</b>。</para>
/// </summary>
public sealed class ModExportValidator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>能识别的文件形态（用于「类型是否与原资源一致」的比对）。</summary>
    private enum FileKind
    {
        Image,
        Audio,
        Json,
        Text,
        Bank,
        Unknown,
    }

    public ModExportValidation Validate(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var scope = Log.Scope("导出前写前校验");
        var checks = new List<ModExportCheck>();
        var changed = project.Assets.Where(AssetEditService.HasEdits).ToArray();
        Log.Info("写前校验开始：改动资源 {0} 条（项目共 {1} 条）", changed.Length, project.Assets.Count);
        var checkedFiles = 0;

        foreach (var asset in changed)
        {
            var target = string.IsNullOrWhiteSpace(asset.LogicalPath) ? asset.AssetId.ToString() : asset.LogicalPath;

            // 音频：目标 bank 副本必须在磁盘上（BankEdit 的同一口径），否则导出骨架不存在。
            if (asset.Type == AssetType.Audio && asset.Metadata.ContainsKey("bankSource"))
            {
                var skeleton = asset.SourcePath;
                if (string.IsNullOrWhiteSpace(skeleton) || !File.Exists(skeleton))
                    checks.Add(new ModExportCheck(ModExportCheckLevel.Error, target,
                        $"目标 bank 副本不在磁盘上（{DescribePath(skeleton)}），导出缺少骨架，加载器不会认这个包"));
            }

            // 没有替换文件：字段 / 元数据类编辑本来就没有文件，如实说明（不算通过，也不算错误）。
            if (!asset.Metadata.TryGetValue("replacementPath", out var replacement) ||
                string.IsNullOrWhiteSpace(replacement))
            {
                checks.Add(new ModExportCheck(ModExportCheckLevel.Info, target,
                    "该改动是字段 / 元数据编辑，没有替换文件，未做文件级校验"));
                continue;
            }

            if (!File.Exists(replacement))
            {
                checks.Add(new ModExportCheck(ModExportCheckLevel.Error, target,
                    $"替换文件不在磁盘上（{DescribePath(replacement)}），导出时会写出一个空槽位"));
                continue;
            }

            checkedFiles++;
            var size = new FileInfo(replacement).Length;
            if (size == 0)
            {
                checks.Add(new ModExportCheck(ModExportCheckLevel.Error, target,
                    $"替换文件是 0 字节（{DescribePath(replacement)}），写进模组会让加载器读到空资源"));
                continue;
            }

            var kind = DetectKind(replacement);
            checks.AddRange(ParseChecks(kind, replacement, target, size));
            checks.AddRange(CompareWithOriginal(asset, kind, size, target));

            if (kind == FileKind.Unknown)
                checks.Add(new ModExportCheck(ModExportCheckLevel.Info, target,
                    $"未校验：本轮没有 {DescribeExtension(replacement)} 这类文件的解析器（不伪报通过）"));
        }

        var result = new ModExportValidation(changed.Length, checkedFiles, checks);
        Log.Info("写前校验完成：{0}（错误 {1}，警告 {2}）", result.Info, result.ErrorCount, result.WarningCount);
        foreach (var check in checks.Where(x => x.Level != ModExportCheckLevel.Info))
            Log.Debug("写前校验结论 {0}", check.Describe());
        return result;
    }

    /// <summary>按魔数（其次扩展名）判定文件形态：魔数优先，避免只看扩展名被骗。</summary>
    private static FileKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var head = ReadHead(path, 16);
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            return FileKind.Image;
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return FileKind.Image;
        if (head.Length >= 4 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F')
            return FileKind.Audio;
        if (head.Length >= 4 && head[0] == (byte)'F' && head[1] == (byte)'S' && head[2] == (byte)'B' && head[3] == (byte)'5')
            return FileKind.Bank;
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".webp" => FileKind.Image,
            ".wav" => FileKind.Audio,
            ".bank" or ".fsb" => FileKind.Bank,
            ".json" => FileKind.Json,
            ".txt" or ".csv" or ".xml" or ".yaml" => FileKind.Text,
            _ => FileKind.Unknown,
        };
    }

    /// <summary>「能解析吗」：对能判定的格式做一次真实解码 / 结构校验，读不回来就是 error。</summary>
    private static IEnumerable<ModExportCheck> ParseChecks(FileKind kind, string path, string target, long size)
    {
        switch (kind)
        {
            case FileKind.Image:
            {
                yield return ImageCheck(path, target, size);
                break;
            }
            case FileKind.Audio:
            {
                foreach (var check in WavChecks(path, target, size)) yield return check;
                break;
            }
            case FileKind.Bank:
            {
                if (size < 64)
                    yield return new ModExportCheck(ModExportCheckLevel.Error, target,
                        $"FSB 文件不完整：{size} 字节（连头都放不下）");
                break;
            }
            case FileKind.Json:
            {
                yield return JsonCheck(path, target);
                break;
            }
        }
    }

    /// <summary>图片：用 ImageSharp 真解一次（不是只看魔数），解不开就是 error。</summary>
    private static ModExportCheck ImageCheck(string path, string target, long size)
    {
        try
        {
            using var image = Image.Load(path);
            return new ModExportCheck(ModExportCheckLevel.Info, target,
                $"已解码：{image.Width} × {image.Height}（{DescribeExtension(path)}，{size} 字节）");
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or IOException)
        {
            return new ModExportCheck(ModExportCheckLevel.Error, target,
                $"无法解析：图片解码失败（{ex.GetType().Name}），写进模组会让加载器读到坏图");
        }
    }

    /// <summary>JSON：真解析一次，语法错就是 error。</summary>
    private static ModExportCheck JsonCheck(string path, string target)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            _ = document.RootElement;
            return new ModExportCheck(ModExportCheckLevel.Info, target, "已解析：JSON 语法正确");
        }
        catch (JsonException ex)
        {
            return new ModExportCheck(ModExportCheckLevel.Error, target,
                $"无法解析：JSON 语法错误（{ex.Message}）");
        }
    }

    /// <summary>WAV 结构校验：RIFF/WAVE 标记 + 头声明长度与实际长度是否对得上（截断检测）。</summary>
    private static IEnumerable<ModExportCheck> WavChecks(string path, string target, long size)
    {
        var head = ReadHead(path, 64);
        if (head.Length < 12 || head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F' ||
            head[8] != 'W' || head[9] != 'A' || head[10] != 'V' || head[11] != 'E')
        {
            yield return new ModExportCheck(ModExportCheckLevel.Error, target,
                "无法解析：不是 RIFF/WAVE 结构的音频文件");
            yield break;
        }
        var declared = BitConverter.ToInt32(head, 4) + 8;
        if (declared > size)
        {
            yield return new ModExportCheck(ModExportCheckLevel.Error, target,
                $"文件被截断：头声明 {declared} 字节，实际只有 {size} 字节");
        }
        else if (size - declared > 4)
        {
            yield return new ModExportCheck(ModExportCheckLevel.Warning, target,
                $"文件比头声明长（声明 {declared} 字节，实际 {size} 字节）：尾部有多余数据，可能被加载器整段读走");
        }
    }

    /// <summary>与原资源比对：类型是否对得上、大小是否差一个量级（拿不到原值的维度不比，如实说没校验）。</summary>
    private static IEnumerable<ModExportCheck> CompareWithOriginal(AssetRecord asset, FileKind kind, long size, string target)
    {
        var expected = ExpectedKind(asset.Type);
        if (expected != FileKind.Unknown && kind != FileKind.Unknown && expected != kind)
            yield return new ModExportCheck(ModExportCheckLevel.Warning, target,
                $"形态与原资源不一致：原资源是 {asset.Type}（期望 {KindText(expected)}），替换文件是 {KindText(kind)}");

        var original = OriginalSize(asset);
        if (original is null)
        {
            yield return new ModExportCheck(ModExportCheckLevel.Info, target, "未校验：拿不到原资源大小，无法比对量级");
            yield break;
        }
        if (original.Value <= 0) yield break;
        var ratio = (double)size / original.Value;
        if (ratio >= 10 || ratio <= 0.1)
            yield return new ModExportCheck(ModExportCheckLevel.Warning, target,
                $"大小与原资源差一个量级：原 {original.Value} 字节 → 替换 {size} 字节（×{ratio:0.##}）");
    }

    /// <summary>原资源大小：优先用替换时记下的 originalSize，其次资源当前大小。</summary>
    private static long? OriginalSize(AssetRecord asset)
    {
        if (asset.Metadata.TryGetValue("originalSize", out var text) &&
            long.TryParse(text, out var parsed) && parsed > 0)
            return parsed;
        return asset.Size > 0 ? asset.Size : null;
    }

    private static FileKind ExpectedKind(AssetType type) => type switch
    {
        AssetType.Texture or AssetType.Sprite => FileKind.Image,
        AssetType.Audio => FileKind.Audio,
        AssetType.Json or AssetType.MonoBehaviour or AssetType.MonoScript or AssetType.ScriptableObject => FileKind.Json,
        AssetType.Text => FileKind.Text,
        _ => FileKind.Unknown,
    };

    private static string KindText(FileKind kind) => kind switch
    {
        FileKind.Image => "图片",
        FileKind.Audio => "音频",
        FileKind.Json => "JSON",
        FileKind.Text => "文本",
        FileKind.Bank => "FSB bank",
        _ => "未知",
    };

    private static string DescribeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(ext) ? "无扩展名" : ext.ToLowerInvariant();
    }

    private static string DescribePath(string? path) => string.IsNullOrWhiteSpace(path) ? "<空>" : path;

    private static byte[] ReadHead(string path, int count)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            return read == count ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
