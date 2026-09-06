using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Unity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// T1 写回链路的真实数据端到端验证：真实缓存 bundle 中的流纹理（m_StreamData）
/// → PNG 替换写回（内联像素 + 清流）→ 重打包校验 → 以真实 Carra2 结构承载
/// （键 = 缓存外层/内层/pathId.类型表索引，逐条目 XZ）→ 重新导入核对。
/// 全程只在 artifacts 副本上写，绝不触碰 Unity 缓存原件。
/// 把产物安装进真实模组目录需要显式设置 LME_REALDATA_INSTALL=1（游戏内
/// 最终确认由用户启动游戏完成）。
/// </summary>
public class RealWriteBackTests
{
    /// <summary>写回已实现且真实样本实测覆盖的纹理格式：RGB24/RGBA32/DXT1/DXT5。</summary>
    private static readonly int[] WritableFormats = [3, 4, 10, 12];

    private record Candidate(
        string BundlePath, string Outer, string Inner, string Container, long PathId,
        int Width, int Height, int Format, UnityTextureObject Texture);

    [Fact]
    public async Task Real_stream_texture_replaces_back_through_the_full_carra2_pipeline()
    {
        var root = RealSamples.UnityCacheRoot;
        if (root is null) return;

        var work = Path.Combine(FindRepoRoot(), "artifacts", "realdata-verify");
        Directory.CreateDirectory(work);

        var candidate = FindStreamTextureCandidate(root);
        Assert.True(candidate is not null,
            "扫描范围内没有找到可写回的流纹理样本（m_StreamData + 受支持格式）");

        // ── 0. 副本工作台：缓存原件只读 ──────────────────────────────────
        var sourceCopy = Path.Combine(work, "source.bundle");
        var modifiedBundle = Path.Combine(work, "modified.bundle");
        var pngPath = Path.Combine(work, "replacement.png");
        var carraPath = Path.Combine(work, "LME-写回验证.carra2");
        foreach (var stale in new[] { sourceCopy, modifiedBundle, pngPath, carraPath })
            if (File.Exists(stale)) File.Delete(stale);
        File.Copy(candidate.BundlePath, sourceCopy, overwrite: true);
        var originalSha = RealSamples.Sha256(sourceCopy);

        var codec = new UnityTextureCodec();
        var originalPixels = candidate.Texture.PixelData;
        var originalImage = codec.ToImage(new UnityTextureInfo(
            candidate.Width, candidate.Height, (UnityTexturePixelFormat)candidate.Format, originalPixels));
        var replacementColor = PickReplacementColor(originalImage);

        // ── 1. 真实纹理替换写回（内联像素 + 清 m_StreamData）──────────────
        var (pngBytes, replacementImage) = BuildReplacementPng(candidate.Width, candidate.Height, replacementColor);
        await File.WriteAllBytesAsync(pngPath, pngBytes);
        using (var backend = new AssetsToolsBackend())
            backend.ReplaceBundleTextureFromPng(sourceCopy, candidate.Container, candidate.PathId, pngBytes, modifiedBundle);
        var modifiedSha = RealSamples.Sha256(modifiedBundle);
        Assert.NotEqual(originalSha, modifiedSha);

        // ── 2. 重打包产物校验 ────────────────────────────────────────────
        IReadOnlyList<UnityAssetDescriptor> originalDescriptors;
        using (var backend = new AssetsToolsBackend())
            originalDescriptors = backend.InspectBundle(sourceCopy);
        var originalStructure = originalDescriptors
            .Select(d => $"{d.ContainerPath}|{d.PathId}|{d.TypeId}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        UnityTextureObject modifiedTexture;
        IReadOnlyList<UnityFieldNode> modifiedFields;
        using (var backend = new AssetsToolsBackend())
        {
            var modifiedDescriptors = backend.InspectBundle(modifiedBundle);
            var modifiedStructure = modifiedDescriptors
                .Select(d => $"{d.ContainerPath}|{d.PathId}|{d.TypeId}")
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(originalStructure, modifiedStructure);

            modifiedTexture = backend.ReadTexture(modifiedBundle, candidate.PathId)
                ?? throw new Xunit.Sdk.XunitException("写回后的 bundle 中读取不到该纹理");
            Assert.Equal(candidate.Width, modifiedTexture.Width);
            Assert.Equal(candidate.Height, modifiedTexture.Height);
            Assert.Equal(candidate.Format, modifiedTexture.TextureFormat);

            modifiedFields = backend.ReadBundleObjectFields(modifiedBundle, candidate.Container, candidate.PathId);
        }
        var textureName = FindChild(modifiedFields[0], "m_Name")?.Value ?? "";
        var verify = new AssetsToolsBackend().VerifyBundleReferences(sourceCopy, modifiedBundle);
        Assert.True(verify.Ok, "重打包后引用完整性检查失败: " +
            string.Join("; ", verify.Changes.Where(c => c.IsRegression).Select(c => $"{c.SourcePathId} {c.FieldPath}")));

        // 流已清空：m_StreamData.path/size 归零，像素内联且解码结果与替换图一致
        var streamData = FindChild(modifiedFields[0], "m_StreamData");
        Assert.NotNull(streamData);
        Assert.Equal(string.Empty, FindChild(streamData!, "path")?.Value ?? "missing");
        Assert.Equal("0", FindChild(streamData!, "size")?.Value ?? "missing");
        // 真实 Texture2D 的像素字段随 Unity 版本可为 m_TextureData/m_ImageData/"image data"
        var inlineData = FindChild(modifiedFields[0], "m_TextureData")
            ?? FindChild(modifiedFields[0], "m_ImageData")
            ?? FindChild(modifiedFields[0], "image data");
        Assert.NotNull(inlineData);
        Assert.True(inlineData!.ByteArrayLength > 0, "写回后像素数组为空");

        var modifiedImage = codec.ToImage(new UnityTextureInfo(
            modifiedTexture.Width, modifiedTexture.Height,
            (UnityTexturePixelFormat)modifiedTexture.TextureFormat, modifiedTexture.PixelData));
        var lossy = candidate.Format is 10 or 12; // DXT 块压缩有损
        Assert.True(MeanAbsDiff(modifiedImage, replacementImage) <= (lossy ? 40 : 8),
            $"写回后解码图与替换图不一致（平均差 {MeanAbsDiff(modifiedImage, replacementImage):F1}）");
        Assert.True(MeanAbsDiff(modifiedImage, originalImage) > 25,
            "写回后图像与原图几乎相同，无法在游戏中验证可见变化");

        // ── 3. 真实 Carra2 承载：键=外层/内层/pathId.类型表索引，XZ 逐条目 ──
        UnityBundleSerializedObject modifiedRaw;
        UnityBundleSerializedObject vanillaRaw;
        using (var backend = new AssetsToolsBackend())
            modifiedRaw = backend.ReadBundleSerializedObject(modifiedBundle, candidate.Container, candidate.PathId);
        using (var backend = new AssetsToolsBackend())
            vanillaRaw = backend.ReadBundleSerializedObject(sourceCopy, candidate.Container, candidate.PathId);
        // 真实加载器（LCTA launcher/patch.py patch_bundle_asset）拿键里的类型表
        // 索引与 vanilla 对象的 obj.type_id 比对，不等即静默跳过
        Assert.Equal(vanillaRaw.TypeTableIndex, modifiedRaw.TypeTableIndex);
        Assert.Equal(28, modifiedRaw.TypeTableClassId);
        Assert.InRange(modifiedRaw.TypeTableIndex, 0, modifiedRaw.TypeTableCount - 1);
        Assert.True(modifiedRaw.Data.Length > 0);

        var key = new CarraObjectKey(candidate.Outer, candidate.Inner, candidate.PathId, modifiedRaw.TypeTableIndex);
        Assert.Matches(@"^[0-9a-f]{32}/[0-9a-f]{32}/-?\d+\.\d+$", key.LogicalPath);
        var package = new CarraPackage();
        package.Entries.Add(CarraEntry.CreateNew(key, modifiedRaw.Data));
        package.UnknownFiles.Add(("carra.json",
            Encoding.UTF8.GetBytes($"{{\"format\": \"carra2\", \"objects\": {package.Entries.Count}}}")));

        var handler = new CarraFormatHandler();
        var validation = await handler.ValidateAsync(new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package });
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        await using (var output = File.Create(carraPath))
            await handler.ExportAsync(new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package },
                output, new ExportContext(ModFormatKind.Carra2, Codec: new JovelerXzCodec()));
        var carraSha = RealSamples.Sha256(carraPath);

        // 导出物必须被格式探针识别，且逐字节回读等于写回后的原始对象数据
        await using (var input = File.OpenRead(carraPath))
        {
            var probe = await handler.ProbeAsync(input, Path.GetFileName(carraPath));
            Assert.True(probe.IsMatch, $"导出的 carra2 未通过格式探针: {probe.Description}");
            input.Position = 0;
            var reimported = await handler.ImportAsync(input, new ImportContext());
            var reimportedPayload = Assert.IsType<CarraPackage>(reimported.Payload);
            var entry = Assert.Single(reimportedPayload.Entries);
            var asset = Assert.Single(reimported.Project.Assets);
            Assert.Equal(key.LogicalPath, entry.Key.LogicalPath);
            Assert.Equal(key.LogicalPath, asset.LogicalPath);
            Assert.Equal(modifiedRaw.Data, entry.ReadData());
            Assert.True(entry.IsXzCompressed, "导出的 Carra 条目不是 XZ 流（真实加载器按 FORMAT_XZ 解压）");
            Assert.Single(reimportedPayload.UnknownFiles);
        }

        // 缓存对齐：键的外层/内层必须仍指向真实缓存中的 bundle
        Assert.True(File.Exists(Path.Combine(root, candidate.Outer, candidate.Inner, "__data")),
            "键与缓存目录失配（加载器将无法定位该 bundle）");

        // ── 4. 报告产物 + （可选）安装进真实模组目录 ─────────────────────
        var installTarget = MaybeInstall(carraPath, candidate.Inner);
        var report = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sourceBundle = candidate.BundlePath,
            cacheOuterKey = candidate.Outer,
            cacheInnerKey = candidate.Inner,
            containerPath = candidate.Container,
            pathId = candidate.PathId,
            typeTableIndex = modifiedRaw.TypeTableIndex,
            typeTableClassId = modifiedRaw.TypeTableClassId,
            textureWidth = candidate.Width,
            textureHeight = candidate.Height,
            textureFormat = candidate.Format,
            textureFormatName = ((UnityTexturePixelFormat)candidate.Format).ToString(),
            textureName = textureName,
            originalBundleSha256 = originalSha,
            modifiedBundleSha256 = modifiedSha,
            verifyBundleReferencesOk = verify.Ok,
            replacementPng = pngPath,
            replacementColor = $"#{replacementColor.R:X2}{replacementColor.G:X2}{replacementColor.B:X2}",
            carra2 = carraPath,
            carra2Sha256 = carraSha,
            carra2Key = key.LogicalPath,
            installedTo = installTarget,
            installRequested = Environment.GetEnvironmentVariable("LME_REALDATA_INSTALL") == "1"
        };
        var reportPath = Path.Combine(work, "verify-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        Console.WriteLine($"T1 写回验证: {candidate.Outer}/{candidate.Inner} Path {candidate.PathId} " +
            $"typeIdx {modifiedRaw.TypeTableIndex} {candidate.Width}x{candidate.Height} " +
            $"{((UnityTexturePixelFormat)candidate.Format)}");
        Console.WriteLine($"carra2: {carraPath} (sha256 {carraSha[..12]}…)");
        Console.WriteLine($"安装状态: {(installTarget is null ? "未安装（设 LME_REALDATA_INSTALL=1 以安装）" : installTarget)}");
    }

    /// <summary>扫描缓存中的 bundle，挑选「m_StreamData 流纹理 + 受支持格式 +
    /// 尺寸适中」的最佳写回样本。全部只读。</summary>
    private static Candidate? FindStreamTextureCandidate(string root)
    {
        Candidate? best = null;
        var scanned = 0;
        foreach (var bundle in RealSamples.BundleDataFiles(limit: 250))
        {
            var (outer, inner) = SplitCachePath(bundle);
            if (outer is null || inner is null) continue;
            scanned++;
            IReadOnlyList<UnityAssetDescriptor> descriptors;
            try
            {
                using var backend = new AssetsToolsBackend();
                descriptors = backend.InspectBundle(bundle);
                foreach (var descriptor in descriptors.Where(d => d.TypeId == 28))
                {
                    var fields = backend.ReadBundleObjectFields(bundle, descriptor.ContainerPath, descriptor.PathId);
                    var streamPath = FindChild(FindChild(fields[0], "m_StreamData"), "path")?.Value;
                    if (string.IsNullOrEmpty(streamPath)) continue; // 只要流纹理（T1 场景）
                    if (!int.TryParse(FindChild(fields[0], "m_Width")?.Value, out var width) || width <= 0) continue;
                    if (!int.TryParse(FindChild(fields[0], "m_Height")?.Value, out var height) || height <= 0) continue;
                    if (!int.TryParse(FindChild(fields[0], "m_TextureFormat")?.Value, out var format)) continue;
                    if (!WritableFormats.Contains(format)) continue;
                    if (width * height > 512 * 512) continue;
                    var texture = backend.ReadTexture(bundle, descriptor.PathId);
                    if (texture is null) continue;
                    var candidate = new Candidate(bundle, outer, inner, descriptor.ContainerPath,
                        descriptor.PathId, width, height, format, texture);
                    if (best is null || width * height < best.Width * best.Height)
                        best = candidate;
                    if (width * height <= 128 * 128) return candidate;
                }
            }
            catch (Exception)
            {
                // 个别 bundle 解析失败是真实数据常态，跳过继续扫描
            }
        }
        Console.WriteLine($"流纹理样本扫描: {scanned} 个 bundle，最佳样本 {best?.Width}x{best?.Height}");
        return best;
    }

    private static (string? Outer, string? Inner) SplitCachePath(string dataFile)
    {
        var innerDir = Path.GetDirectoryName(Path.GetFullPath(dataFile));
        var outerDir = Path.GetDirectoryName(innerDir);
        var cacheRoot = Path.GetDirectoryName(outerDir);
        if (innerDir is null || outerDir is null || cacheRoot is null) return (null, null);
        var outer = Path.GetFileName(outerDir);
        var inner = Path.GetFileName(innerDir);
        return (string.IsNullOrEmpty(outer) ? null : outer, string.IsNullOrEmpty(inner) ? null : inner);
    }

    private static string? MaybeInstall(string carraPath, string inner)
    {
        if (Environment.GetEnvironmentVariable("LME_REALDATA_INSTALL") != "1") return null;
        if (!OperatingSystem.IsWindows()) return null;
        var modsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LimbusCompanyMods");
        if (!Directory.Exists(modsRoot)) return null;
        var target = Path.Combine(modsRoot, $"LME-写回验证-{inner[..8]}.carra2");
        File.Copy(carraPath, target, overwrite: true);
        Assert.True(File.Exists(target), "安装（复制到模组目录）失败");
        return target;
    }

    private static (byte[] Png, Image<Rgba32> Image) BuildReplacementPng(int width, int height, Rgba32 color)
    {
        var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image[x, y] = color;
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return (output.ToArray(), image);
    }

    /// <summary>挑一个与原图平均色距离最远的高可见度颜色，保证游戏内肉眼可辨。</summary>
    private static Rgba32 PickReplacementColor(Image<Rgba32> original)
    {
        double r = 0, g = 0, b = 0, n = 0;
        for (var y = 0; y < original.Height; y += Math.Max(1, original.Height / 32))
            for (var x = 0; x < original.Width; x += Math.Max(1, original.Width / 32))
            {
                var p = original[x, y];
                r += p.R; g += p.G; b += p.B; n++;
            }
        var avg = (R: r / n, G: g / n, B: b / n);
        Rgba32[] options = [new(255, 0, 255), new(0, 255, 255), new(255, 255, 0), new(255, 0, 0), new(0, 255, 0), new(255, 255, 255)];
        return options.OrderByDescending(c =>
        {
            var dr = c.R - avg.R; var dg = c.G - avg.G; var db = c.B - avg.B;
            return dr * dr + dg * dg + db * db;
        }).First();
    }

    private static double MeanAbsDiff(Image<Rgba32> left, Image<Rgba32> right)
    {
        Assert.Equal(left.Width, right.Width);
        Assert.Equal(left.Height, right.Height);
        double total = 0;
        var count = 0;
        for (var y = 0; y < left.Height; y++)
            for (var x = 0; x < left.Width; x++)
            {
                var a = left[x, y];
                var b = right[x, y];
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                count += 3;
            }
        return count == 0 ? 0 : total / count;
    }

    private static UnityFieldNode? FindChild(UnityFieldNode? node, string name)
    {
        if (node is null) return null;
        return node.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.Ordinal))
            ?? node.Children.Select(c => FindChild(c, name)).FirstOrDefault(x => x is not null);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LimbusModEditor.slnx")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录（LimbusModEditor.slnx）");
    }
}
