using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace LimbusModEditor.Formats.Bank;

/// <summary>One probed FMOD-related DLL: PE machine, version resources, the
/// export-table snapshot and which of this tool's required symbols are missing.
/// Probing never loads the DLL into the process.</summary>
public sealed record FmodDllReport(
    string FileName,
    bool Present,
    string Machine,
    string? FileVersion,
    string? ProductVersion,
    int ExportCount,
    IReadOnlyList<string> MissingDecodeExports,
    IReadOnlyList<string> MissingEncodeExports,
    bool DecodeReady,
    bool EncodeReady,
    string? Error)
{
    public string Summary() => !Present
        ? $"{FileName}: 未找到"
        : Error is not null
            ? $"{FileName}: 无法解析（{Error}）"
            : $"{FileName}: {Machine}，版本 {FileVersion ?? "未知"}，导出 {ExportCount} 个符号；" +
              $"解码 {(DecodeReady ? "就绪" : $"缺 {MissingDecodeExports.Count} 个符号")}，" +
              $"FSB 编码 {(EncodeReady ? "就绪" : $"缺 {MissingEncodeExports.Count} 个符号")}";
}

/// <summary>
/// Read-only PE/export prober for user-provided FMOD/FSBank DLLs. It parses
/// the PE headers and export directory from the raw file (no LoadLibrary) and
/// reports version resources through the OS file-version API. Nothing is
/// executed, loaded or written.
/// </summary>
public static class FmodDllInspector
{
    private static readonly string[] DecodeExports =
    [
        "FMOD_System_Create", "FMOD_System_Init", "FMOD_System_Release", "FMOD_System_CreateSound",
        "FMOD_Sound_Release", "FMOD_Sound_GetNumSubSounds", "FMOD_Sound_GetSubSound", "FMOD_Sound_SeekData",
        "FMOD_Sound_GetDefaults", "FMOD_Sound_GetFormat", "FMOD_Sound_GetLength", "FMOD_Sound_ReadData"
    ];
    private static readonly string[] EncodeExports = ["FSBank_Init", "FSBank_Build", "FSBank_Release"];

    public static IReadOnlyList<FmodDllReport> InspectDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var reports = new List<FmodDllReport>();
        foreach (var name in new[] { "fmod64.dll", "fsbank64.dll" })
        {
            var path = Path.Combine(directory, name);
            reports.Add(File.Exists(path) ? InspectFile(path) : new FmodDllReport(
                name, false, "-", null, null, 0, DecodeExports, EncodeExports, false, false, null));
        }
        return reports;
    }

    public static FmodDllReport InspectFile(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
                return Fail(fileName, "不是 PE 文件（缺少 MZ 头）。");
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
            if (peOffset <= 0 || peOffset + 24 > data.Length || !data.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8))
                return Fail(fileName, "PE 签名无效。");

            var machineValue = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 4));
            var machine = machineValue switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "ARM64",
                _ => $"未知(0x{machineValue:X4})"
            };
            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 6));
            var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 20));
            if (sizeOfOptionalHeader < 64 || sectionCount is 0 or > 96) return Fail(fileName, "PE 头字段异常。");

            var optionalStart = peOffset + 24;
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optionalStart));
            var dataDirectoryStart = optionalStart + (magic == 0x20B ? 112 : 96);
            var numberOfDirectories = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(
                optionalStart + (magic == 0x20B ? 108 : 92)));
            if (numberOfDirectories == 0 || dataDirectoryStart + 8 > data.Length)
                return WithVersion(path, fileName, machine, 0, missingDecode: DecodeExports, missingEncode: EncodeExports);

            var exportRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataDirectoryStart));
            if (exportRva == 0)
                return WithVersion(path, fileName, machine, 0, DecodeExports, EncodeExports);

            var sections = ParseSections(data, peOffset + 24 + sizeOfOptionalHeader, sectionCount);
            var exportOffset = RvaToOffset(sections, exportRva);
            if (exportOffset is null || exportOffset.Value + 40 > data.Length) return Fail(fileName, "导出目录越界。");

            var numberOfNames = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(exportOffset.Value + 24));
            var namesRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(exportOffset.Value + 32));
            var namesOffset = RvaToOffset(sections, namesRva);
            if (numberOfNames > 0x8000 || namesOffset is null) return Fail(fileName, "导出名称表异常。");

            var capped = (int)Math.Min(numberOfNames, 0x8000);
            var names = new List<string>(capped);
            for (var i = 0; i < capped; i++)
            {
                var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(namesOffset.Value + 4 * i));
                var offset = RvaToOffset(sections, nameRva);
                if (offset is null || offset.Value >= data.Length) { names.Add($"<越界@{i}>"); continue; }
                var end = offset.Value;
                while (end < data.Length && data[end] != 0 && end - offset.Value < 256) end++;
                names.Add(Encoding.ASCII.GetString(data, offset.Value, end - offset.Value));
            }

            var missingDecode = DecodeExports.Where(export => !names.Contains(export, StringComparer.Ordinal)).ToArray();
            var missingEncode = EncodeExports.Where(export => !names.Contains(export, StringComparer.Ordinal)).ToArray();
            return WithVersion(path, fileName, machine, names.Count, missingDecode, missingEncode);
        }
        catch (Exception ex)
        {
            return Fail(fileName, ex.Message);
        }
    }

    private static FmodDllReport Fail(string fileName, string error) =>
        new(fileName, true, "-", null, null, 0, DecodeExports, EncodeExports, false, false, error);

    private static FmodDllReport WithVersion(string fullPath, string fileName, string machine, int exportCount,
        string[] missingDecode, string[] missingEncode)
    {
        string? fileVersion = null, productVersion = null;
        try
        {
            // Reads the version resource through the loader-free file-version
            // API; the DLL is never loaded into the process.
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            fileVersion = string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
            productVersion = string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion;
        }
        catch (Exception) { /* version resources are optional */ }
        return new FmodDllReport(fileName, true, machine, fileVersion, productVersion, exportCount,
            missingDecode, missingEncode, missingDecode.Length == 0, missingEncode.Length == 0, null);
    }

    private static (uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer)[] ParseSections(byte[] data, int start, int count)
    {
        var sections = new (uint, uint, uint, uint)[count];
        for (var i = 0; i < count; i++)
        {
            var at = start + 40 * i;
            if (at + 40 > data.Length) throw new InvalidDataException("节表越界。");
            sections[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 12)),   // VirtualAddress
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8)),    // VirtualSize
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 16)),   // SizeOfRawData
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 20)));  // PointerToRawData
        }
        return sections;
    }

    private static int? RvaToOffset((uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer)[] sections, uint rva)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
                return checked((int)(rva - section.VirtualAddress + section.RawPointer));
        }
        return null;
    }
}
