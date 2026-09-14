using SkiaSharp;
using Spine;

namespace LimbusModEditor.SpineRuntime;

/// <summary>
/// 把 Spine 骨架当前姿态离屏渲染成位图（PNG 字节）。
/// <para>
/// 支持 region 与 mesh 附件、<see cref="SkeletonClipping"/> 裁剪、图集 <c>pma:true</c> 预乘混合，
/// 以及插槽顶点色（slot 色 × 附件 tint）。所有解析/渲染错误都收敛为 <see cref="SpineRenderResult"/>
/// 中的中文原因，绝不把异常抛过边界。
/// </para>
/// </summary>
public sealed class SpineFrameRenderer : IDisposable
{
    private const float Margin = 8f;

    // region 附件的世界顶点顺序为 [BR, BL, UL, UR]，而 attachment.uvs 顺序为 [UL, UR, BR, BL]，
    // 二者索引不对齐，需在绘制时显式映射（见 DrawSkeleton）。
    private static readonly int[] RegionTriangleIndices = { 0, 1, 2, 0, 2, 3 };

    private readonly SpineDocument _document;
    private readonly SkeletonClipping _clipper = new();
    private float[] _boundsBuffer;

    public SpineFrameRenderer(SpineDocument document) => _document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>
    /// 计算把骨架 AABB 适配进画布的变换矩阵（含 Y 翻转，使立绘正向朝上）。
    /// 同时被渲染与测试复用，以便验证“顶点落在画布内”。
    /// </summary>
    public static SKMatrix FitToCanvas(float boundsX, float boundsY, float boundsW, float boundsH, int width, int height, float margin = Margin)
    {
        if (boundsW <= 0 || boundsH <= 0) return SKMatrix.CreateIdentity();
        float scale = Math.Min((width - 2 * margin) / boundsW, (height - 2 * margin) / boundsH);
        float tx = margin - scale * boundsX;
        float ty = (height - margin) + scale * boundsY; // 翻转 Y：屏幕 y 向下
        var m = SKMatrix.CreateScale(scale, -scale);
        m.TransX = tx;
        m.TransY = ty;
        return m;
    }

    /// <summary>
    /// 在给定时间渲染一帧为 PNG 字节。
    /// </summary>
    /// <param name="time">动画绝对轨道时间（秒）。</param>
    /// <param name="width">画布宽（像素）。</param>
    /// <param name="height">画布高（像素）。</param>
    /// <param name="background">背景色；透明（默认）表示镂空。</param>
    public SpineRenderResult RenderFrame(double time, int width, int height, SKColor background = default)
    {
        try
        {
            if (width <= 0 || height <= 0)
                return SpineRenderResult.Fail("渲染尺寸无效：宽和高都必须大于 0。");

            _document.ApplyTime(time);

            // 缺页检查（只检查当前绘制顺序里实际引用到的页）。
            var missing = CollectMissingPages();
            if (missing.Count > 0)
                return SpineRenderResult.Fail("图集页缺失，无法渲染：" + string.Join("、", missing));

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(background);

            _document.GetBounds(out float bx, out float by, out float bw, out float bh, ref _boundsBuffer);
            canvas.SetMatrix(FitToCanvas(bx, by, bw, bh, width, height));

            DrawSkeleton(canvas);

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded == null)
                return SpineRenderResult.Fail("编码 PNG 失败（SKData 为空）。");
            return SpineRenderResult.Ok(encoded.ToArray());
        }
        catch (Exception ex)
        {
            return SpineRenderResult.Fail("渲染帧时发生错误：" + ex.Message);
        }
    }

    private List<string> CollectMissingPages()
    {
        var missing = new List<string>();
        var drawOrder = _document.Skeleton.DrawOrder;
        for (int i = 0; i < drawOrder.Count; i++)
        {
            var att = drawOrder.Items[i].Attachment;
            if (att is RegionAttachment ra && ra.RendererObject is AtlasRegion r1 && r1.page.rendererObject == null)
                missing.Add(r1.page.name);
            else if (att is MeshAttachment ma && ma.RendererObject is AtlasRegion r2 && r2.page.rendererObject == null)
                missing.Add(r2.page.name);
        }
        return missing.Distinct().ToList();
    }

    private void DrawSkeleton(SKCanvas canvas)
    {
        var drawOrder = _document.Skeleton.DrawOrder;
        for (int i = 0; i < drawOrder.Count; i++)
        {
            Slot slot = drawOrder.Items[i];
            if (slot.Attachment == null)
            {
                _clipper.ClipEnd(slot);
                continue;
            }
            if (slot.Attachment is ClippingAttachment clip)
            {
                _clipper.ClipStart(slot, clip);
                continue;
            }

            AtlasRegion region = null;
            SKImage pageImg = null;
            int pw = 0, ph = 0;
            float[] worldVerts;
            int[] triIndices;
            float[] uvsArr;
            float vr, vg, vb, va;

            if (slot.Attachment is RegionAttachment ra)
            {
                region = (AtlasRegion)ra.RendererObject;
                if (region.page.rendererObject is not SKImage img) { _clipper.ClipEnd(slot); continue; }
                pageImg = img; pw = region.page.width; ph = region.page.height;
                worldVerts = new float[8];
                ra.ComputeWorldVertices(slot.bone, worldVerts, 0, 2);
                uvsArr = ra.uvs;
                triIndices = RegionTriangleIndices;
                vr = slot.R * ra.R; vg = slot.G * ra.G; vb = slot.B * ra.B; va = slot.A * ra.A;
            }
            else if (slot.Attachment is MeshAttachment ma)
            {
                region = (AtlasRegion)ma.RendererObject;
                if (region.page.rendererObject is not SKImage img) { _clipper.ClipEnd(slot); continue; }
                pageImg = img; pw = region.page.width; ph = region.page.height;
                int wl = ma.WorldVerticesLength;
                worldVerts = new float[wl];
                ma.ComputeWorldVertices(slot, 0, wl, worldVerts, 0);
                uvsArr = ma.uvs;
                triIndices = ma.triangles;
                vr = slot.R * ma.R; vg = slot.G * ma.G; vb = slot.B * ma.B; va = slot.A * ma.A;
            }
            else
            {
                _clipper.ClipEnd(slot);
                continue;
            }

            float[] drawVerts;
            float[] drawUvs;
            int[] drawTris;
            bool isRegion = slot.Attachment is RegionAttachment;
            bool clipping = _clipper.IsClipping;
            if (clipping)
            {
                _clipper.ClipTriangles(worldVerts, worldVerts.Length, triIndices, triIndices.Length, uvsArr);
                drawVerts = _clipper.ClippedVertices.Items;
                drawUvs = _clipper.ClippedUVs.Items;
                drawTris = _clipper.ClippedTriangles.Items;
            }
            else
            {
                drawVerts = worldVerts;
                drawUvs = uvsArr;
                drawTris = triIndices;
            }

            int vCount = drawVerts.Length / 2;
            if (vCount == 0) { _clipper.ClipEnd(slot); continue; }

            var positions = new SKPoint[vCount];
            var textures = new SKPoint[vCount];
            if (isRegion && !clipping)
            {
                // 世界顶点顺序 [BR, BL, UL, UR] 与 uvs 顺序 [UL, UR, BR, BL] 重新对齐。
                positions[0] = Vertex(drawVerts, 0); textures[0] = Tex(drawUvs, 4, pw, ph); // BR
                positions[1] = Vertex(drawVerts, 2); textures[1] = Tex(drawUvs, 6, pw, ph); // BL
                positions[2] = Vertex(drawVerts, 4); textures[2] = Tex(drawUvs, 0, pw, ph); // UL
                positions[3] = Vertex(drawVerts, 6); textures[3] = Tex(drawUvs, 2, pw, ph); // UR
            }
            else
            {
                for (int k = 0; k < vCount; k++)
                {
                    positions[k] = Vertex(drawVerts, k * 2);
                    textures[k] = Tex(drawUvs, k * 2, pw, ph);
                }
            }

            var indices = new ushort[drawTris.Length];
            for (int k = 0; k < drawTris.Length; k++) indices[k] = (ushort)drawTris[k];

            if (pageImg != null)
                DrawTriangles(canvas, pageImg, positions, textures, indices, vr, vg, vb, va, slot.data.BlendMode);

            _clipper.ClipEnd(slot);
        }
        _clipper.ClipEnd();
    }

    private static void DrawTriangles(SKCanvas canvas, SKImage pageImg, SKPoint[] positions, SKPoint[] textures, ushort[] indices,
        float vr, float vg, float vb, float va, BlendMode blend)
    {
        // 着色（slot 色 × 附件 tint）通过 ColorMatrix 完成。
        // SkiaSharp 实测：DrawVertices 的“顶点色”和 CreateBlendMode(Multiply) 都不会降低输出 alpha
        // （背景贴图不透明时 Multiply 的 alpha 合成结果恒为 1）。只有 ColorMatrix 能在 straight 空间
        // 按通道直接缩放 (vr,vg,vb,va)，同时正确染 RGB 与降 alpha。顶点色无需参与，传白色恒等即可。
        float r = Clamp01(vr), g = Clamp01(vg), b = Clamp01(vb), a = Clamp01(va);
        var matrix = new float[] {
            r, 0, 0, 0, 0,
            0, g, 0, 0, 0,
            0, 0, b, 0, 0,
            0, 0, 0, a, 0
        };

        using var baseShader = SKShader.CreateImage(pageImg, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { Shader = baseShader, ColorFilter = colorFilter, IsAntialias = false, IsDither = false };
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, textures, null, indices);
        canvas.DrawVertices(vertices, BlendModeToSkia(blend), paint);
    }

    private static SKPoint Vertex(float[] v, int i) => new(v[i], v[i + 1]);
    private static SKPoint Tex(float[] uv, int i, int pw, int ph) => new(uv[i] * pw, uv[i + 1] * ph);
    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

    private static SKBlendMode BlendModeToSkia(BlendMode bm) => bm switch
    {
        BlendMode.Additive => SKBlendMode.Plus,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        _ => SKBlendMode.SrcOver
    };

    public void Dispose() => _clipper.ClipEnd();
}
