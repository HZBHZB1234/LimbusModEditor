using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Rebank;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P3.1: the new-mod wizard scaffolds a project plus a minimal,
/// handler-validated template; templates the project cannot honestly
/// generate are explicitly unavailable.</summary>
public class NewModTemplateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-newmod-" + Guid.NewGuid().ToString("N"));

    public NewModTemplateServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Creates_carra2_project_and_valid_template()
    {
        var service = new NewModTemplateService(new ProjectService());
        var result = await service.CreateAsync(
            new NewModTemplateRequest("My Mod", "2.1", "tester", "desc", ModFormatKind.Carra2), _root);

        Assert.True(File.Exists(result.ProjectFile));
        Assert.True(File.Exists(result.TemplateFile));
        Assert.Equal(".carra2", Path.GetExtension(result.TemplateFile));

        // round-trip the template through the real reader
        await using var stream = File.OpenRead(result.TemplateFile);
        var imported = await new CarraFormatHandler().ImportAsync(stream, new ImportContext(_root));
        var payload = Assert.IsType<CarraPackage>(imported.Payload);
        Assert.Empty(payload.Entries);
    }

    [Fact]
    public async Task Creates_rebank_template_with_declared_base_bank()
    {
        var service = new NewModTemplateService(new ProjectService());
        var result = await service.CreateAsync(
            new NewModTemplateRequest("AudioMod", "1.0", "a", "d", ModFormatKind.Rebank, BaseBank: "common.bank"), _root);

        Assert.True(File.Exists(result.TemplateFile));
        var payload = RebankArchive.Read(File.OpenRead(result.TemplateFile));
        Assert.Equal("common.bank", payload.Metadata["base_bank"].GetString());
        Assert.Empty(payload.Files);

        var validation = await new RebankFormatHandler().ValidateAsync(
            new ModPackage { SourceFormat = ModFormatKind.Rebank, Payload = payload });
        Assert.True(validation.IsValid);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Code == "REBANK_BASE");
    }

    [Fact]
    public async Task Rebank_requires_base_bank_and_unknown_templates_are_refused()
    {
        var service = new NewModTemplateService(new ProjectService());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            new NewModTemplateRequest("X", "1", "a", "d", ModFormatKind.Rebank), _root));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateAsync(
            new NewModTemplateRequest("X", "1", "a", "d", ModFormatKind.Lunartique), _root));
    }

    [Fact]
    public void Lunartique_template_is_listed_but_disabled()
    {
        var templates = NewModTemplateService.SupportedTemplates();
        var lunartique = templates.Single(t => t.Kind == ModFormatKind.Lunartique);
        Assert.NotNull(lunartique.UnavailableReason);
        Assert.Contains("导入", lunartique.UnavailableReason);
    }
}
