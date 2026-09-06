using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// Real-game .bank verification (paths per LCTA and the real Steam install:
/// LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop).
/// Every test skips silently when the game is absent. Read-only: banks are
/// parsed and re-extracted but never written back to the game directory.
/// </summary>
public class RealBankTests : IDisposable
{
    private static readonly string? BankRoot = FindBankRoot();
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "lme-realbank-" + Guid.NewGuid().ToString("N"));

    public RealBankTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch (IOException) { }
    }

    private static string? FindBankRoot()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\Assets\Sound\FMODBuilds\Desktop",
        };
        var overrideDir = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            candidates = [Path.Combine(overrideDir, "LimbusCompany_Data", "StreamingAssets", "Assets", "Sound", "FMODBuilds", "Desktop"), .. candidates];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static IReadOnlyList<string> BankFiles(int limit)
    {
        if (BankRoot is null) return [];
        try { return Directory.EnumerateFiles(BankRoot, "*.bank", SearchOption.TopDirectoryOnly).Take(limit).ToList(); }
        catch (Exception) { return []; }
    }

    [Fact]
    public void Real_banks_parse_with_sndh_tables()
    {
        var banks = BankFiles(limit: 20);
        Assert.NotEmpty(banks);

        var parsed = 0;
        var encrypted = 0;
        var fsbCount = 0;
        var failures = new List<string>();
        foreach (var path in banks)
        {
            var data = File.ReadAllBytes(path);
            var info = BankParser.TryParse(data);
            if (info is null) { failures.Add(Path.GetFileName(path)); continue; }
            parsed++;
            fsbCount += info.FsbCount;
            if (info.IsEncrypted) encrypted++;
            // every entry must start with FSB5 unless the bank is encrypted
            if (!info.IsEncrypted)
            {
                foreach (var offset in info.FsbOffsets)
                    Assert.Equal("FSB5", System.Text.Encoding.ASCII.GetString(data, (int)offset, 4));
            }
        }
        Assert.True(failures.Count == 0, $"真实 bank 解析失败 {failures.Count} 个: {string.Join(", ", failures.Take(5))}");
        Assert.True(fsbCount > 0, $"解析了 {parsed} 个真实 bank，FSB 总数 {fsbCount}，加密 {encrypted}");
        Console.WriteLine($"真实 bank 勘察: 解析 {parsed}, FSB {fsbCount}, 加密 {encrypted}");
    }

    [Fact]
    public void Real_fsb_payloads_parse_through_fsb5()
    {
        var banks = BankFiles(limit: 12);
        Assert.NotEmpty(banks);

        var fsbParsed = 0;
        var sampleTotal = 0;
        var codecs = new Dictionary<int, int>();
        foreach (var path in banks)
        {
            var data = File.ReadAllBytes(path);
            var info = BankParser.TryParse(data);
            if (info is null || info.IsEncrypted) continue;
            foreach (var fsb in BankParser.Extract(data, info))
            {
                var fsbInfo = Fsb5Parser.TryParse(fsb.Span);
                if (fsbInfo is null) continue;
                fsbParsed++;
                sampleTotal += fsbInfo.SampleCount;
                codecs[fsbInfo.Codec] = codecs.TryGetValue(fsbInfo.Codec, out var n) ? n + 1 : 1;
                foreach (var sample in fsbInfo.Samples.Take(3))
                {
                    // plausible audio facts only from the file itself
                    Assert.InRange(sample.SampleRate, 8000, 192000);
                    Assert.InRange(sample.Channels, 1, 8);
                }
            }
        }
        Assert.True(fsbParsed > 0, $"真实 FSB 解析 {fsbParsed} 个");
        Console.WriteLine($"真实 FSB5 勘察: {fsbParsed} 个 FSB, {sampleTotal} 个样本, 编解码 {string.Join(",", codecs.Select(x => $"{x.Key}:{x.Value}"))}");
    }

    [Fact]
    public async Task Real_bank_rebuild_is_lossless_when_unmodified()
    {
        var banks = BankFiles(limit: 3);
        Assert.NotEmpty(banks);

        foreach (var path in banks)
        {
            var data = File.ReadAllBytes(path);
            var info = BankParser.TryParse(data);
            if (info is null || info.IsEncrypted || info.FsbCount == 0) continue;

            // rebuild with the original FSB blobs must reproduce the file
            var package = new BankPackage { OriginalData = data, Info = info };
            package.FsbData.AddRange(BankParser.Extract(data, info).Select(x => x.ToArray()));
            Assert.False(package.HasModifications);
            using var output = new MemoryStream();
            var modPackage = new LimbusModEditor.Formats.Abstractions.ModPackage
            {
                SourceFormat = LimbusModEditor.Domain.Formats.ModFormatKind.Bank,
                Payload = package
            };
            await new BankFormatHandler().ExportAsync(modPackage, output, new(
                LimbusModEditor.Domain.Formats.ModFormatKind.Bank, true, true, CancellationToken.None, null));
            Assert.Equal(data, output.ToArray());
        }
    }
}
