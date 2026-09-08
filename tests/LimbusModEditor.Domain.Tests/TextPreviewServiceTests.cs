using System.Text;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>文本 / JSON 预览（P3.10）：只读前一段、只认 UTF-8 与 UTF-16，
/// 二进制或其它编码明确返回 null，不猜测内容。</summary>
public class TextPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-text-" + Guid.NewGuid().ToString("N"));

    public TextPreviewServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteText(string name, string text, Encoding? encoding = null)
        => Write(name, (encoding ?? new UTF8Encoding(false)).GetBytes(text));

    [Fact]
    public void Utf8_text_is_previewed_without_bom()
    {
        var path = Write("a.json", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"k\":\"值\"}")]);
        var preview = TextPreviewService.TryPreviewFile(path);
        Assert.NotNull(preview);
        Assert.Equal("UTF-8", preview!.EncodingName);
        Assert.Equal("{\"k\":\"值\"}", preview.Text);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public void Utf16_le_is_decoded()
    {
        var path = Write("b.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("你好")]);
        var preview = TextPreviewService.TryPreviewFile(path);
        Assert.NotNull(preview);
        Assert.Equal("UTF-16 LE", preview!.EncodingName);
        Assert.Equal("你好", preview.Text);
    }

    [Fact]
    public void Binary_payload_is_refused()
    {
        var path = Write("c.bin", [0x01, 0x00, 0x02, 0x00, 0x41, 0x00]);
        Assert.Null(TextPreviewService.TryPreviewFile(path));
    }

    [Fact]
    public void Non_utf8_bytes_are_refused()
    {
        // 0xC3 0x28 不是合法 UTF-8 序列，也不带 UTF-16 BOM。
        var path = Write("d.txt", [0xC3, 0x28, 0x41]);
        Assert.Null(TextPreviewService.TryPreviewFile(path));
    }

    [Fact]
    public void Long_text_is_truncated_to_the_limit()
    {
        var path = WriteText("e.txt", new string('x', 500));
        var preview = TextPreviewService.TryPreviewFile(path, maxChars: 40);
        Assert.NotNull(preview);
        Assert.True(preview!.Truncated);
        Assert.Equal(40, preview.Text.Length);
        Assert.Equal(500, preview.TotalBytes);
    }

    [Fact]
    public void Empty_file_previews_as_empty()
    {
        var path = Write("f.txt", []);
        var preview = TextPreviewService.TryPreviewFile(path);
        Assert.NotNull(preview);
        Assert.Equal(string.Empty, preview!.Text);
        Assert.Equal("空文件", preview.EncodingName);
    }

    [Fact]
    public void Missing_file_and_bad_limit_are_reported()
    {
        Assert.Null(TextPreviewService.TryPreviewFile(Path.Combine(_root, "nope.txt")));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextPreviewService.TryPreviewFile(Path.Combine(_root, "nope.txt"), maxChars: 0));
    }

    [Fact]
    public void Textual_detection_uses_type_or_extension()
    {
        Assert.True(TextPreviewService.LooksTextual(new AssetRecord { Type = AssetType.Text }));
        Assert.True(TextPreviewService.LooksTextual(new AssetRecord { Type = AssetType.Json }));
        Assert.True(TextPreviewService.LooksTextual(new AssetRecord { Type = AssetType.Binary, LogicalPath = "lang/cn/strings.txt" }));
        Assert.False(TextPreviewService.LooksTextual(new AssetRecord { Type = AssetType.Texture, LogicalPath = "assets/ui/logo.png" }));
    }

    [Fact]
    public void Replacement_file_wins_over_source_file()
    {
        var source = WriteText("g-source.txt", "原始内容");
        var replacement = WriteText("g-replaced.txt", "替换后的内容");
        var asset = new AssetRecord
        {
            LogicalPath = "lang/cn/g.txt",
            Type = AssetType.Text,
            SourcePath = source,
            Metadata = { ["replacementPath"] = replacement },
        };
        var preview = TextPreviewService.TryPreview(asset);
        Assert.NotNull(preview);
        Assert.Equal("替换后的内容", preview!.Text);
        Assert.Equal(replacement, TextPreviewService.PreviewPath(asset));
    }
}
