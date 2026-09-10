using System.Globalization;
using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Assets;

/// <summary>属性面板的一行（中文标签 + 值）。</summary>
public sealed record AssetPropertyRow(string Label, string Value);

/// <summary>
/// 选中资源的属性汇总（plan-01 第 2 步）：全部数值来自资源自身（Unity 类型树 /
/// FSB 结构 / 文本字节），<b>单项读取失败只降级为「（不可读：原因）」</b>，
/// 绝不抛整块异常、也绝不用默认值补齐。调用方负责放到后台线程执行。
/// </summary>
public sealed class AssetPropertyService
{
    /// <summary>汇总属性行；<paramref name="asset"/> 为当前选中资源。</summary>
    public IReadOnlyList<AssetPropertyRow> Describe(AssetRecord asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var rows = new List<AssetPropertyRow>
        {
            new("类型", AssetDisplay.TypeLabel(asset.Type)),
            new("大小", DescribeSize(asset.Size)),
            new("修改状态", AssetDisplay.StateLabel(asset.EditState))
        };

        if (asset.Metadata.TryGetValue("containerEntry", out var entry) && !string.IsNullOrWhiteSpace(entry))
            rows.Add(new("容器路径", entry));
        else if (!string.IsNullOrWhiteSpace(asset.ContainerPath))
            rows.Add(new("SerializedFile", asset.ContainerPath));
        rows.Add(new("所在 bundle", DescribeBundle(asset)));

        // 逐类追加：任何一项失败都只影响该行。
        switch (asset.Type)
        {
            case AssetType.Texture:
                AddTextureRows(rows, asset, cancellationToken);
                break;
            case AssetType.Sprite:
                AddSpriteRows(rows, asset, cancellationToken);
                break;
            case AssetType.Audio:
                AddAudioRows(rows, asset, cancellationToken);
                break;
            case AssetType.Text or AssetType.Json:
                AddTextRows(rows, asset, cancellationToken);
                break;
            case AssetType.MonoBehaviour or AssetType.MonoScript:
                AddScriptRows(rows, asset, cancellationToken);
                break;
            case AssetType.Mesh or AssetType.Animation or AssetType.Font:
                AddSummaryRows(rows, asset, cancellationToken);
                break;
            case AssetType.Material or AssetType.Shader or AssetType.Video or AssetType.SpriteAtlas:
                AddFieldTreeRows(rows, asset, cancellationToken);
                break;
        }
        return rows;
    }

    private static string DescribeSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} 字节";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}（{bytes.ToString("N0", CultureInfo.InvariantCulture)} 字节）";
    }

    /// <summary>所在 bundle：缓存外层键前 8 位 + 内层键（外层键是游戏版本相关
    /// 的 32 位十六进制，缩短只为显示）。</summary>
    private static string DescribeBundle(AssetRecord asset)
    {
        var outer = asset.Metadata.GetValueOrDefault("cacheOuter") ?? asset.Account;
        var inner = asset.Metadata.GetValueOrDefault("cacheInner") ?? asset.Bundle;
        var outerLabel = string.IsNullOrWhiteSpace(outer)
            ? "—"
            : outer.Length > 8 ? outer[..8] + "…" : outer;
        return string.IsNullOrWhiteSpace(inner) ? outerLabel : $"{outerLabel} / {inner}";
    }

    private static void AddTextureRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        if (!TryReadFields(asset, cancellationToken, out var root))
        {
            rows.Add(new("纹理属性", Unreadable(asset)));
            return;
        }
        Add(rows, "尺寸", () =>
        {
            var width = FieldValue(root, "m_Width") ?? "?";
            var height = FieldValue(root, "m_Height") ?? "?";
            return $"{width} × {height}";
        });
        Add(rows, "格式", () =>
        {
            var format = FieldValue(root, "m_TextureFormat");
            if (format is null) return "（类型树未包含 m_TextureFormat）";
            if (int.TryParse(format, out var value) && Enum.IsDefined(typeof(UnityTexturePixelFormat), value))
            {
                var known = (UnityTexturePixelFormat)value;
                var capability = TextureFormatCatalog.Describe(known);
                return $"{known}（{value}）· {capability.Notes}";
            }
            return $"{format}（未收录格式：暂不支持预览/替换）";
        });
        Add(rows, "mipmap", () =>
        {
            var mipCount = FieldValue(root, "m_MipCount");
            return mipCount is null ? "（类型树未包含 m_MipCount）" : $"{mipCount} 层";
        });
        Add(rows, "流式（.resS）", () =>
        {
            var streamData = Child(root, "m_StreamData");
            var path = streamData is null ? null : FieldValue(streamData, "path");
            return string.IsNullOrWhiteSpace(path) ? "否（像素内联）" : $"是：{path}";
        });
        Add(rows, "名称", () => FieldValue(root, "m_Name") ?? "（无 m_Name）");
    }

    private static void AddSpriteRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        UnitySpriteObject? sprite;
        try { sprite = ReadSprite(asset, cancellationToken); }
        catch (Exception ex) { rows.Add(new("Sprite 属性", Unreadable(ex))); return; }
        if (sprite is null) { rows.Add(new("Sprite 属性", "（未找到 Sprite 对象）")); return; }

        rows.Add(new("rect 区域", $"{Format(sprite.Rect.Width)} × {Format(sprite.Rect.Height)} @({Format(sprite.Rect.X)}, {Format(sprite.Rect.Y)})"));
        rows.Add(new("pivot", $"({Format(sprite.Pivot.X)}, {Format(sprite.Pivot.Y)})"));
        rows.Add(new("border", $"{Format(sprite.Border.Left)}/{Format(sprite.Border.Bottom)}/{Format(sprite.Border.Right)}/{Format(sprite.Border.Top)}"));
        rows.Add(new("PixelsToUnits", Format(sprite.PixelsToUnits)));
        rows.Add(new("图集纹理", sprite.TexturePathId is { } texturePathId && texturePathId != 0
            ? $"Path {texturePathId}"
            : "（未引用 Texture2D）"));

        // 裁剪区域（m_RD.textureRect）与纹理尺寸：合成预览的同一口径。
        Add(rows, "裁剪区域", () =>
        {
            if (!TryReadFields(asset, cancellationToken, out var root)) return "（字段树不可读）";
            var renderData = Child(root, "m_RD");
            var textureRect = renderData is null ? null : Child(renderData, "textureRect");
            if (textureRect is null) return "（类型树未包含 m_RD.textureRect，预览回退 m_Rect）";
            return $"{FieldValue(textureRect, "width")} × {FieldValue(textureRect, "height")} @({FieldValue(textureRect, "x")}, {FieldValue(textureRect, "y")})";
        });
    }

    private static void AddAudioRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        var isBankFsb = asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase);
        if (isBankFsb)
        {
            Add(rows, "Bank 音频", () =>
            {
                var fsb = Fsb5Parser.TryParse(File.ReadAllBytes(asset.SourcePath!));
                return fsb is null
                    ? "（不是可解析的 FSB5 负载）"
                    : $"FSB5 codec={fsb.CodecName} 样本 {fsb.SampleCount} 个";
            });
            return;
        }

        UnityAudioClipObject? clip;
        try { clip = ReadAudioClip(asset, cancellationToken); }
        catch (Exception ex) { rows.Add(new("音频属性", Unreadable(ex))); return; }
        if (clip is null) { rows.Add(new("音频属性", "（非 Unity AudioClip 资源）")); return; }

        rows.Add(new("名称", string.IsNullOrWhiteSpace(clip.Name) ? "（无 m_Name）" : clip.Name));
        rows.Add(new("压缩格式", clip.Format < 0 ? "（类型树未包含）" : clip.Format.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new("声道数", clip.Channels < 0 ? "（类型树未包含）" : clip.Channels.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new("采样率", clip.Frequency < 0 ? "（类型树未包含）" : $"{clip.Frequency} Hz"));
        if (clip.Length > 0) rows.Add(new("时长", $"{clip.Length:0.###} 秒"));
        rows.Add(new("负载大小", DescribeSize(clip.Data.Length)));
        Add(rows, "FSB 结构", () =>
        {
            var fsb = Fsb5Parser.TryParse(clip.Data);
            if (fsb is null) return "（负载不是 FSB5：可能为其他压缩形态，试听会给出具体原因）";
            var sample = fsb.Samples.FirstOrDefault();
            return $"FSB5 codec={fsb.CodecName} 样本 {fsb.SampleCount} 个" +
                   (sample is null ? string.Empty : $"，首个样本 {sample.SampleRate} Hz / {sample.Channels} 声道 / {sample.SampleCount} 采样点");
        });
    }

    private static void AddTextRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        var isBundle = asset.Metadata.GetValueOrDefault("unityBundle") == "true";
        if (isBundle)
        {
            try
            {
                var textAsset = new UnityAssetService().ReadBundleTextAsset(
                    asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
                var decoded = textAsset.TryDecodeUtf8();
                rows.Add(new("名称", string.IsNullOrWhiteSpace(textAsset.Name) ? "（无 m_Name）" : textAsset.Name));
                rows.Add(new("编码", decoded is null ? "非 UTF-8（按原始字节保留）" : "UTF-8"));
                rows.Add(new("字符数", decoded is null ? $"（不可解码，共 {textAsset.Data.Length:N0} 字节）" : $"{decoded.Length:N0} 字符 / {textAsset.Data.Length:N0} 字节"));
                rows.Add(new("JSON 可解析", decoded is null ? "（非文本，无法判断）" : DescribeJson(decoded)));
            }
            catch (Exception ex) { rows.Add(new("文本属性", Unreadable(ex))); }
            return;
        }

        try
        {
            var preview = TextPreviewService.TryPreview(asset);
            if (preview is null) { rows.Add(new("文本属性", "（非可读文本：非 UTF-8/UTF-16 或含二进制内容）")); return; }
            rows.Add(new("编码", preview.EncodingName));
            rows.Add(new("字符数", $"{preview.Text.Length:N0} 字符 / {preview.TotalBytes:N0} 字节"));
            rows.Add(new("JSON 可解析", DescribeJson(preview.Text)));
        }
        catch (Exception ex) { rows.Add(new("文本属性", Unreadable(ex))); }
    }

    private static string DescribeJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => $"是（对象，{document.RootElement.EnumerateObject().Count()} 个顶层键）",
                JsonValueKind.Array => $"是（数组，{document.RootElement.GetArrayLength()} 项）",
                _ => $"是（{document.RootElement.ValueKind}）"
            };
        }
        catch (JsonException) { return "否（不是合法 JSON）"; }
    }

    private static void AddScriptRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        var isBundle = asset.Metadata.GetValueOrDefault("unityBundle") == "true";
        if (asset.UnityPathId is not { } pathId || string.IsNullOrWhiteSpace(asset.SourcePath))
        {
            rows.Add(new("脚本信息", "（该资源没有 Unity 对象定位信息）"));
            return;
        }
        Add(rows, "脚本类", () =>
        {
            var service = new UnityAssetService();
            var info = isBundle
                ? service.ReadBundleObjectScriptInfo(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken)
                : service.ReadObjectScriptInfo(asset.SourcePath!, pathId, cancellationToken);
            if (info is null) return "（不是 MonoBehaviour / 无法解析脚本）";
            var name = string.IsNullOrWhiteSpace(info.Namespace) ? info.ClassName : $"{info.Namespace}.{info.ClassName}";
            var assembly = string.IsNullOrWhiteSpace(info.AssemblyName) ? string.Empty : $"（{info.AssemblyName}）";
            var external = info.ExternalGuid is null ? string.Empty : $" · 外部 GUID {info.ExternalGuid}";
            var reason = info.TypeTreeMissingReason is null ? string.Empty : $" · {info.TypeTreeMissingReason}";
            return $"{name ?? "（类名不可解析）"}{assembly}{external}{reason}";
        });
        Add(rows, "字段数", () =>
        {
            if (!TryReadFields(asset, cancellationToken, out var root)) return "（字段树不可读）";
            return $"{CountNodes(root) - 1:N0}（含嵌套）";
        });
    }

    /// <summary>材质 / 着色器 / 视频 / 图集：对象名称 + 字段规模（与预览面板互补）。</summary>
    private static void AddFieldTreeRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        Add(rows, "对象名称", () =>
        {
            if (!TryReadFields(asset, cancellationToken, out var root)) return "（字段树不可读）";
            return FieldValue(root, "m_Name") ?? "（无 m_Name）";
        });
        Add(rows, "字段数", () =>
        {
            if (!TryReadFields(asset, cancellationToken, out var root)) return "（字段树不可读）";
            return $"{CountNodes(root) - 1:N0}（含嵌套）";
        });
    }

    private static void AddSummaryRows(List<AssetPropertyRow> rows, AssetRecord asset, CancellationToken cancellationToken)
    {
        Add(rows, "对象摘要", () =>
        {
            var service = new UnityAssetService();
            var summary = asset.Metadata.GetValueOrDefault("unityBundle") == "true"
                ? service.ReadBundleObjectSummary(asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken)
                : service.ReadObjectSummary(asset.SourcePath!, asset.UnityPathId!.Value, cancellationToken);
            if (summary is null) return "（该类型暂无摘要支持）";
            var parts = summary.Fields.Select(f => $"{f.Label} {f.Value}");
            return string.Join(" · ", parts);
        });
    }

    // ── 内部工具 ─────────────────────────────────────────────────────

    private static void Add(List<AssetPropertyRow> rows, string label, Func<string> read)
    {
        try { rows.Add(new AssetPropertyRow(label, read())); }
        catch (Exception ex) { rows.Add(new AssetPropertyRow(label, Unreadable(ex))); }
    }

    private static string Unreadable(Exception ex) => $"（不可读：{ex.Message}）";

    private static string Unreadable(AssetRecord asset)
        => $"（不可读：资源没有可定位的 Unity bundle 对象：{AssetDisplay.DisplayPath(asset)}）";

    private static bool TryReadFields(AssetRecord asset, CancellationToken cancellationToken, out UnityFieldNode root)
    {
        root = null!;
        if (asset.Metadata.GetValueOrDefault("unityBundle") != "true" ||
            asset.UnityPathId is not { } pathId ||
            string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath))
            return false;
        var fields = new UnityAssetService().ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
        if (fields.Count == 0) return false;
        root = fields[0];
        return true;
    }

    private static UnitySpriteObject? ReadSprite(AssetRecord asset, CancellationToken cancellationToken)
    {
        if (asset.UnityPathId is not { } pathId || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath))
            return null;
        return new UnityAssetService().ReadSprite(asset.SourcePath!, pathId, cancellationToken);
    }

    private static UnityAudioClipObject? ReadAudioClip(AssetRecord asset, CancellationToken cancellationToken)
    {
        if (asset.Metadata.GetValueOrDefault("unityBundle") != "true" ||
            asset.UnityPathId is not { } pathId || string.IsNullOrWhiteSpace(asset.SourcePath))
            return null;
        return new UnityAssetService().ReadBundleAudioClipData(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
    }

    private static string? FieldValue(UnityFieldNode node, string name)
        => Child(node, name)?.Value;

    private static UnityFieldNode? Child(UnityFieldNode node, string name)
        => node.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int CountNodes(UnityFieldNode node)
        => 1 + node.Children.Sum(CountNodes);

    private static string Format(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
