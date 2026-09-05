using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Unity;
using System.IO.Compression;

namespace LimbusModEditor.Application.Assets;

public sealed record ImportResult(ModFormatKind Format, int AddedAssets, int UpdatedAssets, IReadOnlyList<string> PreservedFiles);

/// <summary>Imports a package through the registered format handler and merges
/// its neutral asset records into the current authoring project.</summary>
public sealed class ModImportService(FormatRegistry registry)
{
    public async Task<ImportResult> ImportSerializedFileIntoProjectAsync(
        string serializedFilePath, ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        ArgumentNullException.ThrowIfNull(project);
        var full = Path.GetFullPath(serializedFilePath);
        if (!File.Exists(full)) throw new FileNotFoundException("SerializedFile 不存在。", full);
        var sourcePath = await MaterializeSourceAsync(full, project, ModFormatKind.Directory, cancellationToken);
        var descriptors = new UnityAssetService().ScanSerializedFile(sourcePath, cancellationToken: cancellationToken);
        var added = 0; var updated = 0;
        foreach (var asset in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = project.Assets.FirstOrDefault(x => string.Equals(x.LogicalPath, asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                asset.Metadata["originalSourcePath"] = full;
                asset.Metadata["sourcePackagePath"] = sourcePath;
                project.Assets.Add(asset); added++;
            }
            else
            {
                existing.SourcePath = asset.SourcePath; existing.ContainerPath = asset.ContainerPath;
                existing.UnityPathId = asset.UnityPathId; existing.UnityTypeId = asset.UnityTypeId;
                existing.Type = asset.Type; existing.Size = asset.Size;
                foreach (var pair in asset.Metadata) existing.Metadata[pair.Key] = pair.Value;
                existing.Metadata["originalSourcePath"] = full; existing.Metadata["sourcePackagePath"] = sourcePath; updated++;
            }
        }
        return new(ModFormatKind.Directory, added, updated, []);
    }

    /// <summary>Inspects a UnityFS/UnityRaw bundle with AssetsTools.NET and
    /// merges its object index into the project. The original bundle is copied
    /// into sources so the project remains portable.</summary>
    public async Task<ImportResult> ImportUnityBundleIntoProjectAsync(
        string bundlePath, ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(project);
        var full = Path.GetFullPath(bundlePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Unity Bundle 不存在。", full);
        var sourcePath = await MaterializeSourceAsync(full, project, ModFormatKind.Directory, cancellationToken);
        var descriptors = new UnityAssetService().ScanBundle(sourcePath, cancellationToken: cancellationToken);
        var added = 0;
        var updated = 0;
        foreach (var asset in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = project.Assets.FirstOrDefault(x => string.Equals(x.LogicalPath, asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                asset.Metadata["originalSourcePath"] = full;
                asset.Metadata["sourcePackagePath"] = sourcePath;
                project.Assets.Add(asset);
                added++;
            }
            else
            {
                existing.SourcePath = asset.SourcePath;
                existing.ContainerPath = asset.ContainerPath;
                existing.Account = asset.Account;
                existing.Bundle = asset.Bundle;
                existing.UnityPathId = asset.UnityPathId;
                existing.UnityTypeId = asset.UnityTypeId;
                existing.Type = asset.Type;
                existing.Size = asset.Size;
                foreach (var pair in asset.Metadata) existing.Metadata[pair.Key] = pair.Value;
                existing.Metadata["originalSourcePath"] = full;
                existing.Metadata["sourcePackagePath"] = sourcePath;
                updated++;
            }
        }
        return new(ModFormatKind.Directory, added, updated, []);
    }

    public async Task<ImportResult> ImportDirectoryIntoProjectAsync(
        string directoryPath, ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(project);
        var source = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        var sourceRoot = string.IsNullOrWhiteSpace(project.SourceDirectory)
            ? Path.Combine(source, ".lme-sources")
            : Path.Combine(Path.GetFullPath(project.SourceDirectory), "directories");
        var destinationRoot = Path.Combine(sourceRoot, Path.GetFileName(source));
        Directory.CreateDirectory(destinationRoot);
        var added = 0;
        var updated = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFullPath(file).StartsWith(Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = File.OpenRead(file))
            await using (var output = File.Create(destination))
                await input.CopyToAsync(output, cancellationToken);
            var existing = project.Assets.FirstOrDefault(x => string.Equals(x.LogicalPath, relative, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                project.Assets.Add(new()
                {
                    LogicalPath = relative,
                    ContainerPath = relative,
                    SourcePath = destination,
                    Size = new FileInfo(destination).Length,
                    Type = GuessAssetType(Path.GetExtension(file)),
                    Metadata = { ["sourcePackagePath"] = destinationRoot, ["sourceFormat"] = ModFormatKind.Directory.ToString() }
                });
                added++;
            }
            else
            {
                existing.SourcePath = destination;
                existing.Size = new FileInfo(destination).Length;
                existing.Type = GuessAssetType(Path.GetExtension(file));
                existing.Metadata["sourcePackagePath"] = destinationRoot;
                existing.Metadata["sourceFormat"] = ModFormatKind.Directory.ToString();
                updated++;
            }
        }
        project.SourceDirectory = string.IsNullOrWhiteSpace(project.SourceDirectory) ? sourceRoot : project.SourceDirectory;
        if (!project.Sources.Any(x => string.Equals(x.Path, destinationRoot, StringComparison.OrdinalIgnoreCase)))
            project.Sources.Add(new ProjectSource { DisplayName = Path.GetFileName(source), Path = destinationRoot, Format = ModFormatKind.Directory });
        return new(ModFormatKind.Directory, added, updated, []);
    }

    public async Task<ImportResult> ImportIntoProjectAsync(
        string packagePath,
        ModProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(project);
        await using var input = File.OpenRead(Path.GetFullPath(packagePath));
        var probe = await registry.ProbeAsync(input, Path.GetFileName(packagePath), cancellationToken);
        if (probe is null && Path.GetExtension(packagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return await ImportGenericZipIntoProjectAsync(packagePath, project, cancellationToken);
        if (probe is null) throw new InvalidDataException($"无法识别模组格式: {packagePath}");
        input.Position = 0;
        var handler = registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind == probe.Format)
            ?? (probe.Format is ModFormatKind.Carra or ModFormatKind.Carra2
                ? registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind is ModFormatKind.Carra or ModFormatKind.Carra2)
                : null)
            ?? throw new InvalidDataException($"未注册格式处理器: {probe.Format}");
        var imported = await handler.ImportAsync(input, new(Path.GetDirectoryName(packagePath), true, cancellationToken));

        // Keep an immutable copy in the project so the authoring session is
        // self-contained and can be reopened/exported without re-selecting the
        // original archive.  SourceDirectory is assigned by ProjectService;
        // projects created by older versions fall back to their project root.
        var sourcePath = await MaterializeSourceAsync(packagePath, project, probe.Format, cancellationToken);
        var added = 0;
        var updated = 0;
        foreach (var asset in imported.Project.Assets)
        {
            var existing = project.Assets.FirstOrDefault(x => string.Equals(x.LogicalPath, asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                asset.SourcePath = sourcePath;
                asset.Metadata["sourcePackagePath"] = sourcePath;
                asset.Metadata["sourceFormat"] = probe.Format.ToString();
                project.Assets.Add(asset);
                added++;
            }
            else
            {
                existing.SourcePath = sourcePath;
                existing.ContainerPath = asset.ContainerPath;
                existing.Account = asset.Account;
                existing.Bundle = asset.Bundle;
                existing.UnityPathId = asset.UnityPathId;
                existing.UnityTypeId = asset.UnityTypeId;
                existing.Type = asset.Type;
                existing.Size = asset.Size;
                existing.Metadata["sourcePackagePath"] = sourcePath;
                existing.Metadata["sourceFormat"] = probe.Format.ToString();
                updated++;
            }
        }

        return new(probe.Format, added, updated, imported.PreservedFiles);
    }

    /// <summary>Imports a normal ZIP as an unpacked source tree. This is the
    /// fallback used by the launcher for non-Lunartique ZIP mods.</summary>
    public async Task<ImportResult> ImportGenericZipIntoProjectAsync(
        string zipPath, ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(project);
        var full = Path.GetFullPath(zipPath);
        if (!File.Exists(full)) throw new FileNotFoundException("ZIP 文件不存在。", full);
        var sourceRoot = string.IsNullOrWhiteSpace(project.SourceDirectory)
            ? Path.Combine(Path.GetDirectoryName(full)!, "sources")
            : Path.GetFullPath(project.SourceDirectory);
        var extractedRoot = Path.Combine(sourceRoot, "archives", Path.GetFileNameWithoutExtension(full));
        Directory.CreateDirectory(extractedRoot);
        var added = 0;
        var updated = 0;
        using var archive = ZipFile.OpenRead(full);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var relative = entry.FullName.Replace('\\', '/');
            EnsureSafeRelativePath(relative);
            var target = Path.Combine(extractedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = entry.Open())
            await using (var output = File.Create(target))
                await input.CopyToAsync(output, cancellationToken);
            var existing = project.Assets.FirstOrDefault(x => string.Equals(x.LogicalPath, relative, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                project.Assets.Add(new()
                {
                    LogicalPath = relative,
                    ContainerPath = relative,
                    SourcePath = target,
                    Size = new FileInfo(target).Length,
                    Type = GuessAssetType(Path.GetExtension(relative)),
                    Metadata = { ["sourcePackagePath"] = extractedRoot, ["sourceFormat"] = ModFormatKind.Directory.ToString() }
                });
                added++;
            }
            else
            {
                existing.SourcePath = target;
                existing.ContainerPath = relative;
                existing.Size = new FileInfo(target).Length;
                existing.Type = GuessAssetType(Path.GetExtension(relative));
                existing.Metadata["sourcePackagePath"] = extractedRoot;
                existing.Metadata["sourceFormat"] = ModFormatKind.Directory.ToString();
                updated++;
            }
        }
        project.SourceDirectory = sourceRoot;
        if (!project.Sources.Any(x => string.Equals(x.Path, extractedRoot, StringComparison.OrdinalIgnoreCase)))
            project.Sources.Add(new ProjectSource { DisplayName = Path.GetFileName(full), Path = extractedRoot, Format = ModFormatKind.Directory });
        return new(ModFormatKind.Directory, added, updated, []);
    }

    private static async Task<string> MaterializeSourceAsync(
        string packagePath, ModProject project, ModFormatKind format,
        CancellationToken cancellationToken)
    {
        var fullSource = Path.GetFullPath(packagePath);
        var sourceRoot = string.IsNullOrWhiteSpace(project.SourceDirectory)
            ? Path.Combine(Path.GetDirectoryName(fullSource)!, "sources")
            : Path.GetFullPath(project.SourceDirectory);
        Directory.CreateDirectory(sourceRoot);
        var fileName = Path.GetFileName(fullSource);
        var destination = Path.Combine(sourceRoot, fileName);
        if (!string.Equals(fullSource, destination, StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var candidate = destination;
            var suffix = 1;
            while (File.Exists(candidate) && !await SameFileAsync(fullSource, candidate, cancellationToken))
                candidate = Path.Combine(sourceRoot, $"{baseName}_{suffix++}{extension}");
            destination = candidate;
            if (!File.Exists(destination))
            {
                await using var input = File.OpenRead(fullSource);
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        project.SourceDirectory = sourceRoot;
        if (!project.Sources.Any(x => string.Equals(x.Path, destination, StringComparison.OrdinalIgnoreCase)))
            project.Sources.Add(new ProjectSource
            {
                DisplayName = fileName,
                Path = destination,
                Format = format
            });
        return destination;
    }

    private static async Task<bool> SameFileAsync(string left, string right, CancellationToken cancellationToken)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        if (a.Length != b.Length) return false;
        await using var first = File.OpenRead(left);
        await using var second = File.OpenRead(right);
        var leftHash = await System.Security.Cryptography.SHA256.HashDataAsync(first, cancellationToken);
        var rightHash = await System.Security.Cryptography.SHA256.HashDataAsync(second, cancellationToken);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static LimbusModEditor.Domain.Assets.AssetType GuessAssetType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => LimbusModEditor.Domain.Assets.AssetType.Texture,
        ".wav" or ".fsb" or ".bank" => LimbusModEditor.Domain.Assets.AssetType.Audio,
        ".json" or ".txt" or ".csv" => LimbusModEditor.Domain.Assets.AssetType.Text,
        ".bundle" or ".carra" or ".carra2" => LimbusModEditor.Domain.Assets.AssetType.Binary,
        _ => LimbusModEditor.Domain.Assets.AssetType.Binary
    };

    private static void EnsureSafeRelativePath(string path)
    {
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException($"ZIP 包含非法路径: {path}");
    }
}
