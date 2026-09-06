using System.IO.Compression;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Carra;

namespace LimbusModEditor.Domain.Tests;

/// <summary>多格式项目：one project holds Unity-bundle edits (Carra2) and
/// audio edits (Bank) at once; 导出全部 exports each source to its own
/// format.</summary>
public class MultiFormatExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-multi-" + Guid.NewGuid().ToString("N"));

    public MultiFormatExportTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "out"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Export_all_writes_every_source_to_its_own_format()
    {
        // a carra2 source with one object
        var carraPath = Path.Combine(_root, "skin.carra2");
        await using (var file = File.Create(carraPath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/1.0").Open();
            entry.Write([1, 2, 3]);
        }
        // a bank source (real FEV layout: RIFF + PROJ + SNDH(size 4, unknown) —
        // minimal valid bank with zero FSBs)
        var bankPath = Path.Combine(_root, "voice.bank");
        await File.WriteAllBytesAsync(bankPath, BuildMinimalBank());

        var project = new ModProject { Name = "Multi" };
        project.Sources.Add(new ProjectSource { DisplayName = "skin", Path = carraPath, Format = ModFormatKind.Carra2 });
        project.Sources.Add(new ProjectSource { DisplayName = "voice", Path = bankPath, Format = ModFormatKind.Bank });

        var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportAllAsync(project, Path.Combine(_root, "out"));
        Assert.Equal(2, result.SucceededCount);
        Assert.DoesNotContain(result.Items, x => !x.Succeeded);

        var carraOut = Path.Combine(_root, "out", "skin.carra2");
        var bankOut = Path.Combine(_root, "out", "voice.bank");
        Assert.True(File.Exists(carraOut));
        Assert.True(File.Exists(bankOut));
        // carra2 output round-trips the key
        using (var exported = File.OpenRead(carraOut))
        {
            var package = CarraArchive.Read(exported);
            var entry = Assert.Single(package.Entries);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", entry.Key.Account);
            Assert.Equal(1, entry.Key.PathId);
        }
        // bank output is byte-identical (unmodified export)
        Assert.Equal(await File.ReadAllBytesAsync(bankPath), await File.ReadAllBytesAsync(bankOut));
    }

    [Fact]
    public async Task Export_all_collects_failures_per_source()
    {
        var missingPath = Path.Combine(_root, "gone.carra2");
        var project = new ModProject();
        project.Sources.Add(new ProjectSource { DisplayName = "gone", Path = missingPath, Format = ModFormatKind.Carra2 });

        var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportAllAsync(project, Path.Combine(_root, "out"));
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("不存在", result.Items[0].Error);
    }

    [Fact]
    public async Task Export_all_skips_directory_sources_with_reason()
    {
        var project = new ModProject();
        project.Sources.Add(new ProjectSource { DisplayName = "folder", Path = Path.Combine(_root, "folder"), Format = ModFormatKind.Directory });

        var result = await new ModExportService(BuiltInFormatRegistry.Create()).ExportAllAsync(project, Path.Combine(_root, "out"));
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("目录来源", result.Items[0].Error);
    }

    private static byte[] BuildMinimalBank()
    {
        // Real FEV layout: RIFF + "FEV " + FMT chunk(8B) + LIST(4, form "PROJ")
        // + BNKI chunk(size 0x20, blob) + SNDH chunk(size 4, unknown u32 only).
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Chunk(string id, byte[] payload)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(id));
            w.Write(payload.Length);
            w.Write(payload);
        }
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0); // RIFF size, patched below
        w.Write(System.Text.Encoding.ASCII.GetBytes("FEV "));
        Chunk("FMT ", [0x8E, 0, 0, 0, 0x8C, 0, 0, 0]);
        Chunk("LIST", System.Text.Encoding.ASCII.GetBytes("PROJ"));
        Chunk("BNKI", new byte[0x20]); // project blob
        Chunk("SNDH", []);             // empty SNDH — valid event-bank shape (size 0)
        var bytes = ms.ToArray();
        var size = bytes.Length - 8;
        bytes[4] = (byte)size;
        bytes[5] = (byte)(size >> 8);
        bytes[6] = (byte)(size >> 16);
        bytes[7] = (byte)(size >> 24);
        return bytes;
    }
}
