using System.Buffers.Binary;
using System.Text;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Format.Tests;

/// <summary>P2.2 tests over a hand-built minimal PE64 image with a real export
/// directory: machine detection, export scan, required-symbol matching, and
/// the fingerprint-based probe cache.</summary>
public class FmodDllInspectorTests : IDisposable
{
    private readonly string _root;

    public FmodDllInspectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-fmod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Builds a minimal PE32+ image whose export table contains the
    /// given ASCII names, mapped at RVA 0x1000 / file offset 0x400.</summary>
    private static byte[] BuildPe64(string[] exportNames)
    {
        var blob = new MemoryStream();
        using var w = new BinaryWriter(blob);
        // DOS header
        w.Write("MZ"u8.ToArray());
        w.Write(new byte[58]);
        w.Write(0x80); // e_lfanew
        w.Flush();
        blob.Position = 0x80;
        // NT headers
        w.Write("PE\0\0"u8.ToArray());
        w.Write((ushort)0x8664);      // Machine
        w.Write((ushort)1);           // NumberOfSections
        w.Write(0u);                  // TimeDateStamp
        w.Write(0u);                  // PointerToSymbolTable
        w.Write(0u);                  // NumberOfSymbols
        w.Write((ushort)0xF0);        // SizeOfOptionalHeader
        w.Write((ushort)0x0022);      // Characteristics
        // Optional header PE32+ (240 bytes: 112 fixed + 16 data directories)
        var optStart = (int)blob.Position;
        w.Write((ushort)0x20B);       // Magic
        w.Write(new byte[106]);       // standard + windows-specific fields
        w.Write(16u);                 // NumberOfRvaAndSizes at offset 108
        w.Write(0x1000u);             // DataDirectory[0] export RVA
        w.Write(0x200u);              // export size
        w.Write(new byte[15 * 8]);    // remaining data directories
        // Section header (sized generously for long export-name lists)
        w.Write(Encoding.ASCII.GetBytes(".edata\0\0"));
        w.Write(0x800u);              // VirtualSize
        w.Write(0x1000u);             // VirtualAddress
        w.Write(0x800u);              // SizeOfRawData
        w.Write(0x400u);              // PointerToRawData
        w.Write(new byte[24]);
        // Section payload at file offset 0x400; keep every array clear of the
        // 40-byte export directory itself.
        blob.Position = 0x400;
        var functionsOffset = 0x30;
        var namesArrayOffset = functionsOffset + 4 * exportNames.Length;
        var ordinalsOffset = namesArrayOffset + 4 * exportNames.Length;
        var stringsOffset = ordinalsOffset + 2 * exportNames.Length + 2;
        var dllNameOffset = stringsOffset;
        var stringsStart = stringsOffset + "fmod64.dll".Length + 1;
        var nameOffsets = new List<int>();
        var cursor = stringsStart;
        foreach (var name in exportNames) { nameOffsets.Add(cursor); cursor += name.Length + 1; }

        w.Write(0u);                  // Characteristics
        w.Write(0u);                  // TimeDateStamp
        w.Write((ushort)0); w.Write((ushort)0); // version
        w.Write((uint)(0x1000 + dllNameOffset)); // Name RVA
        w.Write(0u);                  // Base
        w.Write((uint)exportNames.Length);       // NumberOfFunctions
        w.Write((uint)exportNames.Length);       // NumberOfNames
        w.Write((uint)(0x1000 + functionsOffset));       // AddressOfFunctions RVA
        w.Write((uint)(0x1000 + namesArrayOffset));      // AddressOfNames RVA
        w.Write((uint)(0x1000 + ordinalsOffset));        // AddressOfNameOrdinals
        // DLL name
        blob.Position = 0x400 + dllNameOffset;
        w.Write(Encoding.ASCII.GetBytes("fmod64.dll\0"));
        // export function addresses
        blob.Position = 0x400 + functionsOffset;
        foreach (var _ in exportNames) w.Write(0x1000u);
        // name RVAs
        blob.Position = 0x400 + namesArrayOffset;
        foreach (var offset in nameOffsets) w.Write((uint)(0x1000 + offset));
        // ordinals
        blob.Position = 0x400 + ordinalsOffset;
        for (var i = 0; i < exportNames.Length; i++) w.Write((ushort)i);
        // name strings (RVAs point at the region START, not the end cursor)
        blob.Position = 0x400 + stringsStart;
        foreach (var name in exportNames) w.Write(Encoding.ASCII.GetBytes(name + "\0"));

        return blob.ToArray();
    }

    [Fact]
    public void Detects_machine_and_missing_exports()
    {
        var path = Path.Combine(_root, "fmod64.dll");
        File.WriteAllBytes(path, BuildPe64(["FMOD_System_Create", "FMOD_System_Init", "FMOD_System_CreateSound"]));
        var report = FmodDllInspector.InspectFile(path);
        Assert.Equal("x64", report.Machine);
        Assert.True(report.Present);
        Assert.Equal(3, report.ExportCount);
        Assert.Contains("FMOD_Sound_ReadData", report.MissingDecodeExports);
        Assert.False(report.DecodeReady);
        Assert.Equal(3, report.MissingEncodeExports.Count);
        Assert.Equal("不是 PE 文件（缺少 MZ 头）。", FmodDllInspector.InspectFile(
            WriteFile("garbage.dll", [1, 2, 3, 4])).Error);
    }

    private string WriteFile(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Full_decode_export_set_is_ready()
    {
        var path = Path.Combine(_root, "fmod64.dll");
        File.WriteAllBytes(path, BuildPe64(
        [
            "FMOD_System_Create", "FMOD_System_Init", "FMOD_System_Release", "FMOD_System_CreateSound",
            "FMOD_Sound_Release", "FMOD_Sound_GetNumSubSounds", "FMOD_Sound_GetSubSound", "FMOD_Sound_SeekData",
            "FMOD_Sound_GetDefaults", "FMOD_Sound_GetFormat", "FMOD_Sound_GetLength", "FMOD_Sound_ReadData"
        ]));
        var report = FmodDllInspector.InspectFile(path);
        Assert.Empty(report.MissingDecodeExports);
        Assert.True(report.DecodeReady);
        Assert.False(report.EncodeReady);
    }

    [Fact]
    public void Directory_probe_reports_missing_dlls()
    {
        var reports = FmodDllInspector.InspectDirectory(_root);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.False(r.Present));
        Assert.Contains("fsbank64.dll", reports.Select(r => r.FileName));
    }

    [Fact]
    public void Probe_cache_reuses_result_until_directory_changes()
    {
        var cacheFile = Path.Combine(_root, "probe.json");
        File.WriteAllBytes(Path.Combine(_root, "fmod64.dll"), BuildPe64(["FMOD_System_Create"]));
        var service = new FmodCompatibilityService();
        var first = service.Probe(_root, cacheFile);
        Assert.Equal(1, first.Reports.Single(r => r.FileName == "fmod64.dll").ExportCount);

        // replace the DLL content: fingerprint changes, so a fresh probe runs
        File.WriteAllBytes(Path.Combine(_root, "fmod64.dll"), BuildPe64(["FMOD_System_Create", "FMOD_System_Init"]));
        var second = service.Probe(_root, cacheFile);
        Assert.Equal(2, second.Reports.Single(r => r.FileName == "fmod64.dll").ExportCount);

        // no further changes: the cached report comes back untouched
        var third = service.Probe(_root, cacheFile);
        Assert.Equal(second.Fingerprint, third.Fingerprint);
        Assert.Equal(2, third.Reports.Single(r => r.FileName == "fmod64.dll").ExportCount);
    }
}
