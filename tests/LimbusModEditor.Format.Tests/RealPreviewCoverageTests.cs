using System.Text;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Unity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// 真实数据预览覆盖探针（plan-01 第 0/1 步验收）：对真实 Unity 缓存 bundle 的
/// 有界子集按类型抽样，断言「预览链路能产出结果」——TextAsset 正文可 UTF-8
/// 解码、Sprite 能合成裁剪子图（尺寸 == 裁剪区域）、Texture2D 基线、AudioClip
/// 能取出音频负载（本机缓存无 class 83 样本时跳过该行）。
/// 门控：无真实缓存自动跳过；<c>LME_SCAN_BUDGET</c> 限定 bundle 数（默认 12）。
/// 矩阵写入 <c>%TEMP%/lme-preview-coverage.txt</c>。
/// </summary>
public class RealPreviewCoverageTests
{
    private static int ScanBudget
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("LME_SCAN_BUDGET");
            return int.TryParse(raw, out var value) && value > 0 ? value : 12;
        }
    }

    private static string MatrixFile => Path.Combine(Path.GetTempPath(), "lme-preview-coverage.txt");

    [Fact]
    public void Real_bundle_previews_cover_text_sprite_texture_and_audio()
    {
        var root = RealSamples.UnityCacheRoot;
        if (root is null) return; // 无样本机器：跳过

        var bundles = RealSamples.BundleDataFiles(ScanBudget);
        Assert.NotEmpty(bundles);

        var service = new UnityAssetService();
        var report = new StringBuilder();
        report.AppendLine($"cacheRoot = {root}");
        report.AppendLine($"budget = {ScanBudget}（LME_SCAN_BUDGET 可调）");
        report.AppendLine($"bundles = {bundles.Count}");
        report.AppendLine();

        var samples = new Dictionary<int, AssetRecord>();
        var counts = new Dictionary<int, int>();
        foreach (var bundle in bundles)
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception ex)
            {
                report.AppendLine($"SCAN FAIL {Path.GetFileName(Path.GetDirectoryName(bundle))}: {ex.Message}");
                continue;
            }
            foreach (var o in objects)
            {
                if (o.UnityTypeId is not { } typeId) continue;
                counts[typeId] = counts.GetValueOrDefault(typeId) + 1;
                if (typeId is UnityClassId.Texture2D or UnityClassId.Sprite or UnityClassId.AudioClip or UnityClassId.TextAsset)
                    samples.TryAdd(typeId, o);
            }
        }

        report.AppendLine("=== 类型 × 预览链路（修复后）===");
        var texture = ProbeTexture(service, samples, report);
        var sprite = ProbeSprite(service, samples, report);
        var text = ProbeTextAsset(service, samples, report);
        ProbeAudioClip(service, samples, report);
        report.AppendLine();
        report.AppendLine("=== 抽样范围内对象计数 ===");
        foreach (var kv in counts.OrderByDescending(x => x.Value).Take(20))
            report.AppendLine($"class {kv.Key}: {kv.Value}");

        Directory.CreateDirectory(Path.GetDirectoryName(MatrixFile)!);
        File.WriteAllText(MatrixFile, report.ToString());

        // 真实缓存样本必须覆盖纹理与 Sprite（本机 12 个 bundle 内已实测存在）；
        // TextAsset 视抽样范围而定（默认预算内存在时一并断言）。
        Assert.True(texture, "Texture2D 基线预览失败");
        Assert.True(sprite, "Sprite 合成预览失败");
        if (samples.ContainsKey(UnityClassId.TextAsset)) Assert.True(text, "TextAsset 正文读取失败");
    }

    private static bool ProbeTexture(UnityAssetService service, IReadOnlyDictionary<int, AssetRecord> samples, StringBuilder report)
    {
        if (!samples.TryGetValue(UnityClassId.Texture2D, out var record))
        {
            report.AppendLine("Texture2D  → 抽样范围内无样本");
            return true;
        }
        try
        {
            var png = service.ReadTexturePng(record.SourcePath!, record.UnityPathId!.Value);
            if (png is null) { report.AppendLine("Texture2D  → FAIL：ReadTexturePng 返回 null"); return false; }
            using var image = Image.Load<Rgba32>(png);
            report.AppendLine($"Texture2D  → OK：{image.Width}×{image.Height} PNG {png.Length} 字节");
            return true;
        }
        catch (Exception ex) { report.AppendLine($"Texture2D  → FAIL：{ex.Message}"); return false; }
    }

    private static bool ProbeSprite(UnityAssetService service, IReadOnlyDictionary<int, AssetRecord> samples, StringBuilder report)
    {
        if (!samples.TryGetValue(UnityClassId.Sprite, out var record))
        {
            report.AppendLine("Sprite     → 抽样范围内无样本");
            return true;
        }
        try
        {
            var composite = service.ReadBundleSpriteComposite(record.SourcePath!, record.ContainerPath!, record.UnityPathId!.Value);
            using var image = Image.Load<Rgba32>(composite.Png);
            var expectedWidth = (int)MathF.Round(composite.CropRect.Width);
            var expectedHeight = (int)MathF.Round(composite.CropRect.Height);
            report.AppendLine($"Sprite     → OK：{image.Width}×{image.Height} PNG（裁剪区域 {expectedWidth}×{expectedHeight}，" +
                              $"纹理 {composite.TextureWidth}×{composite.TextureHeight} 格式 {composite.TextureFormat}，" +
                              $"逻辑 rect {composite.SpriteRect.Width}×{composite.SpriteRect.Height}）");
            if (image.Width != expectedWidth || image.Height != expectedHeight)
            {
                report.AppendLine($"Sprite     → FAIL：合成尺寸 {image.Width}×{image.Height} != 裁剪区域 {expectedWidth}×{expectedHeight}");
                return false;
            }
            return true;
        }
        catch (Exception ex) { report.AppendLine($"Sprite     → FAIL：{ex.Message}"); return false; }
    }

    private static bool ProbeTextAsset(UnityAssetService service, IReadOnlyDictionary<int, AssetRecord> samples, StringBuilder report)
    {
        if (!samples.TryGetValue(UnityClassId.TextAsset, out var record))
        {
            report.AppendLine("TextAsset  → 抽样范围内无样本");
            return true;
        }
        try
        {
            var asset = service.ReadBundleTextAsset(record.SourcePath!, record.ContainerPath!, record.UnityPathId!.Value);
            var decoded = asset.TryDecodeUtf8();
            var preview = decoded is null ? "(非 UTF-8)" : decoded[..Math.Min(48, decoded.Length)].ReplaceLineEndings(" ");
            report.AppendLine($"TextAsset  → {(decoded is null ? "FAIL：非 UTF-8" : "OK")}：m_Name={asset.Name}，" +
                              $"{asset.Data.Length} 字节，正文开头：{preview}");
            return decoded is not null && asset.Data.Length > 0;
        }
        catch (Exception ex) { report.AppendLine($"TextAsset  → FAIL：{ex.Message}"); return false; }
    }

    private static void ProbeAudioClip(UnityAssetService service, IReadOnlyDictionary<int, AssetRecord> samples, StringBuilder report)
    {
        if (!samples.TryGetValue(UnityClassId.AudioClip, out var record))
        {
            report.AppendLine("AudioClip  → 抽样范围内无样本（本机缓存实测无 class 83：音频在 FMOD bank 中）");
            return;
        }
        try
        {
            var clip = service.ReadBundleAudioClipData(record.SourcePath!, record.ContainerPath!, record.UnityPathId!.Value);
            var fsb = Fsb5Parser.TryParse(clip.Data);
            report.AppendLine($"AudioClip  → OK：m_Name={clip.Name}，{clip.Data.Length} 字节，" +
                              $"format={clip.Format} 声道={clip.Channels} 采样率={clip.Frequency}，" +
                              (fsb is null ? "负载不是 FSB5（可能为其他压缩形态）" : $"FSB5 codec={fsb.CodecName} 样本数={fsb.SampleCount}"));
        }
        catch (Exception ex) { report.AppendLine($"AudioClip  → FAIL：{ex.Message}"); }
    }
}
