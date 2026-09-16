using System.Buffers.Binary;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Tests;

/// <summary>plan-05 预览提供者管线：注册顺序与兜底、各提供者输出契约（合成样本）、
/// 波形/十六进制工具，以及真实缓存样本上的「类型 → 预览 Kind」矩阵。</summary>
public class AssetPreviewRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-preview-" + Guid.NewGuid().ToString("N"));

    public AssetPreviewRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private sealed class StubProvider(string name, AssetPreview? result) : IAssetPreviewProvider
    {
        public string Name { get; } = name;
        public bool CanPreview(AssetRecord asset) => true;
        public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken ct)
            => Task.FromResult(result);
    }

    [Fact]
    public async Task Registry_uses_first_provider_that_produces_a_preview()
    {
        var asset = new AssetRecord { LogicalPath = "x", Type = AssetType.Unknown };
        var registry = new AssetPreviewRegistry(
        [
            new StubProvider("空手", null),
            new StubProvider("有货", AssetPreview.Message("命中", "内容")),
            new StubProvider("不该被调用", AssetPreview.Message("更晚")),
        ]);
        var preview = await registry.PreviewAsync(asset);
        Assert.Equal("命中", preview.InfoLine);
    }

    [Fact]
    public async Task Registry_never_returns_blank_when_every_provider_fails()
    {
        var asset = new AssetRecord { LogicalPath = "x", Type = AssetType.Binary };
        var registry = new AssetPreviewRegistry([new StubProvider("空手", null)]);
        var preview = await registry.PreviewAsync(asset);
        Assert.Equal(AssetPreviewKind.Message, preview.Kind);
        Assert.False(string.IsNullOrWhiteSpace(preview.Text));
        Assert.Contains("十六进制", preview.Text);
    }

    [Fact]
    public async Task Registry_without_selection_returns_none()
    {
        var preview = await AssetPreviewRegistry.CreateDefault().PreviewAsync(null);
        Assert.Equal(AssetPreviewKind.None, preview.Kind);
    }

    [Fact]
    public async Task Hex_fallback_covers_unknown_binary_assets()
    {
        var file = Path.Combine(_root, "blob.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var asset = new AssetRecord { LogicalPath = "blob", SourcePath = file, Type = AssetType.Unknown };

        var preview = await AssetPreviewRegistry.CreateDefault().PreviewAsync(asset);
        Assert.Equal(AssetPreviewKind.Hex, preview.Kind);
        Assert.Contains("十六进制", preview.InfoLine);
        Assert.Contains("00000000", preview.Text); // 偏移列
        Assert.Contains("..", preview.Text);       // ASCII 边栏（不可打印字节）
    }

    [Fact]
    public void Hex_dump_truncates_and_reports_sizes()
    {
        var data = new byte[HexDumpService.MaxBytes + 512];
        var dump = HexDumpService.Dump(data);
        Assert.True(dump.Truncated);
        Assert.Equal(HexDumpService.MaxBytes, dump.ShownBytes);
        Assert.Equal(data.Length, dump.TotalBytes);
        Assert.Contains("已截断", dump.Text);
        Assert.Contains("只读预览", dump.Describe());
    }

    [Fact]
    public void Waveform_envelope_reflects_pcm_amplitude()
    {
        // 16-bit 单声道 8000 Hz，前半静音、后半满幅方波。
        var frames = 800;
        var pcm = new byte[frames * 2];
        for (var i = frames / 2; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), short.MaxValue);
        var wav = BuildPcm16Wav(pcm, sampleRate: 8000, channels: 1);

        var result = AudioWaveform.BuildEnvelope(wav, buckets: 20);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(8000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.Equal(0.1, result.DurationSeconds, 3);
        Assert.Equal(20, result.Envelope.Count);
        Assert.True(result.Envelope.Take(10).All(x => x == 0f));
        Assert.True(result.Envelope.Skip(10).All(x => x > 0.9f));
    }

    [Fact]
    public void Waveform_reports_missing_or_invalid_wav_instead_of_guessing()
    {
        Assert.Contains("没有解码后的 WAV", AudioWaveform.BuildEnvelope(null).UnavailableReason);
        Assert.Contains("RIFF/WAVE", AudioWaveform.BuildEnvelope([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]).UnavailableReason);
    }

    [Fact]
    public async Task Real_cache_samples_map_to_expected_preview_kinds()
    {
        var cacheRoot = FindRealCacheRoot();
        if (cacheRoot is null) return; // 无真实样本：跳过

        var service = new UnityAssetService();
        var registry = AssetPreviewRegistry.CreateDefault();
        var kinds = new Dictionary<AssetType, AssetPreviewKind>();
        foreach (var bundle in EnumerateBundles(cacheRoot, limit: 12))
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception) { continue; }
            foreach (var asset in objects)
            {
                if (asset.Type is not (AssetType.Texture or AssetType.Sprite or AssetType.Text or AssetType.MonoBehaviour)) continue;
                if (kinds.ContainsKey(asset.Type)) continue;
                var preview = await registry.PreviewAsync(asset);
                kinds[asset.Type] = preview.Kind;
                Assert.NotEqual(AssetPreviewKind.None, preview.Kind);
                Assert.False(string.IsNullOrWhiteSpace(preview.InfoLine));
                switch (preview.Kind)
                {
                    case AssetPreviewKind.Image:
                        Assert.NotNull(preview.ImagePng);
                        Assert.NotEmpty(preview.ImagePng!);
                        break;
                    case AssetPreviewKind.Text or AssetPreviewKind.Json:
                        Assert.False(string.IsNullOrWhiteSpace(preview.Text));
                        break;
                    case AssetPreviewKind.Rows:
                        Assert.NotNull(preview.Rows);
                        Assert.NotEmpty(preview.Rows!);
                        break;
                }
            }
            if (kinds.Count == 4) break;
        }
        Assert.Equal(AssetPreviewKind.Image, kinds[AssetType.Texture]);
        Assert.Equal(AssetPreviewKind.Image, kinds[AssetType.Sprite]);
        Assert.Contains(kinds[AssetType.Text], new[] { AssetPreviewKind.Text, AssetPreviewKind.Json });
        Assert.Equal(AssetPreviewKind.Rows, kinds[AssetType.MonoBehaviour]);
    }

    /// <summary>
    /// 关键回归：静态数据表（3 MB 级）在资源工作台不得再产出「大预览」——
    /// 早先截断到 20 万字符后仍当合法 JSON 去建树，UI 侧一次性造几千个
    /// TreeViewItem，点一下预览就未响应。现在只给「摘要 + 开头若干行」。
    /// </summary>
    [Fact]
    public async Task Real_static_table_preview_stays_bounded()
    {
        var cacheRoot = FindRealCacheRoot();
        if (cacheRoot is null) return; // 无真实样本：跳过

        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        var location = StaticBundleLocator.Locate(gameDirectory, [cacheRoot]);
        if (location is null || !location.IsCached) return; // 定位不到静态 bundle：跳过

        var service = new UnityAssetService();
        var texts = service.ScanBundle(location.DataPath!)
            .Where(d => d.Type == AssetType.Text && d.UnityPathId.HasValue)
            .ToArray();
        Assert.NotEmpty(texts);

        // 取最大的一张表（真实环境 3 MB 级）。
        var largest = texts.MaxBy(d => d.Size)!;
        var asset = new AssetRecord
        {
            LogicalPath = $"{location.OuterKey}/{location.InnerHash}/{largest.ContainerPath}/{largest.UnityPathId}.{largest.UnityTypeId}",
            SourcePath = location.DataPath,
            ContainerPath = largest.ContainerPath,
            Account = location.OuterKey,
            Bundle = location.BundleName,
            UnityPathId = largest.UnityPathId,
            UnityTypeId = largest.UnityTypeId,
            Type = largest.Type,
            Size = largest.Size,
            Metadata = { ["unityBundle"] = "true" },
        };

        var preview = await AssetPreviewRegistry.CreateDefault().PreviewAsync(asset);

        Assert.Equal(AssetPreviewKind.Message, preview.Kind);
        Assert.NotNull(preview.Text);
        Assert.True(preview.Text!.Length <= TextPreviewProvider.PreviewCharLimit,
            $"静态表预览必须受上限约束，实际 {preview.Text.Length:N0} 字符");
        Assert.Contains("静态数据工作台", preview.Text);
    }

    [Fact]
    public void Default_registry_orders_new_format_providers_before_hex()    {
        var names = AssetPreviewRegistry.CreateDefault().Providers.Select(p => p.GetType().Name).ToArray();
        // 新格式提供者必须在十六进制兜底之前注册，否则永远轮不到。
        Assert.Contains(nameof(MaterialPreviewProvider), names);
        Assert.Contains(nameof(ShaderPreviewProvider), names);
        Assert.Contains(nameof(VideoClipPreviewProvider), names);
        Assert.Contains(nameof(SpriteAtlasPreviewProvider), names);
        var hex = Array.IndexOf(names, nameof(HexPreviewProvider));
        Assert.True(hex > Array.IndexOf(names, nameof(MaterialPreviewProvider)));
        Assert.True(hex > Array.IndexOf(names, nameof(ShaderPreviewProvider)));
        Assert.True(hex > Array.IndexOf(names, nameof(VideoClipPreviewProvider)));
        Assert.True(hex > Array.IndexOf(names, nameof(SpriteAtlasPreviewProvider)));
        Assert.Equal(names.Length - 1, hex);
    }

    [Fact]
    public async Task Real_cache_new_format_types_preview_as_rows()
    {
        var cacheRoot = FindRealCacheRoot();
        if (cacheRoot is null) return; // 无真实样本：跳过

        var service = new UnityAssetService();
        var registry = AssetPreviewRegistry.CreateDefault();
        var seen = new Dictionary<AssetType, string>(); // 类型 → 期望的中文标签
        foreach (var bundle in EnumerateBundles(cacheRoot, limit: 120))
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception) { continue; }
            foreach (var asset in objects)
            {
                if (asset.Type is not (AssetType.Material or AssetType.Shader or AssetType.Video or AssetType.SpriteAtlas)) continue;
                if (seen.ContainsKey(asset.Type)) continue;
                var preview = await registry.PreviewAsync(asset);
                Assert.Equal(AssetPreviewKind.Rows, preview.Kind);
                Assert.NotNull(preview.Rows);
                Assert.NotEmpty(preview.Rows!);
                seen[asset.Type] = preview.InfoLine;
                if (asset.Type == AssetType.Video)
                    Assert.Contains(preview.Rows!, r => r.Label == "负载大小");
            }
            // 材质与着色器在真实缓存里极常见，必须命中；视频/图集按 bundle 运气（不强求）。
            if (seen.ContainsKey(AssetType.Material) && seen.ContainsKey(AssetType.Shader) && seen.Count >= 4) break;
        }
        Assert.Contains(AssetType.Material, seen.Keys);
        Assert.Contains(AssetType.Shader, seen.Keys);
        Assert.Contains("材质", seen[AssetType.Material]);
        Assert.Contains("着色器", seen[AssetType.Shader]);
    }

    [Fact]
    public async Task Real_cache_named_assets_have_no_unknown_types()
    {
        // 用户默认视图（仅显示容器内资源）看到的每个资源都不该再是「未知类型」。
        var cacheRoot = FindRealCacheRoot();
        if (cacheRoot is null) return;

        var service = new UnityAssetService();
        var named = 0;
        foreach (var bundle in EnumerateBundles(cacheRoot, limit: 60))
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception) { continue; }
            foreach (var asset in objects)
            {
                if (!asset.Metadata.TryGetValue("containerEntry", out var entry) || string.IsNullOrWhiteSpace(entry)) continue;
                named++;
                Assert.NotEqual(AssetType.Unknown, asset.Type);
            }
        }
        Assert.True(named > 0, "60 个真实 bundle 里应能扫到带容器名的资源");
    }

    /// <summary>
    /// 字段树 provider 的覆盖面对齐：Component / GameObject / ScriptableObject 也必须有 provider 接手。
    /// 它们原先没有任何 provider（既不是 MonoBehaviour 也不是 Mesh/Animation/Font），
    /// 于是一路掉到十六进制兜底 —— 用户看到的就是「这个资源加载不出预览」。
    /// </summary>
    [Fact]
    public void Field_tree_provider_claims_components_and_game_objects_not_just_scripts()
    {
        var file = Path.Combine(_root, "coverage.bundle");
        File.WriteAllText(file, "x");
        var provider = new ScriptPreviewProvider();

        foreach (var type in new[]
                 {
                     AssetType.Component, AssetType.GameObject, AssetType.ScriptableObject, AssetType.MonoBehaviour,
                 })
            Assert.True(provider.CanPreview(Bundle(type, file, isBundle: true)), $"{type} 应交给字段树 provider");

        Assert.False(provider.CanPreview(Bundle(AssetType.Component, file, isBundle: false)),
            "非 bundle 资源读不到字段树，不该被接管");
        Assert.False(provider.CanPreview(Bundle(AssetType.Texture, file, isBundle: true)),
            "纹理有自己的 provider，不该被字段树抢走");
    }

    private static AssetRecord Bundle(AssetType type, string file, bool isBundle)
    {
        var asset = new AssetRecord { Type = type, SourcePath = file, UnityPathId = 42 };
        asset.Metadata["unityBundle"] = isBundle ? "true" : "false";
        return asset;
    }

    /// <summary>
    /// 真实缓存上的覆盖率护栏：GameObject / Component 必须得到<b>字段树</b>预览，
    /// 而不是掉到十六进制兜底（那正是用户报的「有些资源加载不出东西」）。
    ///
    /// <para><b>为什么这两类值得单独钉</b>：真实索引里 Component 是<b>数量最多</b>的一类
    /// （51 万个，占全体约四成），GameObject 也有 22 万个；而它们原先没有任何 provider 接手
    /// ——既不是 MonoBehaviour，也不是 Mesh/Animation/Font——于是一路掉到十六进制。</para>
    /// </summary>
    [Fact]
    public async Task Real_cache_components_and_game_objects_preview_as_field_trees()
    {
        var cacheRoot = FindRealCacheRoot();
        if (cacheRoot is null) return; // 无真实样本：跳过（本机装了游戏/Unity 缓存时才会真跑）

        var service = new UnityAssetService();
        var registry = AssetPreviewRegistry.CreateDefault();
        var rows = new HashSet<AssetType>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new HashSet<AssetType>();

        foreach (var bundle in EnumerateBundles(cacheRoot, limit: 40))
        {
            IReadOnlyList<AssetRecord> objects;
            try { objects = service.ScanBundle(bundle); }
            catch (Exception) { continue; }
            foreach (var asset in objects)
            {
                if (asset.Type is not (AssetType.Component or AssetType.GameObject)) continue;
                candidates.Add(asset.Type);
                if (rows.Contains(asset.Type)) continue;

                var preview = await registry.PreviewAsync(asset);
                var key = $"{asset.Type}/{preview.Kind}";
                kinds[key] = kinds.GetValueOrDefault(key) + 1;
                if (preview.Kind == AssetPreviewKind.Rows && rows.Add(asset.Type))
                {
                    Assert.True(preview.Rows is { Count: > 0 }, $"{asset.Type} 的字段树不该为空");
                    Assert.Contains("字段树", preview.InfoLine);
                }
            }
            if (rows.Count == 2) break;
        }

        var stats = string.Join("、", kinds.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
        // 缓存里根本没扫到这两类对象时不能「假装通过」——那会让这条护栏变成摆设。
        Assert.True(candidates.Count > 0,
            "本机 Unity 缓存取样里没有 Component / GameObject 对象，这条护栏无法验证（检查 EnumerateBundles 的取样口径）。");
        Assert.True(rows.Count > 0,
            $"真实缓存里 Component / GameObject 应至少有一类能得到字段树预览，实际：{stats}");
    }

    private static string? FindRealCacheRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
                 {
                     overrideDir,
                     string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany")
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateBundles(string root, int limit)
    {
        var found = 0;
        foreach (var outer in Directory.EnumerateDirectories(root))
        {
            foreach (var inner in Directory.EnumerateDirectories(outer))
            {
                var data = Path.Combine(inner, "__data");
                if (!File.Exists(data)) continue;
                yield return data;
                if (++found >= limit) yield break;
            }
        }
    }

    /// <summary>最小 16-bit PCM WAV 构造（fmt + data）。</summary>
    private static byte[] BuildPcm16Wav(byte[] pcm, int sampleRate, int channels)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }
}
