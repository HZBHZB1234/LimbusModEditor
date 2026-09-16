using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 数据网关实现：从 Unity bundle 定位、读取并预解码 Spine 三件套。
/// 所有方法绝不抛异常；失败返回中文原因。
/// 不引用 WPF（铁律 §3-10）；使用既有 AssetsTools.NET 适配层与 ImageSharp 纹理解码。
/// </summary>
internal sealed class SpineDataGateway : ISpineDataGateway, IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly SpineAssetLocator _locator;
    private readonly AssetsToolsBackend _backend = new();

    public SpineDataGateway(string dbPath)
    {
        _locator = new SpineAssetLocator(dbPath);
    }

    public async Task<(SpineRawData? Data, string? Error)> GetSpineDataAsync(
        string assetId, CancellationToken cancellationToken = default)
    {
        // assetId 格式：containerEntry（纯数据定位，不猜测 id）
        return await GetSpineDataByPathAsync(assetId, cancellationToken);
    }

    public async Task<(SpineRawData? Data, string? Error)> GetSpineDataByPathAsync(
        string containerEntry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerEntry))
            return (null, "容器路径为空，无法获取 Spine 数据。");

        if (!SpinePathRules.IsSpinePath(containerEntry))
            return (null, $"路径 \"{containerEntry}\" 不是 Spine 资源。");

        try
        {
            return await Task.Run(() => LoadSpineData(containerEntry, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "获取 Spine 数据失败：{0}", containerEntry);
            return (null, $"获取 Spine 数据失败：{ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SpineRawData>> GetSpineDataBatchAsync(
        IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        var results = new List<SpineRawData>();
        foreach (var id in assetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (data, error) = await GetSpineDataAsync(id, cancellationToken);
            if (data is not null) results.Add(data);
        }
        return results;
    }

    private (SpineRawData? Data, string? Error) LoadSpineData(string containerEntry, CancellationToken cancellationToken)
    {
        // 1. 定位同目录资源
        var siblings = _locator.FindSiblings(containerEntry);
        if (siblings.Count == 0)
            return (null, $"找不到 \"{containerEntry}\" 所在的 Spine 资源目录。");

        // 2. 分类：骨架 JSON、图集文本、纹理
        var skeletonEntry = FindSkeletonEntry(containerEntry, siblings);
        var atlasEntry = FindAtlasEntry(siblings);
        var textureEntries = FindTextureEntries(siblings);

        if (skeletonEntry is null)
            return (null, $"同目录里找不到骨架 JSON（*.json），无法获取 Spine 数据。");

        if (atlasEntry is null)
            return (null, $"同目录里找不到图集文本（*.atlas.txt），无法获取 Spine 数据。");

        // 3. 读取骨架字节
        var skeletonBytes = ReadTextAssetBytes(skeletonEntry, cancellationToken);
        if (skeletonBytes.Length == 0)
            return (null, $"骨架 JSON 读取失败（{skeletonEntry.ContainerEntry}）：可能是空文件或不在可读容器里。");

        var skeletonFormat = SpinePathRules.IsSkeletonJsonFileName(SpinePathRules.FileNameOf(skeletonEntry.ContainerEntry))
            ? "json" : "binary";

        // 4. 读取图集文本
        var atlasText = ReadTextAssetText(atlasEntry, cancellationToken);
        if (string.IsNullOrWhiteSpace(atlasText))
            return (null, $"图集文本读取失败（{atlasEntry.ContainerEntry}）：可能是空文件或不在可读容器里。");

        // 5. 读取并解码纹理
        var pageBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var texEntry in textureEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageName = SpinePathRules.FileNameOf(texEntry.ContainerEntry);
            try
            {
                var png = ReadTexturePng(texEntry, cancellationToken);
                if (png.Length > 0)
                    pageBytes[pageName] = png;
                else
                    warnings.Add($"{pageName}：解码结果为空");
            }
            catch (Exception ex)
            {
                warnings.Add($"{pageName}：{ex.Message}");
                Log.Warn(ex, "Spine 纹理解码失败：{0}", texEntry.ContainerEntry);
            }
        }

        var folder = SpinePathRules.FolderOf(containerEntry);
        var label = folder.Length == 0 ? SpinePathRules.FileNameOf(containerEntry)
            : folder[(folder.LastIndexOf('/') + 1)..];

        var data = new SpineRawData(
            skeletonBytes,
            skeletonFormat,
            atlasText,
            pageBytes,
            textureEntries.Select(s => SpinePathRules.FileNameOf(s.ContainerEntry)).ToList(),
            string.IsNullOrWhiteSpace(label) ? "spine" : label
        );

        if (warnings.Count > 0)
            Log.Warn("Spine 数据获取完成，有 {0} 个纹理警告：{1}", warnings.Count, string.Join("；", warnings.Take(3)));
        else
            Log.Info("Spine 数据获取完成：骨架 {0} 字节，图集 {1} 字节，{2} 页纹理",
                skeletonBytes.Length, atlasText.Length, pageBytes.Count);

        return (data, null);
    }

    private SpineAssetInfo? FindSkeletonEntry(string selfEntry, IReadOnlyList<SpineAssetInfo> siblings)
    {
        // 自身是 .json 且不是 .atlas.json → 直接用
        if (SpinePathRules.IsSkeletonJsonFileName(SpinePathRules.FileNameOf(selfEntry)))
            return siblings.FirstOrDefault(s => string.Equals(s.ContainerEntry, selfEntry, StringComparison.OrdinalIgnoreCase));

        // 否则找同目录第一个 .json（排除 .atlas.json）
        return siblings.FirstOrDefault(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return (s.TypeId == 49 /* TextAsset */ || s.TypeId == 4) && SpinePathRules.IsSkeletonJsonFileName(name);
        });
    }

    private SpineAssetInfo? FindAtlasEntry(IReadOnlyList<SpineAssetInfo> siblings)
    {
        return siblings.FirstOrDefault(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return (s.TypeId == 49 /* TextAsset */ || s.TypeId == 4) && SpinePathRules.IsAtlasFileName(name);
        });
    }

    private IReadOnlyList<SpineAssetInfo> FindTextureEntries(IReadOnlyList<SpineAssetInfo> siblings)
    {
        return siblings.Where(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return s.TypeId == 28 /* Texture2D */ || s.TypeId == 213 || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private byte[] ReadTextAssetBytes(SpineAssetInfo entry, CancellationToken cancellationToken)
    {
        try
        {
            var text = _backend.ReadBundleTextAsset(entry.BundleDataPath, entry.ContainerName, entry.PathId, cancellationToken);
            return text.Data;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "读取文本失败：{0}", entry.ContainerEntry);
            return [];
        }
    }

    private string ReadTextAssetText(SpineAssetInfo entry, CancellationToken cancellationToken)
    {
        var bytes = ReadTextAssetBytes(entry, cancellationToken);
        if (bytes.Length == 0) return string.Empty;
        try
        {
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private byte[] ReadTexturePng(SpineAssetInfo entry, CancellationToken cancellationToken)
    {
        var texture = _backend.ReadTexture(entry.BundleDataPath, entry.PathId, cancellationToken);
        if (texture is null)
            throw new InvalidDataException($"纹理读取失败（{entry.ContainerEntry}）。");

        if (TextureFormatRules.IsDxt(texture.TextureFormat))
        {
            // DXT5/DXT1 → 预解码为 PNG
            var info = new UnityTextureInfo(texture.Width, texture.Height, (UnityTexturePixelFormat)texture.TextureFormat, texture.PixelData);
            return new UnityTextureCodec().ToPng(info);
        }
        else if (TextureFormatRules.IsPassthrough(texture.TextureFormat))
        {
            // RGBA32/RGB24/BGRA32/ARGB32 → 编码为 PNG
            var info = new UnityTextureInfo(texture.Width, texture.Height, (UnityTexturePixelFormat)texture.TextureFormat, texture.PixelData);
            return new UnityTextureCodec().ToPng(info);
        }
        else
        {
            // 不支持的格式 → 尝试转 RGBA32 再编码
            var info = new UnityTextureInfo(texture.Width, texture.Height, (UnityTexturePixelFormat)texture.TextureFormat, texture.PixelData);
            return new UnityTextureCodec().ToPng(info);
        }
    }

    public void Dispose()
    {
        _backend.Dispose();
    }
}

/// <summary>
/// 网关工厂：创建配置好的 ISpineDataGateway 实例。
/// </summary>
public static class SpineDataGatewayFactory
{
    /// <summary>创建网关实例（使用默认索引库路径）。</summary>
    public static ISpineDataGateway Create(string? dbPath = null)
    {
        dbPath ??= System.IO.Path.Combine(
            AppContext.BaseDirectory, "cache", "unity-cache-index.db");
        return new SpineDataGateway(dbPath);
    }
}
