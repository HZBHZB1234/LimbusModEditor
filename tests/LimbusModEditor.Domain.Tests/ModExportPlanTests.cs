using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-16 S3：导出**槽位契约**与**计划服务**。
///
/// <para>用户口径：选一个目录 → 分析全部修改 → 「种类 → 格式」两层文件夹下放产物。
/// 本类把两件事钉死：① 目录/命名口径（<see cref="ExportLayout"/>）；
/// ② 计划服务只按「真的改了什么」点亮槽位（没改的种类/格式一个都不许出现）。</para>
/// </summary>
public sealed class ModExportPlanTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-exportplan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响结论 */ }
    }

    // ── 槽位契约 ─────────────────────────────────────────────────────

    [Fact]
    public void Layout_has_two_levels_and_the_expected_folder_names()
    {
        Assert.Equal(ExportGroup.Fmod, ExportLayout.For(ExportSlot.Bank).Group);
        Assert.Equal("bank", ExportLayout.For(ExportSlot.Bank).FolderName);
        Assert.Equal("rebank", ExportLayout.For(ExportSlot.Rebank).FolderName);
        Assert.Equal("carra", ExportLayout.For(ExportSlot.Carra).FolderName);
        Assert.Equal("lunartique", ExportLayout.For(ExportSlot.Lunartique).FolderName);
        Assert.Equal("bus", ExportLayout.For(ExportSlot.LangBus).FolderName);
        Assert.Equal("patch", ExportLayout.For(ExportSlot.LangPatch).FolderName);
        Assert.Equal("pathset", ExportLayout.For(ExportSlot.LangPathset).FolderName);
        Assert.Equal("staticmod", ExportLayout.For(ExportSlot.StaticMod).FolderName);

        // 种类目录名 = <项目名>_<种类>
        Assert.Equal("MyMod_fmod", ExportLayout.GroupFolder(ExportGroup.Fmod, "MyMod"));
        Assert.Equal("MyMod_data", ExportLayout.GroupFolder(ExportGroup.Data, "MyMod"));
        Assert.Equal("MyMod_text", ExportLayout.GroupFolder(ExportGroup.Text, "MyMod"));
        Assert.Equal("MyMod_static", ExportLayout.GroupFolder(ExportGroup.Static, "MyMod"));

        // 产物相对路径 = <项目名>_<种类>/<格式>/<文件名>
        Assert.Equal(
            Path.Combine("MyMod_fmod", "bank", "1D101A.assets.bank"),
            ExportLayout.RelativeOutputPath(ExportGroup.Fmod, ExportSlot.Bank, "MyMod", "1D101A.assets.bank"));

        // 槽位与种类必须对得上（写错分组要在计划阶段就炸，而不是产出一个错目录）
        Assert.Throws<ArgumentException>(() => ExportLayout.FolderName(ExportGroup.Data, ExportSlot.Bank));
    }

    [Fact]
    public void Layout_sanitizes_illegal_file_name_characters()
    {
        Assert.Equal("My_Mod", ExportLayout.Sanitize("My:Mod"));
        Assert.Equal("LME", ExportLayout.Sanitize("   "));
    }

    [Fact]
    public void Debug_overwrite_patch_flag_matches_the_agreed_semantics()
    {
        // 用户答复：bank / 资源 patch 走「导出成 __data 与 .bank 后备份覆盖」；
        // lang 与 staticmod 走「标准 patch」。
        Assert.True(ExportLayout.For(ExportSlot.Bank).DebugOverwritePatch);
        Assert.True(ExportLayout.For(ExportSlot.Rebank).DebugOverwritePatch);
        Assert.True(ExportLayout.For(ExportSlot.Carra).DebugOverwritePatch);
        Assert.True(ExportLayout.For(ExportSlot.Lunartique).DebugOverwritePatch);
        Assert.False(ExportLayout.For(ExportSlot.LangBus).DebugOverwritePatch);
        Assert.False(ExportLayout.For(ExportSlot.LangPatch).DebugOverwritePatch);
        Assert.False(ExportLayout.For(ExportSlot.LangPathset).DebugOverwritePatch);
        Assert.False(ExportLayout.For(ExportSlot.StaticMod).DebugOverwritePatch);
    }

    // ── 计划：只点亮真的改了东西的槽位 ────────────────────────────────

    [Fact]
    public void Plan_marks_only_slots_that_have_source_changes()
    {
        var project = new ModProject { Name = "环指回调" };
        var lang = new LangEditSession();
        var statics = new StaticEditSession();

        var plan = new ModExportPlanService().Plan(project, _work, lang, statics);

        Assert.Equal(0, plan.PlannedSlotCount);
        Assert.Equal("环指回调", plan.ModName);
        Assert.All(plan.Items, item =>
        {
            Assert.False(item.Planned);
            Assert.False(string.IsNullOrWhiteSpace(item.SkipReason));
        });
    }

    [Fact]
    public void Plan_lights_up_fmod_bank_and_rebank_for_audio_edits()
    {
        var project = new ModProject { Name = "Mod" };
        var bankCopy = Path.Combine(_work, "sources", "banks", "1D101A.assets.bank");
        Directory.CreateDirectory(Path.GetDirectoryName(bankCopy)!);
        File.WriteAllBytes(bankCopy, [1, 2, 3]);
        var replacement = Path.Combine(_work, "0.wav");
        File.WriteAllBytes(replacement, [4, 5, 6]);
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "fsb/3",
            ContainerPath = "fsb/3",
            SourcePath = bankCopy,
            Type = AssetType.Audio,
            Bundle = "1D101A.assets.bank",
            Metadata =
            {
                ["bankSource"] = "1D101A.assets.bank",
                ["replacementPath"] = replacement,
            },
        });

        // rebank 槽位需要 FMOD 解码逐样本 WAV（加载器按样本名匹配）：给一个存在的目录即可通过计划门。
        var fmodDirectory = Path.Combine(_work, "fmod");
        Directory.CreateDirectory(fmodDirectory);
        var plan = new ModExportPlanService().Plan(project, _work, new LangEditSession(), new StaticEditSession(),
            new ModExportPlanContext(FmodDirectory: fmodDirectory));

        var bank = plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Bank);
        var rebank = plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        Assert.True(bank.Planned);
        Assert.Equal(1, bank.ArtifactCount);
        Assert.Equal(Path.Combine(_work, "Mod_fmod", "bank"), bank.Directory);
        Assert.True(rebank.Planned);
        Assert.Equal(Path.Combine(_work, "Mod_fmod", "rebank"), rebank.Directory);

        // 音频改动不点亮资源 / 文本 / 静态槽位。
        Assert.False(plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Carra).Planned);
        Assert.False(plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.LangPatch).Planned);
        Assert.False(plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.StaticMod).Planned);

        // 目标 bank 名与产物基名（加载器按文件名整包替换）。
        var edit = Assert.Single(plan.Banks);
        Assert.Equal("1D101A.assets.bank", edit.TargetBankFileName);
        Assert.Equal("1D101A.assets", edit.BaseName);
        Assert.Equal([3], edit.FsbIndexes);

        Assert.Equal(["Mod_fmod"], plan.PlannedGroupFolders);
    }

    [Fact]
    public void Plan_skips_audio_slots_when_the_original_bank_copy_is_missing()
    {
        var project = new ModProject { Name = "Mod" };
        var asset = new AssetRecord
        {
            LogicalPath = "fsb/1",
            SourcePath = Path.Combine(_work, "missing.bank"),
            Type = AssetType.Audio,
            Metadata =
            {
                ["bankSource"] = "x.bank",
                ["replacementPath"] = Path.Combine(_work, "1.wav"),
            },
        };
        project.Assets.Add(asset);

        var plan = new ModExportPlanService().Plan(project, _work, new LangEditSession(), new StaticEditSession());
        // 定位不到原版 bank = 无法生成整包/差分：明确不点亮（而不是产出一个加载器不认的包）。
        Assert.Empty(plan.Banks);
        Assert.False(plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Bank).Planned);
    }

    [Fact]
    public void Plan_lights_up_text_slots_for_lang_edits_and_keeps_the_loader_key()
    {
        var project = new ModProject { Name = "Mod" };
        var lang = new LangEditSession();
        var langRoot = Path.Combine(_work, "LimbusCompany_Data", "lang");
        var languageDirectory = Path.Combine(langRoot, "LLc-CN-LCTA");
        Directory.CreateDirectory(languageDirectory);
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLc-CN-LCTA"}""");
        var file = Path.Combine(languageDirectory, "AbDlg_Faust.json");
        File.WriteAllText(file, """{"dataList":[{"id":1,"dialog":"原"}]}""");
        lang.AttachLangRoot(langRoot);
        lang.BeginEdit("AbDlg_Faust.json");
        lang.SetModified("AbDlg_Faust.json", """{"dataList":[{"id":1,"dialog":"改"}]}""");

        var plan = new ModExportPlanService().Plan(project, _work, lang, new StaticEditSession());

        foreach (var slot in new[] { ExportSlot.LangBus, ExportSlot.LangPatch, ExportSlot.LangPathset })
        {
            var item = plan.Items.Single(x => x.Descriptor.Slot == slot);
            Assert.True(item.Planned, $"{slot} 应该被点亮");
            Assert.Equal(1, item.ArtifactCount);
        }
        var entry = Assert.Single(plan.LangEntries);
        Assert.Equal("AbDlg_Faust.json", entry.RelativePath);          // 条目口径（界面 / 索引）
        Assert.Equal("LLc-CN-LCTA/AbDlg_Faust.json", entry.PatchKey);  // 加载器口径（导出键）
    }

    [Fact]
    public void Plan_ignores_edits_that_end_up_identical()
    {
        var project = new ModProject { Name = "Mod" };
        var lang = new LangEditSession();
        var langRoot = Path.Combine(_work, "LimbusCompany_Data", "lang");
        Directory.CreateDirectory(Path.Combine(langRoot, "LLC_zh-CN"));
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLC_zh-CN"}""");
        var original = """{"k":"v"}""";
        File.WriteAllText(Path.Combine(langRoot, "LLC_zh-CN", "a.json"), original);
        lang.AttachLangRoot(langRoot);
        lang.BeginEdit("a.json");
        lang.SetModified("a.json", original); // 改了但内容没变

        var plan = new ModExportPlanService().Plan(project, _work, lang, new StaticEditSession());
        Assert.Empty(plan.LangEntries);
        Assert.False(plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.LangPatch).Planned);
    }

    // ── 会话（S3 的状态归属）─────────────────────────────────────────

    [Fact]
    public void Lang_session_exposes_revision_and_snapshot_with_patch_keys()
    {
        var langRoot = Path.Combine(_work, "LimbusCompany_Data", "lang");
        Directory.CreateDirectory(Path.Combine(langRoot, "LLC_zh-CN"));
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLC_zh-CN"}""");
        File.WriteAllText(Path.Combine(langRoot, "LLC_zh-CN", "a.json"), """{"k":"v"}""");

        var session = new LangEditSession();
        var changed = 0;
        session.Changed += (_, _) => changed++;
        session.AttachLangRoot(langRoot);
        Assert.Equal(0, session.Revision);

        session.BeginEdit("a.json");                 // 只读基线不算改动
        Assert.Equal(0, session.Revision);
        session.SetModified("a.json", """{"k":"w"}""");
        Assert.Equal(1, session.Revision);
        Assert.Equal(1, changed);
        session.SetModified("a.json", """{"k":"w"}"""); // 再次写入同样内容也计一次（页面无法区分）
        Assert.Equal(2, session.Revision);
        Assert.True(session.Revert("a.json"));
        Assert.Equal(3, session.Revision);
        Assert.False(session.Revert("a.json"));       // 已不在编辑集：不再触发
        Assert.Equal(3, session.Revision);
        Assert.Empty(session.EditedFiles);
    }

    [Fact]
    public void Static_session_snapshot_drops_identical_entries()
    {
        var session = new StaticEditSession();
        var entry = new LimbusModEditor.Application.StaticMods.StaticTableEntry(
            "assets/x/mission.json", "mission", "mission", "walpu8-mission.json", "static", 1, 10, true);
        session.Set("k1", entry, """{"a":1}""", """{"a":2}""");
        session.Set("k2", entry, """{"a":1}""", """{"a":1}"""); // 无差异

        var snapshot = session.Snapshot();
        var single = Assert.Single(snapshot);
        Assert.Equal("k1", single.Key);
        Assert.Equal(2, session.EntryCount); // 编辑集仍然记着两条（页面状态），快照只给有差异的
        Assert.Equal("""{"a":1}""", session.TryGetOfficialText("k1"));
    }
}
