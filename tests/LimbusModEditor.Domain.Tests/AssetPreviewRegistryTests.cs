using System.Buffers.Binary;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

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
