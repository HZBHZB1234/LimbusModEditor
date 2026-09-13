using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Build;

public sealed record LunartiqueConversionDiagnostic(string RelativePath, string Message, bool IsError);
public sealed record LunartiqueCarraConversionResult(CarraPackage Package, IReadOnlyList<LunartiqueConversionDiagnostic> Diagnostics, int AddedObjects, int ModifiedObjects, int UnchangedObjects);

/// <summary>Converts Lunartique's paired SerializedFile resources into the
/// object-level Carra representation used by the Limbus loader. Conversion is
/// hash-based and follows the reference launcher's behavior: unchanged objects
/// are omitted, while changed/new objects become XZ-compressed Carra entries.</summary>
public sealed class LunartiqueCarraConversionService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly Regex ResourcePattern = new("^(?<account>[^/\\\\]+)/(?<bundle>[^/\\\\]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<LunartiqueCarraConversionResult> ConvertAsync(LunartiquePackage source, CancellationToken cancellationToken = default)
    {
        var resources = source.Resources.Count;
        Log.Info("Lunartique → Carra 转换开始：资源 {0} 个，取消已请求 {1}", resources, cancellationToken.IsCancellationRequested);
        try
        {
            return Task.Run(() => Convert(source, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            Log.Info("已取消：Lunartique → Carra 转换（资源 {0} 个）：{1}", resources, ex.Message);
            throw;
        }
    }

    private static LunartiqueCarraConversionResult Convert(LunartiquePackage source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var scope = Log.Scope("Lunartique → Carra 转换");
        var package = new CarraPackage();
        var diagnostics = new List<LunartiqueConversionDiagnostic>();
        var added = 0; var modified = 0; var unchanged = 0;
        var failedResources = 0;
        foreach (var resource in source.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = ResourcePattern.Match(resource.RelativePath.Replace('\\', '/').Trim('/'));
            if (!match.Success)
            {
                diagnostics.Add(new(resource.RelativePath, "资源路径不是 account/bundle 形式，已保留为不可转换资源。", false));
                Log.Warn("Lunartique 资源路径不匹配 account/bundle：{0}（Installation {1} 字节 / Uninstallation {2} 字节），该资源被保留为不可转换资源",
                    resource.RelativePath, resource.Installation.Length, resource.Uninstallation.Length);
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
                    Log.Warn("Lunartique Installation 资源为空（表示删除）：{0}，Carra 不支持显式删除，整个资源已跳过（Uninstallation {1} 字节）",
                        resource.RelativePath, resource.Uninstallation.Length);
                    continue;
                }
                var newObjects = backend.ReadSerializedObjects(temp.AfterPath).ToDictionary(x => x.PathId);
                Log.Debug("转换资源 {0}：原对象 {1} 个 → 新对象 {2} 个", resource.RelativePath, oldObjects.Count, newObjects.Count);
                foreach (var item in newObjects.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (oldObjects.TryGetValue(item.PathId, out var old) && SHA256.HashData(old.Data).AsSpan().SequenceEqual(SHA256.HashData(item.Data))) { unchanged++; continue; }
                    var key = new CarraObjectKey(match.Groups["account"].Value, match.Groups["bundle"].Value, item.PathId, item.TypeId);
                    if (package.Find(key.LogicalPath) is not null)
                    {
                        diagnostics.Add(new(resource.RelativePath, $"对象路径重复：{key.LogicalPath}，已跳过后续对象。", true));
                        Log.Warn("Carra 对象路径重复，跳过后续对象：{0}（来源资源 {1}，PathId {2}，TypeId {3}）",
                            key.LogicalPath, resource.RelativePath, item.PathId, item.TypeId);
                        continue;
                    }
                    package.Entries.Add(CarraEntry.CreateNew(key, item.Data));
                    if (oldObjects.ContainsKey(item.PathId)) modified++; else added++;
                }
                foreach (var deleted in oldObjects.Keys.Except(newObjects.Keys))
                {
                    diagnostics.Add(new(resource.RelativePath, $"对象 Path ID {deleted} 只存在于 Uninstallation，Carra 不支持显式删除，已跳过。", false));
                    Log.Debug("对象 PathId {0} 只存在于 Uninstallation（资源 {1}）：Carra 不支持显式删除，已跳过", deleted, resource.RelativePath);
                }
            }
            catch (OperationCanceledException ex)
            {
                Log.Info("已取消：Lunartique → Carra 转换（正在处理资源 {0}，{1}）", resource.RelativePath, ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException)
            {
                diagnostics.Add(new(resource.RelativePath, $"无法解析 SerializedFile：{ex.Message}", true));
                failedResources++;
                Log.Error(ex, "无法解析 SerializedFile：资源 {0}（Uninstallation {1} 字节 / Installation {2} 字节）",
                    resource.RelativePath, resource.Uninstallation.Length, resource.Installation.Length);
            }
        }
        var errorCount = diagnostics.Count(x => x.IsError);
        Log.Debug("Lunartique → Carra 转换结束：Carra 条目 {0} 个，added {1} / modified {2} / unchanged {3}，诊断 {4} 条（其中错误 {5} 条，失败资源 {6} 个）",
            package.Entries.Count, added, modified, unchanged, diagnostics.Count, errorCount, failedResources);
        if (errorCount > 0)
        {
            Log.Warn("Lunartique → Carra 转换存在 {0} 条错误诊断：其中有资源被整体跳过，产出的 Carra 包不完整", errorCount);
        }
        return new(package, diagnostics, added, modified, unchanged);
    }

    private sealed class TemporaryPair : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public string BeforePath { get; } = Path.Combine(Path.GetTempPath(), "lme-lunartique-" + Guid.NewGuid().ToString("N") + ".assets");
        public string AfterPath { get; } = Path.Combine(Path.GetTempPath(), "lme-lunartique-" + Guid.NewGuid().ToString("N") + ".assets");
        public TemporaryPair(byte[] before, byte[] after)
        {
            if (before.Length > 0)
            {
                try
                {
                    File.WriteAllBytes(BeforePath, before);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "写入临时 Before 文件失败：{0}（{1} 字节，Uninstallation 将读不到内容）", BeforePath, before.Length);
                    throw;
                }
            }
            try
            {
                File.WriteAllBytes(AfterPath, after);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "写入临时 After 文件失败：{0}（{1} 字节）", AfterPath, after.Length);
                throw;
            }
            Log.Debug("Lunartique 临时资源文件就绪：Before {0}（{1} 字节）→ {2}，After {3}（{4} 字节）→ {5}",
                BeforePath, before.Length, before.Length > 0 ? "已写出" : "跳过（Uninstallation 为空）", AfterPath, after.Length, "已写出");
        }
        public void Dispose() { TryDelete(BeforePath); TryDelete(AfterPath); }
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Debug(ex, "删除临时资源文件失败（残留可在 %TEMP% 手动清理）：{0}", path); }
        }
    }
}
