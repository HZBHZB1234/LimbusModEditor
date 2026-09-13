using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Application.Spine;

/// <summary>
/// Spine 预览 provider：把 Spine 骨架 / 图集文本渲染成「结构 + 图集布局」预览。
///
/// <para><see cref="CanPreview"/> 只按路径 / 类型粗筛（不读盘）；真正的判定在
/// <see cref="SpinePreviewService.Build"/> 里——文本结构不像 Spine 就返回 null，
/// 交回后面的文本预览 provider。因此命中的路径不会因为「看起来像」而被吞掉内容。</para>
/// </summary>
public sealed class SpinePreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SpinePreviewService _service;

    public SpinePreviewProvider(SpinePreviewService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public string Name => "Spine 预览";

    public bool CanPreview(AssetRecord asset) => SpinePreviewService.LooksLikeSpineText(asset);

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("Spine 预览");
            var data = _service.Build(asset, cancellationToken);
            if (data is null)
            {
                Log.Debug("Spine 预览不适用（文本结构不像 Spine 骨架/图集），交回后续 provider：{0}",
                    AssetDisplay.DisplayPath(asset));
                return null;
            }
            Log.Info("Spine 预览完成：{0}；结构行 {1} 条，布局图 {2} 字节",
                data.InfoLine, data.Rows.Count, data.LayoutPng?.Length ?? 0);
            return new AssetPreview(AssetPreviewKind.Spine, data.InfoLine,
                ImagePng: data.LayoutPng, Rows: data.Rows);
        }, cancellationToken);
}
