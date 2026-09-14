using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Formats.Carra;

public sealed record CarraChange(CarraObjectKey Key, byte[]? OriginalData, byte[] ModifiedData, bool IsAdded);

public static class CarraDiffService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<CarraChange> Compare(CarraPackage original, CarraPackage modified)
    {
        using var scope = Log.Scope("Carra 差异比对");
        Log.Debug("Carra 差异比对开始：原始对象 {0} 个，修改后对象 {1} 个", original.Entries.Count, modified.Entries.Count);
        var before = original.Entries.ToDictionary(x => x.Key.LogicalPath, StringComparer.Ordinal);
        var changes = new List<CarraChange>();
        var index = 0L;
        var added = 0L;
        foreach (var item in modified.Entries)
        {
            index++;
            var data = item.ModifiedData ?? item.ReadData();
            if (!before.TryGetValue(item.Key.LogicalPath, out var old))
            {
                added++;
                changes.Add(new(item.Key, null, data, true));
                continue;
            }
            var oldData = old.ReadData();
            if (!oldData.AsSpan().SequenceEqual(data))
                changes.Add(new(item.Key, oldData, data, false));
            Log.Every(index, 200, LogLevel.Debug, () => $"Carra 差异比对进度：已比对 {index} 个对象，当前 {item.Key.LogicalPath}");
        }
        Log.Debug("Carra 差异比对完成：变动 {0} 个（其中新增 {1} 个）", changes.Count, added);
        return changes;
    }

    public static CarraPackage CreatePatch(CarraPackage original, CarraPackage modified)
    {
        using var scope = Log.Scope("Carra 生成差分包");
        var patch = new CarraPackage();
        var byKey = modified.Entries.ToDictionary(x => x.Key.LogicalPath, StringComparer.Ordinal);
        var index = 0L;
        foreach (var change in Compare(original, modified))
        {
            // A patch package can preserve the modified compressed bytes when the
            // editor has not re-encoded the object yet. Structured encoders can
            // later call ReplaceCompressedData before export.
            var source = byKey[change.Key.LogicalPath];
            patch.Entries.Add(source);
            index++;
            Log.Every(index, 200, LogLevel.Debug, () => $"Carra 差分包累计：第 {index} 个对象 {change.Key.LogicalPath}（新增 {change.IsAdded}）");
        }
        patch.UnknownFiles.AddRange(modified.UnknownFiles);
        Log.Info("Carra 差分包生成完成：对象条目 {0} 个，未知文件 {1} 个", patch.Entries.Count, patch.UnknownFiles.Count);
        return patch;
    }
}
