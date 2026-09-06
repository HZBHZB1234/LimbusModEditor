using System.IO;
using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

/// <summary>ModInstallService mirrors the real loader's conventions (LCTA
/// launcher: flat files or per-mod directories, "_disable" suffix toggling).
/// Tests exercise the rename contract on a temp layout that mirrors the real
/// machine's %APPDATA%\LimbusCompanyMods (carra2 file + disabled dir).</summary>
public class ModInstallServiceTests : IDisposable
{
    private readonly string _mods = Path.Combine(Path.GetTempPath(), "lme-mods-" + Guid.NewGuid().ToString("N"));

    public ModInstallServiceTests()
    {
        Directory.CreateDirectory(_mods);
        File.WriteAllText(Path.Combine(_mods, "92_Sora_Mod.carra2"), "x");
        Directory.CreateDirectory(Path.Combine(_mods, "Sora_Mod_disable"));
        File.WriteAllText(Path.Combine(_mods, "Sora_Mod_disable", "mod.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_mods, "SomeMod"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_mods, true); } catch (IOException) { }
    }

    [Fact]
    public void Lists_entries_with_disable_state()
    {
        var entries = ModInstallService.ListInstalled(_mods);
        Assert.Equal(3, entries.Count);
        var carra = entries.Single(x => x.Name == "92_Sora_Mod.carra2");
        Assert.False(carra.IsDisabled);
        Assert.False(carra.IsDirectory);
        var disabled = entries.Single(x => x.Name == "Sora_Mod_disable");
        Assert.True(disabled.IsDisabled);
        Assert.True(disabled.IsDirectory);
        Assert.Equal("Sora_Mod", disabled.EnabledName);
    }

    [Fact]
    public async Task Toggle_disables_and_reenables_by_rename_only()
    {
        var target = ModInstallService.ListInstalled(_mods).Single(x => x.Name == "SomeMod");
        var disabled = await Task.Run(() => ModInstallService.SetEnabled(_mods, target, enabled: false));
        Assert.True(disabled.IsDisabled);
        Assert.Equal("SomeMod_disable", disabled.Name);
        Assert.True(Directory.Exists(Path.Combine(_mods, "SomeMod_disable")));
        Assert.False(Directory.Exists(Path.Combine(_mods, "SomeMod")));

        var enabled = await Task.Run(() => ModInstallService.SetEnabled(_mods, disabled, enabled: true));
        Assert.False(enabled.IsDisabled);
        Assert.True(Directory.Exists(Path.Combine(_mods, "SomeMod")));
    }

    [Fact]
    public async Task Toggle_refuses_when_target_name_already_exists()
    {
        var target = ModInstallService.ListInstalled(_mods).Single(x => x.Name == "92_Sora_Mod.carra2");
        // create a colliding disabled name up front
        await File.WriteAllTextAsync(Path.Combine(_mods, "92_Sora_Mod.carra2_disable"), "y");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.Run(() => ModInstallService.SetEnabled(_mods, target, enabled: false)));
        Assert.Contains("已存在", ex.Message);
    }

    [Fact]
    public void Listing_requires_an_existing_directory()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ModInstallService.ListInstalled(Path.Combine(_mods, "no-such")));
    }
}
