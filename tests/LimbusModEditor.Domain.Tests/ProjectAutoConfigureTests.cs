using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>无感自动化：缺失目录自动从真实安装定位；用户已设置的值不被覆盖；
/// FMOD DLL 目录从不自动填写。真实机器门控测试在本机真实安装上验证。</summary>
public class ProjectAutoConfigureTests
{
    [Fact]
    public void Fills_missing_directories_from_the_real_install_on_this_machine()
    {
        var realGame = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        if (!File.Exists(Path.Combine(realGame, GameDirectoryLocator.GameExecutable))) return;

        var project = new ModProject(); // everything missing
        var report = ProjectAutoConfigureService.Apply(project);

        Assert.True(report.Any);
        Assert.True(report.GameDirectoryFilled);
        Assert.True(File.Exists(Path.Combine(project.GameDirectory!, GameDirectoryLocator.GameExecutable)));
        // cache and mods come from the real machine layouts when present
        if (report.UnityCacheDirectoryFilled)
            Assert.True(Directory.Exists(project.UnityCacheDirectory));
        if (report.ModDirectoryFilled)
            Assert.True(Directory.Exists(project.ModDirectory));
    }

    [Fact]
    public void Never_overwrites_directories_the_user_already_set()
    {
        var project = new ModProject { GameDirectory = @"C:\custom\game" };
        var report = ProjectAutoConfigureService.Apply(project);
        Assert.Equal(@"C:\custom\game", project.GameDirectory);
        Assert.False(report.GameDirectoryFilled);
    }

    [Fact]
    public void Fmod_directory_is_never_auto_filled()
    {
        var project = new ModProject();
        ProjectAutoConfigureService.Apply(project);
        Assert.Null(project.FmodLibraryDirectory);
    }
}
