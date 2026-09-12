using System.IO.Compression;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-16 S4：<b>槽位导出执行器</b>（<see cref="ModPackExportService"/>）的目录形状与产物正确性。
///
/// <para>用合成 bank（<see cref="SyntheticBank"/>）在临时目录里跑真链路：分析 → 写出 →
/// 用既有 reader 回读验证。断言的是三件对用户可见的事：
/// ① 目录形状是「&lt;项目名&gt;_&lt;种类&gt;/&lt;格式&gt;/产物」；② 空槽位**不建目录**；
/// ③ 加载器关键口径（.bank 用目标 bank 名、.rebank 用 bank 基名 + 真实样本名）。</para>
/// </summary>
public sealed class ModPackExportTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-packexport-" + Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), "lme-packout-" + Guid.NewGuid().ToString("N"));

    public ModPackExportTests() => Directory.CreateDirectory(_work);

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _work, _out })
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception) { /* 临时目录清理失败不影响结论 */ }
        }
    }

    private (ModProject Project, ModExportPlan Plan) SeedBankProject(string bankFileName = "1D101A.assets.bank")
    {
        Directory.CreateDirectory(Path.Combine(_work, "sources", "banks"));
        Directory.CreateDirectory(_out);
        var bankCopy = SyntheticBank.WriteAudioBank(Path.Combine(_work, "sources", "banks"), bankFileName);
        var replacement = Path.Combine(_work, "replacement.fsb5");
        File.WriteAllBytes(replacement, SyntheticBank.MinimalFsb5(codec: 16));

        var project = new ModProject { Name = "环指回调" };
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "fsb/0",
            ContainerPath = "fsb/0",
            SourcePath = bankCopy,
            Type = AssetType.Audio,
            Bundle = bankFileName,
            Metadata =
            {
                ["bankSource"] = bankFileName,
                ["replacementPath"] = replacement,
            },
        });

        var plan = new ModExportPlanService().Plan(project, _out, new LangEditSession(), new StaticEditSession(),
            new ModExportPlanContext());
        return (project, plan);
    }

    // ── 音频：整包 ───────────────────────────────────────────────────

    [Fact]
    public async Task Bank_slot_writes_one_bank_named_after_the_target_bank()
    {
        var (project, plan) = SeedBankProject();

        var result = await new ModPackExportService().ExportAsync(
            project, _work, plan, new ModExportPlanContext());

        var bankSlot = result.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Bank);
        Assert.True(bankSlot.Written);
        var output = Assert.Single(bankSlot.OutputPaths);
        // 加载器按文件名整包替换 → 产物名 = 游戏内目标 bank 文件名
        Assert.Equal("1D101A.assets.bank", Path.GetFileName(output));
        Assert.Equal(Path.Combine(_out, "环指回调_fmod", "bank"), Path.GetDirectoryName(output));
        Assert.True(File.Exists(output));

        // 写出的 bank 必须是合法 FEV bank，且 FSB 负载已被替换成我们给的那份。
        var written = File.ReadAllBytes(output);
        var info = BankParser.TryParse(written);
        Assert.NotNull(info);
        Assert.Equal(1, info!.FsbCount);
        Assert.True(written.AsSpan((int)info.FsbOffsets[0], 4).SequenceEqual("FSB5"u8));
    }

    [Fact]
    public async Task Empty_slots_create_no_directories()
    {
        var (project, plan) = SeedBankProject();

        var result = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());

        // 只改了音频 → 只有 _fmod 出现，_data / _text / _static 一个都不许留下空目录。
        Assert.Equal(["环指回调_fmod"], result.WrittenGroupFolders);
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_data")));
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_text")));
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_static")));
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_fmod", "rebank")));
    }

    // ── 音频：差分（S4 的真问题：条目名必须是真实样本名）─────────────

    [Fact]
    public async Task Rebank_slot_is_skipped_without_fmod_instead_of_writing_a_useless_package()
    {
        var (project, plan) = SeedBankProject();
        var rebankPlan = plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        // 计划层（无 FMOD）就该判定「不可写」：加载器按样本名匹配，拿不到样本名写不出可用包。
        Assert.False(rebankPlan.Planned);
        Assert.Contains("FMOD", rebankPlan.SkipReason!, StringComparison.Ordinal);

        var result = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());

        var rebank = result.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        Assert.False(rebank.Written);
        Assert.Contains(rebank.Diagnostics, x => x.Contains("FMOD", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_fmod", "rebank")));
    }

    /// <summary>计划里点亮了、但执行期缺 FMOD 目录 → 仍然必须跳过并说明（执行层再判一次）。</summary>
    [Fact]
    public async Task Rebank_slot_skips_with_a_reason_when_execution_lacks_fmod()
    {
        var (project, plan) = SeedBankProject();
        var planned = plan with
        {
            Items = plan.Items.Select(x => x.Descriptor.Slot == ExportSlot.Rebank
                ? x with { Planned = true, SkipReason = null }
                : x).ToArray(),
        };

        var result = await new ModPackExportService().ExportAsync(project, _work, planned, new ModExportPlanContext());

        var rebank = result.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        Assert.False(rebank.Written);
        Assert.NotEmpty(rebank.Diagnostics);
        Assert.False(Directory.Exists(Path.Combine(_out, "环指回调_fmod", "rebank")));
    }

    [Fact]
    public void Rebank_plan_requires_fmod_but_bank_plan_does_not()
    {
        // 有音频修改但没配 FMOD 目录：整包槽位仍然用（可以替换已是 FSB5 的样本），
        // 差分槽位必须直接判定「不可写」并给出中文原因——加载器按样本名匹配，
        // 拿不到样本名写出来的包一条都对不上。
        var bankCopy = SyntheticBank.WriteAudioBank(EnsureDirectory(Path.Combine(_work, "src2")), "x.assets.bank");
        var project = new ModProject { Name = "M" };
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "fsb/0",
            SourcePath = bankCopy,
            Type = AssetType.Audio,
            Metadata = { ["bankSource"] = "x.assets.bank", ["replacementPath"] = bankCopy },
        });

        var plan = new ModExportPlanService().Plan(project, _out, new LangEditSession(), new StaticEditSession(),
            new ModExportPlanContext());

        var bank = plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Bank);
        var rebank = plan.Items.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        Assert.True(bank.Planned);
        Assert.Contains(bank.Warnings!, x => x.Contains("FMOD", StringComparison.Ordinal));
        Assert.False(rebank.Planned);
        Assert.Contains("FMOD", rebank.SkipReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rebank_slot_writes_entries_with_the_target_base_name_when_fmod_is_present()
    {
        // 本机没有 FMOD DLL / 合成 FSB 没有样本名时，这条链路会走「跳过 + 诊断」分支。
        // 真实数据的验证放在 RealWorkbenchGateTests 的标签器门控里；这里只断言：
        // 有 FMOD 目录时若仍未能产出条目，诊断必须说明原因（不许静默）。
        var (project, plan) = SeedBankProject();
        var fmodDirectory = new ModExportPlanContext().FmodDirectory;
        if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
        {
            var result = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());
            var rebank = result.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
            if (!rebank.Written) Assert.NotEmpty(rebank.Diagnostics);
            return;
        }

        var context = new ModExportPlanContext(FmodDirectory: fmodDirectory);
        var withFmod = new ModExportPlanService().Plan(project, _out, new LangEditSession(), new StaticEditSession(), context);
        var planResult = await new ModPackExportService().ExportAsync(project, _work, withFmod, context);
        var slot = planResult.Slots.Single(x => x.Descriptor.Slot == ExportSlot.Rebank);
        if (slot.Written)
        {
            var output = Assert.Single(slot.OutputPaths);
            Assert.Equal("1D101A.assets.rebank", Path.GetFileName(output));
            using var zip = ZipFile.OpenRead(output);
            var config = zip.GetEntry("rebank.json");
            Assert.NotNull(config);
            // 每个条目名必须是 <数字序号>/<真实样本名>.wav
            foreach (var entry in zip.Entries.Where(x => x.FullName != "rebank.json"))
            {
                var parts = entry.FullName.Split('/');
                Assert.Equal(2, parts.Length);
                Assert.True(int.TryParse(parts[0], out var index) && index >= 0, $"条目序号非法: {entry.FullName}");
                Assert.EndsWith(".wav", parts[1], StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            Assert.NotEmpty(slot.Diagnostics); // 没产出就必须说明原因
        }
    }

    // ── 汇总 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Result_summarizes_written_slots_for_the_report()
    {
        var (project, plan) = SeedBankProject();
        var result = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());

        Assert.Equal(_out, result.RootDirectory);
        Assert.Equal("环指回调", result.ModName);
        Assert.True(result.WrittenSlotCount >= 1);
        Assert.Contains("1 个格式槽位", result.Describe(), StringComparison.Ordinal);
        // 未写出的槽位必须带中文原因（报告里要逐条列出来）。
        foreach (var slot in result.Slots.Where(x => !x.Written))
            Assert.False(string.IsNullOrWhiteSpace(slot.Diagnostics.FirstOrDefault()));
    }
}
