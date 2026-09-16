using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// plan-16 S7：调试应用（<see cref="ModApplyService"/>）——「使用当前修改启动游戏进行调试」的
/// 应用层语义与可逆性。
///
/// <para>全部在临时目录里跑，**不碰真实游戏目录**：断言的是
/// ① bank 走「备份 → 覆盖」、② lang 走「标准 patch（补丁 JSON 进 lang 目录）」、
/// ③ 关闭时逐字节还原、④ 目标被外部改过时不覆盖（护栏）、⑤ 不支持的格式明确跳过。</para>
/// </summary>
public sealed class ModApplyServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-apply-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _out;
    private readonly string _bankDirectory;

    public ModApplyServiceTests()
    {
        _game = Path.Combine(_work, "game");
        _out = Path.Combine(_work, "out");
        _bankDirectory = Path.Combine(_game, Path.Combine(ModApplyService.BankRelativePath));
        Directory.CreateDirectory(_bankDirectory);
        Directory.CreateDirectory(Path.Combine(_game, Path.Combine(ModApplyService.LangRelativePath)));
        Directory.CreateDirectory(Path.Combine(_work, "sources", "banks"));
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响结论 */ }
    }

    private async Task<(ModProject Project, ModPackExportResult Export)> SeedAudioExportAsync(
        string bankFileName = "1D101A.assets.bank")
    {
        // 游戏里放一个原始 bank（用合成 bank 保证是合法 FEV）。
        var original = File.ReadAllBytes(SyntheticBank.WriteAudioBank(_bankDirectory, bankFileName));
        var copyToEdit = Path.Combine(_work, "sources", "banks", bankFileName);
        File.Copy(Path.Combine(_bankDirectory, bankFileName), copyToEdit, overwrite: true);

        // 替换内容：另一份 FSB5（codec 不同 → 字节确实不同，便于断言「确实被改了」）。
        var replacement = Path.Combine(_work, "replacement.fsb5");
        File.WriteAllBytes(replacement, SyntheticBank.MinimalFsb5(codec: 17));

        var project = new ModProject { Name = "ApplyGate" };
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "fsb/0",
            SourcePath = copyToEdit,
            Type = AssetType.Audio,
            Metadata = { ["bankSource"] = bankFileName, ["replacementPath"] = replacement },
        });

        var plan = new ModExportPlanService().Plan(project, _out, new LangEditSession(), new StaticEditSession(),
            new ModExportPlanContext());
        var export = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());
        _ = original;
        return (project, export);
    }

    [Fact]
    public async Task Bank_patch_is_backed_up_overwritten_and_restored_byte_for_byte()
    {
        var (project, export) = await SeedAudioExportAsync();
        var target = Path.Combine(_bankDirectory, "1D101A.assets.bank");
        var before = await File.ReadAllBytesAsync(target);

        var apply = new ModApplyService();
        var report = await apply.ApplyAsync(project, export, _game, null, Path.Combine(_work, "backups"),
            new ModExportPlanContext());

        Assert.Equal(1, report.ChangedFileCount);
        var step = Assert.Single(report.Steps);
        Assert.Equal(ModApplyKind.FileOverwrite, step.Kind);
        Assert.Equal(target, step.TargetPath);
        Assert.NotNull(step.BackupPath);
        Assert.True(File.Exists(step.BackupPath));
        var applied = await File.ReadAllBytesAsync(target);
        Assert.False(before.SequenceEqual(applied), "应用后目标 bank 应该被覆盖");
        Assert.Equal(await File.ReadAllBytesAsync(export.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Bank).OutputPaths[0]), applied);

        // 关闭还原：逐字节回到原状
        var conflicts = await apply.RestoreAsync(report.BackupDirectory);
        Assert.Empty(conflicts);
        Assert.Equal(before, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Lang_patch_json_is_placed_into_the_game_lang_directory_and_removed_on_restore()
    {
        // 造一条文本修改并导出（走完整链路，顺带验证 patch 槽位的产物能被加载器读到）。
        var langRoot = Path.Combine(_game, Path.Combine(ModApplyService.LangRelativePath));
        Directory.CreateDirectory(Path.Combine(langRoot, "LLc-CN-LCTA"));
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLc-CN-LCTA"}""");
        var table = Path.Combine(langRoot, "LLc-CN-LCTA", "AbDlg_Faust.json");
        File.WriteAllText(table, """{"dataList":[{"id":1,"dialog":"原"}]}""");

        var session = new LangEditSession();
        session.AttachLangRoot(langRoot);
        session.BeginEdit("AbDlg_Faust.json");
        session.SetModified("AbDlg_Faust.json", """{"dataList":[{"id":1,"dialog":"改"}]}""");

        var project = new ModProject { Name = "LangApply" };
        var plan = new ModExportPlanService().Plan(project, _out, session, new StaticEditSession());
        var export = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());
        Assert.True(export.Slots.Single(x => x.Descriptor.Slot == ExportSlot.LangPatch).Written);

        var apply = new ModApplyService();
        var report = await apply.ApplyAsync(project, export, _game, null, Path.Combine(_work, "backups"),
            new ModExportPlanContext());

        // 补丁 JSON 落在 lang 目录下（加载器就是这么找的）；原文本表没被改。
        var placed = Path.Combine(langRoot, "AbDlg_Faust.json");
        Assert.True(File.Exists(placed));
        Assert.Contains("patchs", await File.ReadAllTextAsync(placed));
        Assert.Equal("""{"dataList":[{"id":1,"dialog":"原"}]}""", await File.ReadAllTextAsync(table));
        Assert.Contains(report.Steps, x => x.Kind == ModApplyKind.Added && x.TargetPath == placed);

        // 关闭还原：新增的补丁文件被删掉，原样世界
        var conflicts = await apply.RestoreAsync(report.BackupDirectory);
        Assert.Empty(conflicts);
        Assert.False(File.Exists(placed));
        Assert.True(File.Exists(table));
    }

    [Fact]
    public async Task Restore_refuses_to_overwrite_a_file_changed_by_someone_else()
    {
        var (project, export) = await SeedAudioExportAsync();
        var target = Path.Combine(_bankDirectory, "1D101A.assets.bank");

        var apply = new ModApplyService();
        var report = await apply.ApplyAsync(project, export, _game, null, Path.Combine(_work, "backups"),
            new ModExportPlanContext());

        // 模拟「应用之后别的程序又改了同一个文件」
        await File.WriteAllBytesAsync(target, "external change"u8.ToArray());

        var conflicts = await apply.RestoreAsync(report.BackupDirectory);
        var conflict = Assert.Single(conflicts);
        Assert.Equal(target, conflict);
        Assert.Equal("external change"u8.ToArray(), await File.ReadAllBytesAsync(target)); // 没被覆盖
    }

    [Fact]
    public async Task Unsupported_formats_are_listed_as_skipped_not_silently_dropped()
    {
        var (project, export) = await SeedAudioExportAsync();
        // 造一个「bus / pathset / staticmod 都写了」的结果：把音频槽位换掉，只留这三类。
        var faked = export with
        {
            Slots =
            [
                new ModPackSlotResult(ExportLayout.For(ExportSlot.LangBus), true, _out, 1, [Path.Combine(_out, "x.json")], [], []),
                new ModPackSlotResult(ExportLayout.For(ExportSlot.LangPathset), true, _out, 1, [Path.Combine(_out, "y.json")], [], []),
                new ModPackSlotResult(ExportLayout.For(ExportSlot.StaticMod), true, _out, 1, [Path.Combine(_out, "z.staticmod")], [], []),
            ],
        };

        var report = await new ModApplyService().ApplyAsync(project, faked, _game, null,
            Path.Combine(_work, "backups"), new ModExportPlanContext());

        Assert.Equal(0, report.ChangedFileCount);
        Assert.Equal(3, report.Skipped.Count);
        Assert.Contains(report.Skipped, x => x.Contains("catalog", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Skipped, x => x.Contains("bus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_target_bank_is_reported_and_nothing_is_written()
    {
        var (project, export) = await SeedAudioExportAsync();
        File.Delete(Path.Combine(_bankDirectory, "1D101A.assets.bank"));

        var report = await new ModApplyService().ApplyAsync(project, export, _game, null,
            Path.Combine(_work, "backups"), new ModExportPlanContext());

        Assert.Equal(0, report.ChangedFileCount);
        Assert.NotEmpty(report.Diagnostics);
        Assert.Contains(report.Diagnostics, x => x.Contains("1D101A.assets.bank", StringComparison.Ordinal));
    }
}
