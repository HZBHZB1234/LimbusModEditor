using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 导出前「写前校验」的契约测试：只断言<b>能确定判定</b>的结论，
/// 并守住一条底线——判不了的格式记 info「未校验」，<b>不得伪报通过</b>。
/// </summary>
public sealed class ModExportValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "export-validate-" + Guid.NewGuid().ToString("N"));
    private readonly ModProject _project = new() { Name = "校验用模组" };

    public ModExportValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 测试结束，尽力清理 */ }
    }

    private ModExportValidation Validate() => new ModExportValidator().Validate(_project);

    /// <summary>建一条「有编辑」的资源：替换文件指向 <paramref name="file"/>（可为 null = 只记路径不落盘）。</summary>
    private AssetRecord AddEdited(string logicalPath, AssetType type, string? file, long? originalSize = null)
    {
        // 不指定原大小时用替换文件自身大小：这样「大小量级」这条不会误报，测试只盯被测维度。
        var size = originalSize ?? (file is { } existing && File.Exists(existing) ? new FileInfo(existing).Length : 4096);
        var asset = new AssetRecord { LogicalPath = logicalPath, Type = type, Size = size };
        asset.Metadata["replacementPath"] = file ?? Path.Combine(_root, "missing-" + Guid.NewGuid().ToString("N"));
        asset.Metadata["originalSize"] = size.ToString();
        _project.Assets.Add(asset);
        return asset;
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WritePng(string name, int width = 8, int height = 4)
    {
        var path = Path.Combine(_root, name);
        using var image = new Image<Rgba32>(width, height);
        image.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void Empty_replacement_file_is_an_error()
    {
        var file = WriteFile("empty.png", []);
        AddEdited("art/empty.png", AssetType.Texture, file);

        var result = Validate();

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("0 字节"));
        Assert.True(result.HasBlockingError);
    }

    [Fact]
    public void Missing_replacement_file_is_an_error()
    {
        AddEdited("art/gone.png", AssetType.Texture, null);

        var result = Validate();

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("不在磁盘上"));
    }

    [Fact]
    public void A_valid_png_is_decoded_and_reports_no_error()
    {
        var file = WritePng("ok.png", 16, 9);
        AddEdited("art/ok.png", AssetType.Texture, file);

        var result = Validate();

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Contains(result.Checks, x => x.Message.Contains("16 × 9"));
        Assert.Equal(1, result.CheckedFileCount);
    }

    [Fact]
    public void A_broken_png_is_reported_as_parse_error()
    {
        // 扩展名是 .png、内容是垃圾：魔数判定走图片分支，解码必须失败（不能静默通过）。
        var file = WriteFile("broken.png", "not a png at all"u8.ToArray());
        AddEdited("art/broken.png", AssetType.Texture, file);

        var result = Validate();

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("无法解析"));
    }

    [Fact]
    public void A_truncated_wav_is_reported_as_error()
    {
        // RIFF 头声明 4000 字节，实际只给 40 字节 → 截断。
        var head = new byte[44];
        "RIFF"u8.ToArray().CopyTo(head, 0);
        BitConverter.GetBytes(4000).CopyTo(head, 4);
        "WAVEfmt data"u8.ToArray().CopyTo(head, 8);
        var file = WriteFile("cut.wav", head[..40]);
        AddEdited("audio/cut.wav", AssetType.Audio, file);

        var result = Validate();

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("截断"));
    }

    [Fact]
    public void A_kind_that_does_not_match_the_original_type_is_a_warning()
    {
        var file = WriteFile("table.json", "{\"a\":1}"u8.ToArray());
        AddEdited("art/as-json.json", AssetType.Texture, file);

        var result = Validate();

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Warning && x.Message.Contains("形态与原资源不一致"));
    }

    [Fact]
    public void A_replacement_far_bigger_than_the_original_is_a_warning_with_numbers()
    {
        var file = WritePng("big.png", 64, 64);
        AddEdited("art/big.png", AssetType.Texture, file, originalSize: 16);

        var result = Validate();

        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Warning && x.Message.Contains("差一个量级"));
    }

    [Fact]
    public void A_bad_json_is_reported_as_parse_error()
    {
        var file = WriteFile("table.json", "{ oops"u8.ToArray());
        AddEdited("data/table.json", AssetType.Json, file);

        var result = Validate();

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("JSON"));
    }

    [Fact]
    public void A_format_without_a_parser_is_reported_as_unchecked_and_never_passes()
    {
        var file = WriteFile("mesh.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        AddEdited("mesh/thing.bin", AssetType.Mesh, file);

        var result = Validate();

        // 没有解析器 → 只有 info「未校验」，既没有 error 也没有「通过」的结论。
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Info && x.Message.Contains("未校验"));
        Assert.DoesNotContain(result.Checks, x => x.Message.Contains("校验通过"));
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void An_audio_edit_without_the_original_bank_copy_is_an_error()
    {
        var asset = AddEdited("fsb/0", AssetType.Audio, null);
        asset.Metadata["bankSource"] = "1D101A.assets.bank";
        asset.SourcePath = Path.Combine(_root, "no-such-bank.bank");

        var result = Validate();

        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Error && x.Message.Contains("目标 bank 副本不在磁盘上"));
    }

    [Fact]
    public void A_field_only_edit_says_it_has_no_file_to_check()
    {
        var asset = new AssetRecord { LogicalPath = "obj/thing", Type = AssetType.MonoBehaviour, Size = 128 };
        asset.Metadata["unityFieldEdits"] = "1";
        _project.Assets.Add(asset);

        var result = Validate();

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(0, result.CheckedFileCount);
        Assert.Contains(result.Checks, x => x.Level == ModExportCheckLevel.Info && x.Message.Contains("没有替换文件"));
    }
}
