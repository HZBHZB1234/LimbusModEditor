using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 文本目录树懒加载（text.fileTreeChildren 用的那棵树）的口径测试。
/// 数据源是 lang.files 同一套相对路径，这里直接喂路径集合，钉住「一次一层」。
/// </summary>
public sealed class LangFileTreeTests
{
    private static readonly string[] Paths =
    [
        "AbDlg_DonQuixote.json",
        "battle/BattleDlg_01.json",
        "battle/sub/BattleDlg_02.json",
        "ui/UIDlg_01.json",
    ];

    [Fact]
    public void Root_lists_directories_first_then_files()
    {
        var nodes = LangFileTree.ChildrenOf(Paths, null);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(["battle", "ui", "AbDlg_DonQuixote.json"], nodes.Select(x => x.Name));
        Assert.Equal([false, false, true], nodes.Select(x => x.IsLeaf));
        // battle 下有 sub 目录 + BattleDlg_01.json 两个直接子节点
        Assert.Equal(2, nodes[0].ChildCount);
        Assert.Equal(1, nodes[1].ChildCount);
        Assert.Equal(0, nodes[2].ChildCount);
    }

    [Fact]
    public void Subdirectory_lists_only_its_own_children()
    {
        var nodes = LangFileTree.ChildrenOf(Paths, "battle");

        Assert.Equal(2, nodes.Count);
        Assert.Equal(["sub", "BattleDlg_01.json"], nodes.Select(x => x.Name));
        Assert.Equal("battle/sub", nodes[0].Path);
        Assert.Equal("battle/BattleDlg_01.json", nodes[1].Path);
    }

    [Fact]
    public void A_leaf_directory_deeper_down_is_still_listed()
    {
        var nodes = LangFileTree.ChildrenOf(Paths, "battle/sub");

        var only = Assert.Single(nodes);
        Assert.Equal("BattleDlg_02.json", only.Name);
        Assert.True(only.IsLeaf);
    }

    /// <summary>不存在的父路径 → 空一层（不抛、不返回全部文件）。</summary>
    [Fact]
    public void An_unknown_parent_yields_an_empty_level()
    {
        Assert.Empty(LangFileTree.ChildrenOf(Paths, "no/such/dir"));
    }
}
