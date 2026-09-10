using System.Diagnostics;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-11：音频索引的真实环境门控测试（无游戏目录的机器自动跳过）。
///
/// <para>验证三件事：① 全量 1531 个 bank 能被完整索引；② 二次进页面<b>不重新解析任何 bank 文件</b>
/// 且在 1.5 秒内读完（这是「表缓存」带来的实际收益）；③ 缓存里的样本行与<b>现读</b>
/// <c>BankDirectoryService.ReadSampleTable</c> 的结果<b>逐字段一致</b>
/// （缓存只影响速度，不影响正确性——本计划最重要的不变量）。</para>
/// </summary>
public sealed class RealBankIndexSmokeTests : IDisposable
{
    private static readonly string? BankRoot = FindBankRoot();

    private readonly string _cacheDir;

    public RealBankIndexSmokeTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-bank-index-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private static string? FindBankRoot()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
        };
        var overrideGame = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideGame))
        {
            candidates =
            [
                Path.Combine(overrideGame, "LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMDBuilds", "Desktop"),
                .. candidates,
            ];
        }
        return candidates.FirstOrDefault(Directory.Exists);
    }

    [Fact]
    public async Task Real_bank_index_is_built_reused_and_matches_live_parsing()
    {
        if (BankRoot is null) return;

        var store = new BankIndexStore(_cacheDir);
        var service = new BankIndexService(store);
        var source = BankIndexSource.Describe(BankRoot);

        // ① 冷建索引：全部文件都要解析（带进度回调）。
        var reports = new List<BankIndexProgress>();
        var cold = Stopwatch.StartNew();
        var coldResult = await service.RefreshAsync(source, new Progress<BankIndexProgress>(reports.Add));
        cold.Stop();

        var diskFiles = BankIndexService.EnumerateDiskFiles(BankRoot);
        Assert.Equal(diskFiles.Count, coldResult.DiskFileCount);
        Assert.True(diskFiles.Count >= 1000, $"真实 bank 目录应有上千个文件，实际 {diskFiles.Count}");
        Assert.Equal(coldResult.DiskFileCount, coldResult.ParseCount);
        Assert.Equal(0, coldResult.ReusedCount);
        Assert.Equal(coldResult.DiskFileCount, store.ReadBankCount());
        Assert.True(coldResult.Samples > 0, "音频 bank 总表的样本行数应大于 0");
        Assert.NotEmpty(reports);

        // 类型分布与既有 BankDirectoryService 口径一致（不新增第二套判定）。
        var scanned = service.Banks.ScanDirectory(BankRoot).ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
        var entries = store.ReadEntries();
        Assert.All(entries, entry =>
        {
            var reference = scanned[entry.FileName];
            Assert.Equal(reference.Kind, entry.Kind);
            Assert.Equal(reference.FsbCount, entry.FsbCount);
            Assert.Equal(reference.FileSizeBytes, entry.SizeBytes);
        });
        var audio = entries.Count(x => x.Kind == BankKind.Audio);
        var events = entries.Count(x => x.Kind == BankKind.Event);
        Assert.True(audio > 0 && events > 0, $"类型分布异常：音频 {audio} · 事件 {events}");

        // ② 二次进页面：不重新解析任何 bank 文件，读库 < 1.5 秒。
        var warm = Stopwatch.StartNew();
        var warmResult = await service.RefreshAsync(BankIndexSource.Describe(BankRoot));
        warm.Stop();
        Assert.Equal(0, warmResult.ParseCount);
        Assert.Equal(warmResult.DiskFileCount, warmResult.ReusedCount);

        var readWatch = Stopwatch.StartNew();
        var snapshot = store.ReadSnapshot(source.BankDirectory);
        readWatch.Stop();
        Assert.Equal(entries.Count, snapshot.Entries.Count);
        Assert.True(readWatch.Elapsed < TimeSpan.FromSeconds(1.5),
            $"读索引耗时 {readWatch.Elapsed.TotalMilliseconds:F0}ms，超出 1.5s 预算");

        // ③ 缓存 vs 现读逐字段一致：抽 3 个音频 bank（优先样本多的）。
        var sample = entries.Where(x => x.Kind == BankKind.Audio)
            .OrderByDescending(x => x.Samples.Count)
            .Take(3)
            .ToList();
        Assert.NotEmpty(sample);
        foreach (var entry in sample)
        {
            var live = service.Banks.ReadSampleTable(entry.Path);
            var liveRows = live.FsbTables
                .Where(x => x.Fsb is not null)
                .SelectMany(x => x.Fsb!.Samples.Select(s => (Fsb: x.FsbIndex, Sample: s)))
                .ToList();
            Assert.Equal(liveRows.Count, entry.Samples.Count);
            for (var i = 0; i < liveRows.Count; i++)
            {
                var (fsbIndex, s) = liveRows[i];
                var row = entry.Samples[i];
                Assert.Equal(fsbIndex, row.FsbIndex);
                Assert.Equal(s.Index, row.SampleIndex);
                Assert.Equal(s.SampleRate, row.SampleRate);
                Assert.Equal(s.Channels, row.Channels);
                Assert.Equal(s.SampleCount, row.SampleCount);
                Assert.Equal(live.FsbTables[fsbIndex].Fsb!.CodecName, row.CodecName);
                Assert.Equal(s.DataSize ?? 0, row.DataSize);
            }
        }

        Console.WriteLine(
            $"真实 bank 索引：{coldResult.DiskFileCount} 个文件 · 样本 {coldResult.Samples} 行 · " +
            $"冷建索引 {cold.Elapsed.TotalSeconds:0.0}s（解析 {coldResult.ParseCount}）→ " +
            $"二次进页面 {warm.Elapsed.TotalMilliseconds:F0}ms（解析 {warmResult.ParseCount}）· " +
            $"读库 {readWatch.Elapsed.TotalMilliseconds:F0}ms");
        Console.WriteLine($"类型分布：音频 {audio} · 事件 {events} · 其他 {entries.Count - audio - events}");
    }

    [Fact]
    public async Task Real_bank_index_incremental_change_reparses_only_that_file()
    {
        if (BankRoot is null) return;

        // 用真实目录的一个副本（只复制 1 个音频 bank）验证「只重解析变过的文件」，
        // 避免改动游戏目录里的任何文件。
        var work = Path.Combine(Path.GetTempPath(), "lme-bank-incr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var sourceFile = BankIndexService.EnumerateDiskFiles(BankRoot)
                .Select(x => x.Path)
                .First(p => new FileInfo(p).Length > 0);
            var copyA = Path.Combine(work, "keep.bank");
            var copyB = Path.Combine(work, "change.bank");
            File.Copy(sourceFile, copyA);
            File.Copy(sourceFile, copyB);

            var service = new BankIndexService(new BankIndexStore(_cacheDir));
            var source = BankIndexSource.Describe(work);
            var cold = await service.RefreshAsync(source);
            Assert.Equal(2, cold.ParseCount);

            var warm = await service.RefreshAsync(BankIndexSource.Describe(work));
            Assert.Equal(0, warm.ParseCount);
            Assert.Equal(2, warm.ReusedCount);

            // 改动其中一个文件（追加一个字节，大小与 mtime 都变）→ 只重解析它。
            using (var stream = new FileStream(copyB, FileMode.Append, FileAccess.Write))
            {
                stream.WriteByte(0x00);
            }
            var afterEdit = await service.RefreshAsync(BankIndexSource.Describe(work));
            Assert.Equal(1, afterEdit.ParseCount);
            Assert.Equal(1, afterEdit.ReusedCount);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception) { /* 临时目录 */ }
        }
    }
}
