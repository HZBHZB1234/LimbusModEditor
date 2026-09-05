using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Formats;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P3.2: the export compatibility matrix must mirror the exporter's
/// actual pipeline — same format, Carra family, Lunartique→Carra conversions
/// and directory sources are supported; everything else is disabled with a
/// reason.</summary>
public class ExportMatrixTests
{
    [Theory]
    [InlineData(ModFormatKind.Carra, ModFormatKind.Carra)]
    [InlineData(ModFormatKind.Carra2, ModFormatKind.Carra2)]
    [InlineData(ModFormatKind.Rebank, ModFormatKind.Rebank)]
    [InlineData(ModFormatKind.Bank, ModFormatKind.Bank)]
    [InlineData(ModFormatKind.Lunartique, ModFormatKind.Lunartique)]
    public void Same_format_is_supported(ModFormatKind source, ModFormatKind target)
    {
        var compatibility = ExportMatrix.Evaluate(source, target);
        Assert.True(compatibility.Supported);
    }

    [Fact]
    public void Carra_family_converts_both_ways()
    {
        Assert.True(ExportMatrix.Evaluate(ModFormatKind.Carra, ModFormatKind.Carra2).Supported);
        Assert.True(ExportMatrix.Evaluate(ModFormatKind.Carra2, ModFormatKind.Carra).Supported);
    }

    [Fact]
    public void Lunartique_converts_to_object_level_carra()
    {
        var toCarra = ExportMatrix.Evaluate(ModFormatKind.Lunartique, ModFormatKind.Carra);
        var toCarra2 = ExportMatrix.Evaluate(ModFormatKind.Lunartique, ModFormatKind.Carra2);
        Assert.True(toCarra.Supported);
        Assert.True(toCarra2.Supported);
        Assert.Contains("SerializedFile", toCarra.Reason);
    }

    [Theory]
    [InlineData(ModFormatKind.Bank, ModFormatKind.Rebank)]
    [InlineData(ModFormatKind.Rebank, ModFormatKind.Carra)]
    [InlineData(ModFormatKind.Carra, ModFormatKind.Bank)]
    [InlineData(ModFormatKind.Lunartique, ModFormatKind.Bank)]
    public void Cross_format_exports_without_a_pipeline_are_disabled(ModFormatKind source, ModFormatKind target)
    {
        var compatibility = ExportMatrix.Evaluate(source, target);
        Assert.False(compatibility.Supported);
        Assert.False(string.IsNullOrWhiteSpace(compatibility.Reason));
    }

    [Fact]
    public void Directory_sources_generate_all_mod_formats()
    {
        foreach (var target in new[] { ModFormatKind.Carra, ModFormatKind.Carra2, ModFormatKind.Rebank, ModFormatKind.Lunartique })
            Assert.True(ExportMatrix.Evaluate(ModFormatKind.Directory, target).Supported);
        Assert.False(ExportMatrix.Evaluate(ModFormatKind.Directory, ModFormatKind.Bank).Supported);
    }

    [Fact]
    public void Matrix_has_one_row_per_target_format()
    {
        Assert.Equal(5, ExportMatrix.MatrixFor(ModFormatKind.Carra).Count);
        Assert.Equal(5, ExportMatrix.MatrixFor(ModFormatKind.Unknown).Count);
        Assert.All(ExportMatrix.MatrixFor(ModFormatKind.Unknown), x => Assert.False(x.Supported));
    }

    [Fact]
    public void Guess_source_kind_maps_extensions()
    {
        Assert.Equal(ModFormatKind.Carra, ExportMatrix.GuessSourceKind(@"C:\x\mod.carra"));
        Assert.Equal(ModFormatKind.Rebank, ExportMatrix.GuessSourceKind(@"C:\x\mod.rebank"));
        Assert.Equal(ModFormatKind.Lunartique, ExportMatrix.GuessSourceKind(@"C:\x\mod.zip"));
        Assert.Equal(ModFormatKind.Unknown, ExportMatrix.GuessSourceKind(@"C:\x\mod.bin"));
    }
}
