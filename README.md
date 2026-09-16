# Limbus Mod Editor

Windows-only C#/.NET 8 authoring workbench for Limbus Company mods. The
project is structured as a creator tool (Assets Studio + FMOD Studio workflow),
not as a launcher replacement.

Since the 2026-09-15 refactor the UI is a **WebView2 thin host (C#, 8 source
files) + Vue 3 / TypeScript frontend** (`src/LimbusModEditor.Web/`, Vite +
vue-router + Pinia); Spine rendering (parsing, static preview, animation
playback) lives entirely in the frontend. The WPF page layer and the native
Spine runtime were deleted. See
[`docs/ARCH-WEBVIEW2-VUE.md`](docs/ARCH-WEBVIEW2-VUE.md) and
[`docs/WEB-IPC-CONTRACT.md`](docs/WEB-IPC-CONTRACT.md).

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

## Building from source

```text
# 0. prerequisites: .NET 8 SDK (global.json pins the SDK), Node 18+ / npm
# 1. backend
dotnet build LimbusModEditor.slnx --no-restore --nologo      # 0 warnings / 0 errors (warnings are errors)
dotnet test  LimbusModEditor.slnx --no-build  --nologo       # baseline: 864 green (94 domain + 74 format + 696 application)

# 2. frontend (Node project — deliberately NOT in LimbusModEditor.slnx)
npm --prefix src/LimbusModEditor.Web install                 # first time only
npm --prefix src/LimbusModEditor.Web run build               # vite build -> src/LimbusModEditor.Web/dist/ (+ licenses copied to dist/licenses/)

# 3. publish (the frontend must be built first — see below)
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
```

The App project's `PublishFrontend` MSBuild target copies
`src/LimbusModEditor.Web/dist/**` (including `dist/licenses/`) into the
published `wwwroot/`, so step 2 **must** run before step 3 — a missing `dist/`
fails the publish with a Chinese error instead of silently producing a package
without a UI. Expected result: `wwwroot/` is byte-identical to `dist/`
(52 files on 2026-09-17) and `wwwroot/licenses/` holds `spine-core-LICENSE` and
`spine-webgl-LICENSE`.

`logs/lme.py` (`build` / `test` / `publish`) wraps the dotnet commands only — it
does **not** run npm. For a one-shot "frontend + backend" release either run the
three blocks above, or use
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1`, which
additionally copies the FMOD DLLs from `third_party/fmod/` into the output
`fmod/` folder.

## Packaging

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

Publishes Release win-x64 to `artifacts/publish-win-x64` and copies the FMOD
DLLs from `third_party/fmod/` into `fmod/` inside the output (3/3 when staged).

## Documentation

| Document | What is in it |
|---|---|
| [`docs/README.md`](docs/README.md) | Documentation index: which file to read for what |
| [`docs/STATUS.md`](docs/STATUS.md) | Current state, verification baseline, project rules, real-environment facts, open tasks, known limits |
| [`docs/CODE-STRUCTURE.md`](docs/CODE-STRUCTURE.md) | Layering and dependency direction, directory map, UI shell, runtime flows, cross-file invariants, on-disk layout, "where do I change X" table |
| [`docs/PROJECT-INDEX.md`](docs/PROJECT-INDEX.md) | **Per-file functional index** (every source/test file: purpose, key types, change-locality hints) plus the `AssetRecord.Metadata` key dictionary and a symptom → file lookup |
| [`docs/USAGE.md`](docs/USAGE.md) | User manual: workflows, shortcuts, troubleshooting, format boundaries |
| [`docs/REALDATA-VERIFY.md`](docs/REALDATA-VERIFY.md) | Real-data verification of the write-back chain (including the silently-discarded-changes defect and its root cause) |
| [`docs/REVIEW.md`](docs/REVIEW.md) | Self-review: findings, design-change risks, quantified performance baseline |
| [`docs/ARCHIVE-DESIGN-NOTES.md`](docs/ARCHIVE-DESIGN-NOTES.md) | Design decisions inherited from the (now archived) per-round plans that still constrain the code |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Implementation history and forward-looking direction (long-form) |

Analysis convention: structure and index documents are produced by **reading code**, not by
running probes or taking screenshots. The per-round execution plans (`docs/plans/plan-*.md`) and
`docs/NEXT-STEPS.md` were archived on 2026-09-12 after every item shipped — history is in git
(`git show <commit>:docs/plans/<file>`).

## Verification

```text
dotnet build LimbusModEditor.slnx --no-restore --nologo
dotnet test  LimbusModEditor.slnx --no-build  --nologo
npm --prefix src/LimbusModEditor.Web run build          # frontend (must precede publish)
```

The tests cover shared-config persistence/migration, FMOD discovery, cache
scan (reference mode + incremental index + per-entry fault tolerance),
cache-bundle materialization, `m_Container` container-path extraction and
display/tree/sort/filter behavior, export-outlet advising, text preview
(encoding detection / binary rejection / truncation) and text-asset editing,
one-click Carra2 export round-trips (real-sample gated), project persistence,
source materialization, directory and package import, Carra/Lunartique/Rebank
round trips, XZ compression round trips, bank probing, image/atlas operations,
overlay backup/restore, and game launch path validation — plus, since the
refactor, the IPC gateway/envelope/paging/cancel contract, the wiki page store
and query service, and the relation authority engine.

Current baseline (2026-09-17, measured): **864 tests green** (94 domain +
74 format + 696 application) with `dotnet build` at 0 warnings / 0 errors.
Per-file test coverage, real-data gating variables and known flakes are
documented in [`docs/PROJECT-INDEX.md`](docs/PROJECT-INDEX.md) §11; the full
delivery report (evidence, known gaps, manual smoke checklist) is
[`docs/FINAL-DELIVERY.md`](docs/FINAL-DELIVERY.md).
