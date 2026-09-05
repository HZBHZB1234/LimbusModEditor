# Limbus Mod Editor

Windows-only C#/.NET 8 WPF authoring workbench for Limbus Company mods. The
project is structured as a creator tool (Assets Studio + FMOD Studio workflow),
not as a launcher replacement.

## Current workflow

1. Create an `.lmeproj` project. The project creates `sources`, `edits`,
   `previews`, `builds`, `backups` and `logs` folders.
2. Import `.carra`, `.carra2`, `.rebank`, `.bank`, Lunartique ZIPs, Unity
   `.bundle` files, or a normal resource directory. Imported archives and
   directories are copied into the project source area and recorded in the
   project file.
3. Search the merged asset index, inspect Unity object metadata, preview and
   split/repack supported images, inspect/edit Sprite rect/pivot/border metadata,
   import standalone Unity SerializedFiles (`.assets`) as editable object trees,
   inspect/edit Unity serialized fields (validated primitives, enum values,
   PPtr pointers, vector arrays and byte-array info, with per-field
   original/new/diff view),
   export indexed Bank audio to WAV when user-provided FMOD DLLs are configured,
   edit text/JSON resources, and record replacements in `edits/assets`.
4. Export using the source format or choose the Carra/Carra2/Rebank/Lunartique
   output supported by the current handler. Lunartique Installation pairs can
   be converted to object-level Carra/Carra2 output by comparing Unity object
   payload hashes; unsupported deletion-only or malformed resources are
   reported instead of copied blindly. A project source can be reused after
   reopening the editor.
5. Build a debug overlay, apply it to the configured game directory with a
   backup, launch `LimbusCompany.exe`, and restore files when the editor closes.

## Format boundaries

- Unity bundle/SerializedFile parsing and replacer writes use
  [AssetsTools.NET](https://www.nuget.org/packages/AssetsTools.NET). The editor
  does not duplicate Unity's versioned binary parser.
- PNG/JPEG/BMP/GIF/TGA handling and atlas operations use ImageSharp.
- Carra object compression uses the MIT-licensed
  [Joveler.Compression.XZ](https://www.nuget.org/packages/Joveler.Compression.XZ)
  liblzma adapter on the Windows x64 runtime. The original XZ stream is decoded
  by SharpCompress; modified entries are encoded with liblzma.
- FMOD bank inspection only parses the small RIFF/FEV/SNDH index layer. The
  runtime loader can probe separate `fmod64.dll` and `fsbank64.dll` files from
  the user-selected directory, but no proprietary binaries are shipped.
  `NativeFmodAudioCodec` binds the documented FMOD/FSBank C ABI at runtime for
  FSB→WAV preview/export and WAV→FSB replacement. FSB/WAV codecs and
  WAV-based `.bank` rebuilding therefore require a legitimately obtained,
  compatible FMOD/FSBANK DLL. Complete raw FSB5 replacements can be assembled
  without that DLL; no proprietary codec is reverse engineered or redistributed
  by this repository. `.rebank` authoring remains available.
- Lunartique is represented as paired `Installation`/`Uninstallation` data
  files. Sprite metadata editing is available. Texture2D PNG preview/replacement
  supports Unity RGB24, RGBA32/BGRA32, DXT1 and DXT5 (DXT encoding is lossy);
  ETC/ASTC, atlas mesh rewriting, and advanced AudioClip editors remain separate
  milestones.

## CLI

```text
LimbusModEditor.Cli.exe probe <package>
LimbusModEditor.Cli.exe import <project.lmeproj> <file-or-directory>
LimbusModEditor.Cli.exe export <project.lmeproj> <output> [source]
```

## Verification

```text
dotnet build LimbusModEditor.slnx --no-restore
dotnet test LimbusModEditor.slnx --no-restore
```

The tests cover project persistence, source materialization, directory and
package import, Carra/Lunartique/Rebank round trips, XZ compression round trips,
bank probing, image/atlas operations, overlay backup/restore, and game launch
path validation.
