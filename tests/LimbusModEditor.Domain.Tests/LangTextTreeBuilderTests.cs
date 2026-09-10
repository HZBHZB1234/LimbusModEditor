using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-10：文本工作台浏览树的纯函数构造器单测
/// （目录层级 / 根级文件与子目录并存 / 排序口径 / 惰性展开）。
/// </summary>
public class LangTextTreeBuilderTests
{
    private static readonly string[] Files =
    [
        "config.json",
        "LLc-CN-LCTA/AbDlg_Faust.json",
        "LLc-CN-LCTA/AbDlg_DonQuixote.json",
        "LLc-CN-LCTA/arr.json",
        "LLc-CN-LCTA/StoryData/S1.json",
        "LLc-CN-LCTA/StoryData/S2.json",
        "LLc-CN-LCTA/BattleAnnouncerDlg/Voice.json",
    ];

    [Fact]
    public void Build_groups_files_into_folder_hierarchy_with_config_first()
    {
        var root = LangTextTreeBuilder.Build(Files, "lang");

        Assert.Equal(LangTextTreeNodeKind.Root, root.Kind);
        Assert.True(root.IsFolder);
        Assert.False(root.IsFile);

        // 目录在前、文件在后，各自字典序（config.json 因此在最前）。
        Assert.Equal(
            ["LLc-CN-LCTA", "config.json"],
            root.Children.Select(x => x.Name));
        Assert.True(root.Children[0].IsFolder);
        Assert.True(root.Children[1].IsFile);
        Assert.Equal(string.Empty, root.RelativePath);
        Assert.Equal("config.json", root.Children[1].RelativePath);
    }

    [Fact]
    public void Build_keeps_root_level_files_and_subdirectories_together()
    {
        var root = LangTextTreeBuilder.Build(Files, "lang");
        var language = root.Children.Single(x => x.Name == "LLc-CN-LCTA");

        Assert.Equal(
            ["BattleAnnouncerDlg", "StoryData", "AbDlg_DonQuixote.json", "AbDlg_Faust.json", "arr.json"],
            language.Children.Select(x => x.Name));
        Assert.Equal("LLc-CN-LCTA", language.RelativePath);
        Assert.Equal("LLc-CN-LCTA/StoryData/S1.json",
            language.Children.Single(x => x.Name == "StoryData").Children.Single(x => x.Name == "S1.json").RelativePath);
    }

    [Fact]
    public void Build_accepts_backslashes_and_deduplicates()
    {
        var root = LangTextTreeBuilder.Build(
            ["LLC\\StoryData\\S1.json", "LLC/StoryData/S1.json", "", "   "], "lang");
        var story = root.Children.Single().Children.Single();
        Assert.Equal("StoryData", story.Name);
        Assert.Single(story.Children);
        Assert.Equal("LLC/StoryData/S1.json", story.Children[0].RelativePath);
    }

    [Fact]
    public void Expand_of_a_folder_returns_its_children_and_a_file_returns_nothing()
    {
        var root = LangTextTreeBuilder.Build(Files, "lang");
        var language = root.Children.Single(x => x.IsFolder);

        Assert.Equal(language.Children, LangTextTreeBuilder.Expand(language));
        var file = root.Children.Single(x => x.IsFile);
        Assert.Empty(LangTextTreeBuilder.Expand(file));
    }

    [Fact]
    public void Expand_by_relative_path_walks_the_hierarchy()
    {
        Assert.Equal(
            ["LLc-CN-LCTA", "config.json"],
            LangTextTreeBuilder.Expand(string.Empty, Files).Select(x => x.Name));

        Assert.Equal(
            ["BattleAnnouncerDlg", "StoryData", "AbDlg_DonQuixote.json", "AbDlg_Faust.json", "arr.json"],
            LangTextTreeBuilder.Expand("LLc-CN-LCTA", Files).Select(x => x.Name));

        Assert.Equal(
            ["S1.json", "S2.json"],
            LangTextTreeBuilder.Expand("LLc-CN-LCTA/StoryData", Files).Select(x => x.Name));

        Assert.Empty(LangTextTreeBuilder.Expand("不存在的目录", Files));
    }

    [Fact]
    public void Build_of_empty_list_yields_an_empty_root()
    {
        var root = LangTextTreeBuilder.Build([], "lang");
        Assert.Empty(root.Children);
        Assert.True(root.IsFolder);
    }
}
