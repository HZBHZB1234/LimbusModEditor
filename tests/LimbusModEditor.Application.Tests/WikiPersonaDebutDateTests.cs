using System.Text.Json;
using LimbusModEditor.Application.Relations.WikiBaseData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 人格页「登场时间」取值（<see cref="PersonaBaseDataSections.FormatDate"/>）：
/// 静态表 <c>updatedDate</c> 是 <c>yyyymmdd</c> 整数，须落成 <c>yyyy.MM.dd</c>。
/// 覆盖：真实取值 20230227 → 2023.02.27；缺列 / 非数字 / 非法日期 → null（不占位、不外推）。
/// </summary>
public sealed class WikiPersonaDebutDateTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>浮士德 LCB 罪人：真实维基「登场时间 2023.02.27」。</summary>
    [Fact]
    public void FormatDate_yyyymmdd_integer_becomes_dotted_date()
    {
        var row = Row("""{"id":10201,"updatedDate":20230227}""");

        Assert.Equal("2023.02.27", PersonaBaseDataSections.FormatDate(row, "updatedDate"));
    }

    [Fact]
    public void FormatDate_keeps_zero_padded_month_and_day()
    {
        var row = Row("""{"updatedDate":20260805}""");

        Assert.Equal("2026.08.05", PersonaBaseDataSections.FormatDate(row, "updatedDate"));
    }

    /// <summary>缺列 / 非数字 / 月份或日期越界 → null（不占位、不编）。</summary>
    [Theory]
    [InlineData("""{"id":10201}""")]
    [InlineData("""{"updatedDate":"20230227"}""")]
    [InlineData("""{"updatedDate":20231327}""")]
    [InlineData("""{"updatedDate":20230231}""")]
    [InlineData("""{"updatedDate":0}""")]
    public void FormatDate_returns_null_for_missing_or_invalid(string json)
    {
        Assert.Null(PersonaBaseDataSections.FormatDate(Row(json), "updatedDate"));
    }
}
