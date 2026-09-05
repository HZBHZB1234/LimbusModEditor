using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Build;

public sealed record LunartiqueConversionDiagnostic(string RelativePath, string Message, bool IsError);
public sealed record LunartiqueCarraConversionResult(CarraPackage Package, IReadOnlyList<LunartiqueConversionDiagnostic> Diagnostics, int AddedObjects, int ModifiedObjects, int UnchangedObjects);

/// <summary>Converts Lunartique's paired SerializedFile resources into the
/// object-level Carra representation used by the Limbus loader. Conversion is
/// hash-based and follows the reference launcher's behavior: unchanged objects
/// are omitted, while changed/new objects become XZ-compressed Carra entries.</summary>
public sealed class LunartiqueCarraConversionService
{
    private static readonly Regex ResourcePattern = new("^(?<account>[^/\\\\]+)/(?<bundle>[^/\\\\]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<LunartiqueCarraConversionResult> ConvertAsync(LunartiquePackage source, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(source, cancellationToken), cancellationToken);

    private static LunartiqueCarraConversionResult Convert(LunartiquePackage source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var package = new CarraPackage();
        var diagnostics = new List<LunartiqueConversionDiagnostic>();
        var added = 0; var modified = 0; var unchanged = 0;
        foreach (var resource in source.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = ResourcePattern.Match(resource.RelativePath.Replace('\\', '/').Trim('/'));
            if (!match.Success)
            {
                diagnostics.Add(new(resource.RelativePath, "资源路径不是 account/bundle 形式，已保留为不可转换资源。", false));
                continue;
            }
            try
            {
                using var temp = new TemporaryPair(resource.Uninstallation, resource.Installation);
                using var backend = new AssetsToolsBackend();
                var oldObjects = resource.Uninstallation.Length == 0
                    ? new Dictionary<long, UnitySerializedObject>()
                    : backend.ReadSerializedObjects(temp.BeforePath).ToDictionary(x => x.PathId);
                if (resource.Installation.Length == 0)
                {
                    diagnostics.Add(new(resource.RelativePath, "Installation 资源为空，表示删除；Carra 不支持显式删除，已跳过。", false));
                    continue;
                }
                var newObjects = backend.ReadSerializedObjects(temp.AfterPath).ToDictionary(x => x.PathId);
                foreach (var item in newObjects.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (oldObjects.TryGetValue(item.PathId, out var old) && SHA256.HashData(old.Data).AsSpan().SequenceEqual(SHA256.HashData(item.Data))) { unchanged++; continue; }
                    var key = new CarraObjectKey(match.Groups["account"].Value, match.Groups["bundle"].Value, item.PathId, item.TypeId);
                    if (package.Find(key.LogicalPath) is not null)
                    {
                        diagnostics.Add(new(resource.RelativePath, $"对象路径重复：{key.LogicalPath}，已跳过后续对象。", true));
                        continue;
                    }
                    package.Entries.Add(CarraEntry.CreateNew(key, item.Data));
                    if (oldObjects.ContainsKey(item.PathId)) modified++; else added++;
                }
                foreach (var deleted in oldObjects.Keys.Except(newObjects.Keys))
                    diagnostics.Add(new(resource.RelativePath, $"对象 Path ID {deleted} 只存在于 Uninstallation，Carra 不支持显式删除，已跳过。", false));
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException)
            {
                diagnostics.Add(new(resource.RelativePath, $"无法解析 SerializedFile：{ex.Message}", true));
            }
        }
        return new(package, diagnostics, added, modified, unchanged);
    }

    private sealed class TemporaryPair : IDisposable
    {
        public string BeforePath { get; } = Path.Combine(Path.GetTempPath(), "lme-lunartique-" + Guid.NewGuid().ToString("N") + ".assets");
        public string AfterPath { get; } = Path.Combine(Path.GetTempPath(), "lme-lunartique-" + Guid.NewGuid().ToString("N") + ".assets");
        public TemporaryPair(byte[] before, byte[] after) { if (before.Length > 0) File.WriteAllBytes(BeforePath, before); File.WriteAllBytes(AfterPath, after); }
        public void Dispose() { TryDelete(BeforePath); TryDelete(AfterPath); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
