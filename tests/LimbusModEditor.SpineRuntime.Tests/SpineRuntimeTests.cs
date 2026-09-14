using SkiaSharp;
using Xunit;

namespace LimbusModEditor.SpineRuntime.Tests;

public class SpineRuntimeTests
{
    // ---------- 解析与元信息 ----------

    [Fact]
    public void Parse_Success_ExposesMetadata()
    {
        var (json, atlas, pages) = SyntheticData.RegionMesh();
        var source = new TestTextureSource(pages);
        var result = SpineDocument.TryCreate(json, atlas, source);

        Assert.True(result.Success, "解析应当成功：" + result.Error);
        Assert.Contains(SyntheticData.FadeAnim, result.Document!.AnimationNames);
        Assert.Contains("default", result.Document.Skins);
        Assert.Contains("root", result.Document.Bones);
        Assert.Equal(2, result.Document.Slots.Count);
        Assert.Equal("default", result.Document.CurrentSkinName);
    }

    [Fact]
    public void InvalidJson_ReturnsChineseFailure_NoThrow()
    {
        var (_, atlas, pages) = SyntheticData.RegionMesh();
        var source = new TestTextureSource(pages);

        SpineLoadResult result = null!;
        var ex = Record.Exception(() => result = SpineDocument.TryCreate(SyntheticData.InvalidJson(), atlas, source));

        Assert.Null(ex); // 绝不能把异常抛过边界
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Contains("解析失败", result.Error!); // 中文原因
    }

    // ---------- 渲染非空 ----------

    [Fact]
    public void RegionMesh_RenderNonEmpty()
    {
        var (json, atlas, pages) = SyntheticData.RegionMesh();
        using var doc = Open(json, atlas, pages);
        using var renderer = new SpineFrameRenderer(doc);

        var render = renderer.RenderFrame(0, 200, 200);
        Assert.True(render.Success, "渲染应当成功：" + render.Error);
        Assert.NotNull(render.PngBytes);

        var (_, _, opaque) = DecodeStats(render.PngBytes!);
        Assert.True(opaque > 0, "应当渲染出非空（有非透明像素）的帧。");
    }

    [Fact]
    public void Rotate90Pma_RenderNonEmpty()
    {
        var (json, atlas, pages) = SyntheticData.Rotate90Pma();
        using var doc = Open(json, atlas, pages);
        using var renderer = new SpineFrameRenderer(doc);

        var render = renderer.RenderFrame(0, 200, 200);
        Assert.True(render.Success, "rotate:90 + pma 渲染应当成功：" + render.Error);

        var (_, _, opaque) = DecodeStats(render.PngBytes!);
        Assert.True(opaque > 0, "rotate:90 区域应当渲染出非空帧。");
    }

    // ---------- 时间 → 不同像素 ----------

    [Fact]
    public void DifferentTime_ProducesDifferentPixels()
    {
        var (json, atlas, pages) = SyntheticData.RegionMesh();
        using var doc = Open(json, atlas, pages);
        using var renderer = new SpineFrameRenderer(doc);
        doc.SetAnimation(SyntheticData.FadeAnim, loop: false);

        // 注意：非循环动画在 trackTime>=时长 时 spine 会回到 setup pose（mix=0），
        // 故采样点必须落在 [0, 时长) 内才能体现动画效果。
        var a = renderer.RenderFrame(0, 200, 200);
        var b = renderer.RenderFrame(0.75, 200, 200);
        Assert.True(a.Success && b.Success);

        var pa = PixelHash(a.PngBytes!);
        var pb = PixelHash(b.PngBytes!);
        Assert.NotEqual(pa, pb); // fade 动画改变插槽 alpha → 渲染像素不同
    }

    // ---------- 缺页：中文失败，不抛异常 ----------

    [Fact]
    public void MissingPage_ReturnsChineseFailure_NoThrow()
    {
        var (json, atlas, pages) = SyntheticData.RegionMesh();
        // 故意让唯一纹理页缺失。
        var source = new TestTextureSource(pages, new HashSet<string> { SyntheticData.PageName });
        var load = SpineDocument.TryCreate(json, atlas, source);
        Assert.True(load.Success, "缺页时解析仍应成功（仅记录缺失页）。");
        Assert.Contains(SyntheticData.PageName, load.MissingPages);

        using var doc = load.Document!;
        using var renderer = new SpineFrameRenderer(doc);

        SpineRenderResult render = null!;
        var ex = Record.Exception(() => render = renderer.RenderFrame(0, 200, 200));
        Assert.Null(ex); // 不抛异常
        Assert.False(render.Success);
        Assert.Contains("图集页", render.Error!); // 中文失败原因
    }

    // ---------- FitToCanvas：顶点不越界 ----------

    [Fact]
    public void FitToCanvas_KeepsCornersInsideCanvas()
    {
        // 居中于原点的 200x200 包围盒，放进 300x300 画布（margin=8）。
        var m = SpineFrameRenderer.FitToCanvas(-100, -100, 200, 200, 300, 300, 8);

        // 包围盒四角（spine 坐标，y 向上）。
        var corners = new[] {
            new SKPoint(-100, -100),
            new SKPoint( 100, -100),
            new SKPoint( 100,  100),
            new SKPoint(-100,  100),
        };
        foreach (var c in corners)
        {
            var p = m.MapPoint(c);
            Assert.InRange(p.X, -0.5f, 300.5f);
            Assert.InRange(p.Y, -0.5f, 300.5f);
        }
    }

    // ---------- 辅助 ----------

    private static SpineDocument Open(string json, string atlas, Dictionary<string, byte[]> pages)
    {
        var result = SpineDocument.TryCreate(json, atlas, new TestTextureSource(pages));
        Assert.True(result.Success, "测试夹具解析失败：" + result.Error);
        return result.Document!;
    }

    private static (int width, int height, int opaque) DecodeStats(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        int opaque = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 0) opaque++;
        return (bmp.Width, bmp.Height, opaque);
    }

    private static long PixelHash(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        long h = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                unchecked
                {
                    h = h * 31 + c.Red;
                    h = h * 31 + c.Green;
                    h = h * 31 + c.Blue;
                    h = h * 31 + c.Alpha;
                }
            }
        return h;
    }
}
