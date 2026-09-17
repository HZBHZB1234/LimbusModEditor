using System.Text.Json;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 文本工作台「按 key 改」的契约测试（lang.fileEntries / lang.applyPatch 的底层）。
///
/// <para>关键契约：<b>applyPatch 只动给定键</b>——整份文本替换会逐条互相覆盖，
/// 所以这里逐条断言「其它键原样保留」；删不存在的键、删数组元素都<b>给结论而不是静默成功</b>。</para>
/// </summary>
public sealed class LangTextKeyEditTests
{
    private const string Body = """
        {
          "greeting": "你好",
          "dataList": [
            { "dialog": "第一句" },
            { "dialog": "第二句" }
          ]
        }
        """;

    [Fact]
    public void Entries_flattens_leaves_with_the_same_key_paths_as_search_hits()
    {
        var entries = LangTextWorkbenchService.Entries(Body);

        Assert.Equal(3, entries.Count);
        Assert.Equal("greeting", entries[0].KeyPath);
        Assert.Equal("你好", entries[0].Value);
        Assert.Equal("dataList/0/dialog", entries[1].KeyPath);
        Assert.Equal("第一句", entries[1].Value);
        Assert.Equal("dataList/1/dialog", entries[2].KeyPath);
    }

    [Fact]
    public void Entries_over_unreadable_text_returns_empty_instead_of_throwing()
    {
        Assert.Empty(LangTextWorkbenchService.Entries(null));
        Assert.Empty(LangTextWorkbenchService.Entries("  "));
        Assert.Empty(LangTextWorkbenchService.Entries("不是 JSON"));
    }

    [Fact]
    public void Apply_edits_touches_only_the_given_key()
    {
        var outcome = LangTextWorkbenchService.ApplyEdits(Body, [new LangKeyEdit("greeting", "再见")]);

        var applied = Assert.Single(outcome.Applied);
        Assert.Equal("greeting", applied);
        Assert.Empty(outcome.Missing);
        Assert.Empty(outcome.Rejected);

        using var document = JsonDocument.Parse(outcome.Text);
        Assert.Equal("再见", document.RootElement.GetProperty("greeting").GetString());
        // 其它内容原样保留（这正是「按 key 写回」与「整份替换」的区别）
        Assert.Equal("第一句", document.RootElement.GetProperty("dataList")[0].GetProperty("dialog").GetString());
        Assert.Equal("第二句", document.RootElement.GetProperty("dataList")[1].GetProperty("dialog").GetString());
    }

    [Fact]
    public void Apply_edits_reaches_into_array_items()
    {
        var outcome = LangTextWorkbenchService.ApplyEdits(Body, [new LangKeyEdit("dataList/1/dialog", "改过的第二句")]);

        Assert.Equal(["dataList/1/dialog"], outcome.Applied);
        using var document = JsonDocument.Parse(outcome.Text);
        Assert.Equal("第一句", document.RootElement.GetProperty("dataList")[0].GetProperty("dialog").GetString());
        Assert.Equal("改过的第二句", document.RootElement.GetProperty("dataList")[1].GetProperty("dialog").GetString());
    }

    [Fact]
    public void Apply_edits_with_a_null_value_deletes_the_key()
    {
        var outcome = LangTextWorkbenchService.ApplyEdits(Body, [new LangKeyEdit("greeting", null)]);

        Assert.Equal(["greeting"], outcome.Applied);
        using var document = JsonDocument.Parse(outcome.Text);
        Assert.False(document.RootElement.TryGetProperty("greeting", out _));
    }

    [Fact]
    public void Apply_edits_reports_missing_keys_instead_of_silently_ignoring_them()
    {
        var outcome = LangTextWorkbenchService.ApplyEdits(Body,
            [new LangKeyEdit("不存在的键", "x"), new LangKeyEdit("greeting", "再见")]);

        Assert.Equal(["greeting"], outcome.Applied);
        Assert.Equal(["不存在的键"], outcome.Missing);
        using var document = JsonDocument.Parse(outcome.Text);
        Assert.Equal("再见", document.RootElement.GetProperty("greeting").GetString());
    }

    [Fact]
    public void Apply_edits_refuses_to_delete_an_array_item()
    {
        // 删数组元素会让后面所有下标前移：页面上「同一个 key」下次就指到别的条目——宁可拒绝。
        var outcome = LangTextWorkbenchService.ApplyEdits(Body,
            [new LangKeyEdit("dataList/0", null), new LangKeyEdit("greeting", "再见")]);

        Assert.Equal(["dataList/0"], outcome.Rejected);
        Assert.Equal(["greeting"], outcome.Applied);
    }

    [Fact]
    public void Apply_edits_never_invents_a_key_that_is_not_in_the_file()
    {
        var outcome = LangTextWorkbenchService.ApplyEdits(Body, [new LangKeyEdit("新键", "新值")]);

        Assert.Equal(["新键"], outcome.Missing);
        Assert.Empty(outcome.Applied);
        using var document = JsonDocument.Parse(outcome.Text);
        Assert.False(document.RootElement.TryGetProperty("新键", out _));
    }
}
