using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Limbus Mod Editor CLI");
    Console.WriteLine("  probe <package>                         Detect package format");
    Console.WriteLine("  import <project.lmeproj> <file-or-dir>   Import package, bundle or directory");
    Console.WriteLine("  export <project.lmeproj> <output> [src]  Export project edits");
    return;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "probe" when args.Length >= 2:
            await ProbeAsync(args[1]);
            break;
        case "import" when args.Length >= 3:
            await ImportAsync(args[1], args[2]);
            break;
        case "export" when args.Length >= 3:
            await ExportAsync(args[1], args[2], args.Length >= 4 ? args[3] : null);
            break;
        default:
            Console.Error.WriteLine("Invalid arguments. Run 'help' for usage.");
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task ProbeAsync(string path)
{
    await using var input = File.OpenRead(Path.GetFullPath(path));
    var result = await BuiltInFormatRegistry.Create().ProbeAsync(input, Path.GetFileName(path));
    Console.WriteLine(result is null ? "Unknown" : $"{result.Format}: {result.Confidence}% - {result.Description}");
}

static async Task ImportAsync(string projectFile, string inputPath)
{
    var projects = new ProjectService();
    var project = await projects.LoadAsync(projectFile);
    var importer = new ModImportService(BuiltInFormatRegistry.Create());
    var result = Directory.Exists(inputPath)
        ? await importer.ImportDirectoryIntoProjectAsync(inputPath, project)
        : Path.GetExtension(inputPath).Equals(".bundle", StringComparison.OrdinalIgnoreCase)
            ? await importer.ImportUnityBundleIntoProjectAsync(inputPath, project)
            : await importer.ImportIntoProjectAsync(inputPath, project);
    await projects.SaveAsync(project, projectFile);
    Console.WriteLine($"Imported {result.Format}: added={result.AddedAssets}, updated={result.UpdatedAssets}");
}

static async Task ExportAsync(string projectFile, string outputPath, string? source)
{
    var project = await new ProjectService().LoadAsync(projectFile);
    var result = await new ModExportService(BuiltInFormatRegistry.Create())
        .ExportWithEditsAsync(source, project, outputPath);
    Console.WriteLine($"Exported {result.Format}: {result.OutputPath}, replacements={result.AppliedReplacements}");
}
