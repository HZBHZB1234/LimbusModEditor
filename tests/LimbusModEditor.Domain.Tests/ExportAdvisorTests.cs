using LimbusModEditor.Application.Build;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>导出思路分析（P3.10）：按项目里实际改了什么给出出口建议，
/// 推荐项排最前；缺前置条件（游戏目录 / FMOD DLL）的建议要明确说原因。</summary>
public class ExportAdvisorTests
{
    private static AssetRecord UnityEdit(string containerEntry = "assets/ui/logo.png")
        => new()
        {
            LogicalPath = "outerA/innerA/CAB-a/10.28",
            ContainerPath = "CAB-a",
            UnityPathId = 10,
            Type = AssetType.Texture,
            EditState = AssetEditState.Modified,
            Metadata =
            {
                ["reference"] = "true",
                ["containerEntry"] = containerEntry,
                ["replacementPath"] = @"C:\tmp\logo.png",
                ["unityBundle"] = "true",
            },
        };

    private static AssetRecord BankAudioEdit()
        => new()
        {
            LogicalPath = "fsb/0",
            ContainerPath = "fsb/0",
            Type = AssetType.Audio,
            EditState = AssetEditState.Modified,
            Metadata = { ["replacementPath"] = @"C:\tmp\bgm.fsb" },
        };

    private static ModProject Project(params AssetRecord[] assets)
    {
        var project = new ModProject { Name = "AdvisorTest" };
        foreach (var asset in assets) project.Assets.Add(asset);
        return project;
    }

    [Fact]
    public void Empty_project_suggests_getting_resources_first()
    {
        var ideas = new ExportAdvisor().Analyze(Project());
        var first = ideas[0];
        Assert.Equal(ExportIdeaKind.ScanFirst, first.Kind);
        Assert.True(first.Recommended);
        Assert.True(first.Enabled);
        Assert.DoesNotContain(ideas, x => x.Kind == ExportIdeaKind.OneClickCarra2);
    }

    [Fact]
    public void Assets_without_edits_suggest_editing_before_exporting()
    {
        var asset = new AssetRecord { LogicalPath = "assets/ui/logo.png", Type = AssetType.Texture };
        var ideas = new ExportAdvisor().Analyze(Project(asset));
        var first = ideas[0];
        Assert.Equal(ExportIdeaKind.EditFirst, first.Kind);
        Assert.True(first.Recommended);
        Assert.Contains("1 个资源", first.Summary);
    }

    [Fact]
    public void Unity_edits_recommend_one_click_carra2()
    {
        var ideas = new ExportAdvisor().Analyze(Project(UnityEdit(), UnityEdit("assets/ui/icon.png")));
        var idea = Assert.Single(ideas, x => x.Kind == ExportIdeaKind.OneClickCarra2);
        Assert.True(idea.Recommended);
        Assert.Equal(2, idea.AssetCount);
        Assert.Equal(ExportIdeaKind.OneClickCarra2, ideas[0].Kind); // 推荐项排最前
        Assert.DoesNotContain(ideas, x => x.Kind == ExportIdeaKind.EditFirst);
    }

    [Fact]
    public void Audio_edits_get_a_bank_channel_and_no_false_carra2_recommendation()
    {
        var ideas = new ExportAdvisor().Analyze(Project(BankAudioEdit()), new ExportAdvisorContext(FmodAvailable: false));
        var audio = Assert.Single(ideas, x => x.Kind == ExportIdeaKind.AudioBank);
        Assert.True(audio.Enabled);
        Assert.False(audio.Recommended);
        Assert.Contains("FMOD", audio.Detail);
        Assert.DoesNotContain(ideas, x => x.Kind == ExportIdeaKind.OneClickCarra2);
    }

    [Fact]
    public void Registered_sources_unlock_wizard_and_multi_format()
    {
        var project = Project(UnityEdit());
        project.Sources.Add(new ProjectSource { DisplayName = "legacy", Path = @"C:\tmp\a.carra2" });
        var ideas = new ExportAdvisor().Analyze(project);
        Assert.Contains(ideas, x => x.Kind == ExportIdeaKind.ExportWizard && x.Enabled);
        Assert.Contains(ideas, x => x.Kind == ExportIdeaKind.MultiFormat && x.Enabled);
        // Unity 修改仍然是最推荐的出口。
        Assert.Equal(ExportIdeaKind.OneClickCarra2, ideas[0].Kind);
    }

    [Fact]
    public void Lang_and_debug_channels_require_a_game_directory()
    {
        var withoutGame = new ExportAdvisor().Analyze(Project(UnityEdit()), new ExportAdvisorContext());
        Assert.All(
            withoutGame.Where(x => x.Kind is ExportIdeaKind.LangText or ExportIdeaKind.DebugOverlay),
            x => { Assert.False(x.Enabled); Assert.NotNull(x.BlockedReason); });

        var withGame = new ExportAdvisor().Analyze(
            Project(UnityEdit()),
            new ExportAdvisorContext(GameDirectory: Path.GetTempPath()));
        Assert.All(
            withGame.Where(x => x.Kind is ExportIdeaKind.LangText or ExportIdeaKind.DebugOverlay),
            x => { Assert.True(x.Enabled); Assert.Null(x.BlockedReason); });
    }

    [Fact]
    public void Recommended_ideas_are_listed_before_optional_ones()
    {
        var project = Project(UnityEdit(), BankAudioEdit());
        project.Sources.Add(new ProjectSource { DisplayName = "legacy", Path = @"C:\tmp\a.carra2" });
        var ideas = new ExportAdvisor().Analyze(project, new ExportAdvisorContext(GameDirectory: Path.GetTempPath()));
        var lastRecommended = ideas.Select((x, i) => (x, i)).Where(t => t.x.Recommended).Max(t => t.i);
        var firstOptional = ideas.Select((x, i) => (x, i)).Where(t => !t.x.Recommended).Min(t => t.i);
        Assert.True(lastRecommended < firstOptional, "推荐项必须全部排在可选出口之前");
    }
}
