# Limbus Mod Editor

Windows-only C#/.NET 8 WPF authoring workbench for Limbus Company mods. The
project is structured as a creator tool (Assets Studio + FMOD Studio workflow),
not as a launcher replacement.

## Current workflow (three steps, zero configuration)

1. **Launch** — the editor resumes your last project or opens a welcome dialog
   (new project / open project / recent projects). A contextual hint bar always
   shows the next action; with no project open, a full-screen guide covers the
   workspace.
2. **Create or open a project** — the new-mod wizard only asks for a mod name
   (project defaults to `<程序目录>/projects/<name>`). Game directory, Unity
   cache, mods directory and FMOD DLLs are auto-discovered and stored as
   **shared settings in the program directory**
   (`config/shared-config.json`) — every project reuses them; values from old
   `.lmeproj` files migrate into the shared config on first open (empty slots
   only, manual values are never overwritten).
3. **Scan** — every start runs an automatic scan (status bar shows live per-step progress):
   the four cache databases (`cache/unity-cache-index.db`, `bank-index.db`,
   `static-tables.db`, `text-index.db`) are created/repaired first, then the editor
   indexes every Unity cache bundle (`<outer>/<inner>/__data`) in reference mode (no
   files are copied) with a persistent incremental SQLite index, then refreshes the
   audio / static-table / lang-text indexes. Each step really enumerates the disk but
   only re-parses files whose signature changed (hot start is seconds); a missing
   prerequisite (no game directory yet) only skips that step. Damaged/unknown bundles
   are reported as per-entry diagnostics instead of aborting the scan. Scanned
   (reference-mode) assets live in the index, not in the project file —
   opening a project rehydrates them from the index in the background. The sidebar's
   "自动加载游戏资源" button remains as the manual re-scan entry.
4. **Edit** — the workspace uses a VS Code-style layout: an activity bar opens
   tabbed workbenches (assets / lang text / static data, Ctrl+Tab to cycle,
   fixed assets tab + closable tool tabs). The asset view defaults to a lazy
   folder-like tree built from Unity `m_Container` entries (real in-game asset
   paths), with a flat list one click away; both share one right-click menu.
   Search the merged asset index (debounced, background-filtered) with type /
   state / sort filters plus a default-on "container assets only" filter that
   hides technical support objects. Rows show friendly name, type, state and
   size — never cache keys or Path IDs. The right-hand preview follows the
   selection: decoded texture thumbnails (default "fit to window", Unity's
   bottom-up pixel rows normalised on decode and flipped back on write-back),
   readable text/JSON content, or audio audition (FSB → WAV through the FMOD DLLs,
   played locally, temp file removed afterwards); text/JSON assets open in a
   built-in editor whose saves are registered as ordinary reversible replacements. Replace textures, edit
   Sprite metadata / serialized fields; drag & drop is supported (packages/
   folders to import, an image onto a selected texture to replace it), plus
   double-click actions, Ctrl+F and per-asset undo. Editing a scanned bundle
   copies it into the project once
   (`sources/cache/<outer>_<inner>.bundle`) so game cache changes cannot
   corrupt work in progress.
5. **Export** — one click builds a real-loader Carra2 package
   (`<缓存外层键>/<内层键>/<pathId>.<类型表索引>`, per-entry XZ) from all
   edits, defaulting to the mods directory (`%APPDATA%\LimbusCompanyMods`).
   Before exporting, an advisor inspects what the project actually changed and
   lists the applicable outlets (one-click Carra2, Bank/Rebank audio, lang text
   patch, export wizard, multi-format, debug overlay) with a plain-language
   rationale and prerequisite for each; it appears automatically when several
   outlets apply and stays out of the way for pure Unity asset edits. The
   classic export wizard, multi-format export, debug overlay and lang /
   staticmod channels remain available. The two lang-text channels (export the
   RFC6902 patch, or apply it straight into the game's lang directory) live in the
   sidebar's export section; the text workbench itself browses the **active
   language folder's contents** (as pointed at by `lang/config.json`, e.g.
   `LLc-CN-LCTA`) — no `config.json` row, no extra wrapper level — while the patch
   document keeps its loader-compatible keys relative to the lang root.

Legacy flow (importing existing mod packages as project sources) is unchanged:
import copies packages into the project source area and records them in the
project file; export re-targets Carra/Carra2/Rebank/Bank/Lunartique.

## Format boundaries

- Unity bundle/SerializedFile parsing and replacer writes use
  [AssetsTools.NET](https://www.nuget.org/packages/AssetsTools.NET). The editor
  does not duplicate Unity's versioned binary parser.
- PNG/JPEG/BMP/GIF/TGA handling and atlas operations use ImageSharp.
- Carra object compression uses the MIT-licensed
  [Joveler.Compression.XZ](https://www.nuget.org/packages/Joveler.Compression.XZ)
  liblzma adapter on the Windows x64 runtime. The original XZ stream is decoded
  by SharpCompress; modified entries are encoded with liblzma.
- FMOD bank inspection only parses the small RIFF/FEV/SNDH index layer. Since
  2026-09 (user decision) the FMOD/FSBank DLLs are **distributed with the
  package**: `scripts/publish.ps1` copies legally obtained
  `fmod64.dll`/`fsbank64.dll`/`libfsbvorbis64.dll` (staged in
  `third_party/fmod/`, git-ignored) into the publish output's `fmod/` folder,
  and the editor auto-discovers them at startup in order
  `<程序目录>/fmod` → `<程序目录>` → the game's own runtime directory
  (owner-supplied game files, decode only). A manually configured directory in
  shared settings always wins. `NativeFmodAudioCodec` binds the documented
  FMOD/FSBank C ABI at runtime for FSB→WAV preview/export and WAV→FSB
  replacement. Complete raw FSB5 replacements can be assembled without any
  DLL. `.rebank` authoring remains available.
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

## Packaging

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

Publishes Release win-x64 to `artifacts/publish-win-x64` and copies the FMOD
DLLs from `third_party/fmod/` into `fmod/` inside the output (3/3 when staged).

## Verification

```text
dotnet build LimbusModEditor.slnx --no-restore
dotnet test LimbusModEditor.slnx --no-restore
```

The tests cover shared-config persistence/migration, FMOD discovery, cache
scan (reference mode + incremental index + per-entry fault tolerance),
cache-bundle materialization, `m_Container` container-path extraction and
display/tree/sort/filter behavior, export-outlet advising, text preview
(encoding detection / binary rejection / truncation) and text-asset editing,
one-click Carra2 export round-trips (real-sample gated), project persistence,
source materialization, directory and package import, Carra/Lunartique/Rebank
round trips, XZ compression round trips, bank probing, image/atlas operations,
overlay backup/restore, and game launch path validation.
