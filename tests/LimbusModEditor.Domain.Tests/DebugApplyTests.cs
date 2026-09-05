using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public class DebugApplyTests
{
    [Fact]
    public async Task ApplyAndRestoreRoundTripFiles()
    {
        var root = Directory.CreateTempSubdirectory("lme-test-");
        try
        {
            var overlay = Directory.CreateDirectory(Path.Combine(root.FullName, "overlay"));
            var game = Directory.CreateDirectory(Path.Combine(root.FullName, "game"));
            await File.WriteAllTextAsync(Path.Combine(game.FullName, "existing.txt"), "original");
            await File.WriteAllTextAsync(Path.Combine(overlay.FullName, "existing.txt"), "modified");
            await File.WriteAllTextAsync(Path.Combine(overlay.FullName, "new.txt"), "new");
            var project = new ModProject { SourceDirectory = root.FullName };
            var service = new DebugApplyService();
            var session = await service.ApplyAsync(project, overlay.FullName, game.FullName);
            Assert.Equal("modified", await File.ReadAllTextAsync(Path.Combine(game.FullName, "existing.txt")));
            Assert.True(File.Exists(Path.Combine(game.FullName, "new.txt")));
            await service.RestoreAsync(session);
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(game.FullName, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(game.FullName, "new.txt")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ConsecutiveSessionsUseDistinctBackupDirectories()
    {
        var root = Directory.CreateTempSubdirectory("lme-test-");
        try
        {
            var overlay = Directory.CreateDirectory(Path.Combine(root.FullName, "overlay"));
            var game = Directory.CreateDirectory(Path.Combine(root.FullName, "game"));
            await File.WriteAllTextAsync(Path.Combine(game.FullName, "a.txt"), "before");
            await File.WriteAllTextAsync(Path.Combine(overlay.FullName, "a.txt"), "after");
            var project = new ModProject { SourceDirectory = root.FullName };
            var service = new DebugApplyService();
            var first = await service.ApplyAsync(project, overlay.FullName, game.FullName);
            await service.RestoreAsync(first);
            var second = await service.ApplyAsync(project, overlay.FullName, game.FullName);
            Assert.NotEqual(first.BackupDirectory, second.BackupDirectory);
            await service.RestoreAsync(second);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteFilesChangedAfterApply()
    {
        var root = Directory.CreateTempSubdirectory("lme-test-");
        try
        {
            var overlay = Directory.CreateDirectory(Path.Combine(root.FullName, "overlay"));
            var game = Directory.CreateDirectory(Path.Combine(root.FullName, "game"));
            var target = Path.Combine(game.FullName, "a.txt");
            await File.WriteAllTextAsync(target, "before");
            await File.WriteAllTextAsync(Path.Combine(overlay.FullName, "a.txt"), "editor");
            var project = new ModProject { SourceDirectory = root.FullName };
            var service = new DebugApplyService();
            var session = await service.ApplyAsync(project, overlay.FullName, game.FullName);
            await File.WriteAllTextAsync(target, "changed by game");
            await service.RestoreAsync(session);
            Assert.Equal("changed by game", await File.ReadAllTextAsync(target));
            Assert.Contains(target, session.RestoreConflicts);
        }
        finally { root.Delete(true); }
    }
}
