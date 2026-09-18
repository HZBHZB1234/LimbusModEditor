# Publish script: dotnet publish + bundle FMOD DLLs into the package.
#
# The FMOD binaries (fmod64.dll / fsbank64.dll / libfsbvorbis64.dll) are NOT
# stored in git. Put legally obtained copies into third_party/fmod/ (or pass
# -FmodSource); this script copies them into <output>/fmod so they ship with
# the package. The editor auto-discovers them at startup (program dir\fmod ->
# program dir -> game directory), so users need no configuration.
#
# The output is a CLEAN package: dotnet publish only emits program artifacts,
# so no cache/config/logs/projects/WebView2Data is copied in. Those directories
# are created by the program itself on first run (AppEnvironment points them at
# the program directory; WikiMediaResolver and WebView2 create their own).
# They hold machine-local state (index DBs, user projects, logs, browser
# profile) and must NOT ship -- which is why the script never reuses a dirty
# output directory as a package source.
#
# Point -OutputDir at a fresh directory for a package that has never been run.
# If the directory DOES contain run residue, the script clears the
# regenerable parts (cache/logs/WebView2Data/wwwroot/data) and refuses to touch
# config/ or projects/, which may hold real user data.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
#   ... -OutputDir <dir>      (default artifacts/publish-win-x64)
#   ... -FmodSource <dir>     (default third_party/fmod)

param(
    [string]$OutputDir = "artifacts/publish-win-x64",
    [string]$FmodSource = "third_party/fmod"
)

$ErrorActionPreference = "Stop"

# Runtime state that must never be shipped even if it exists in the output
# directory (it is recreated on first run). Kept here so the "clean" contract
# is explicit rather than implied by the publish step alone.
$runtimeStateDirs = @("cache", "config", "logs", "projects", "WebView2Data", "wwwroot/data")

Write-Host "== 0/4 Remove stale runtime state from the output directory =="

foreach ($relative in $runtimeStateDirs) {
    $stale = Join-Path $OutputDir $relative
    if (Test-Path $stale) {
        # cache/ and logs/ only ever appear here because someone RAN the editor
        # out of this directory, so removing them is safe and intended. config/
        # and projects/ can hold real user work (shared-config.json remembers
        # the game directory; projects/ holds .lmeproj files) -- deleting those
        # silently would be data loss. Refuse and let the caller decide.
        if ($relative -in @("config", "projects")) {
            throw "Refusing to delete $relative in ${OutputDir}: it may hold real user data. " +
                  "Publish to a fresh -OutputDir (or remove it yourself) to get a clean package."
        }
        Remove-Item $stale -Recurse -Force
        Write-Host "   Removed $relative (runtime state, recreated on first run)"
    }
}

Write-Host "== 1/4 dotnet publish (Release, win-x64, framework-dependent) =="

dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj `
    -c Release -r win-x64 --self-contained false `
    -o $OutputDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

Write-Host "== 2/4 Bundle FMOD DLLs =="

$fmodDir = Join-Path $OutputDir "fmod"
$fmodFiles = @("fmod64.dll", "fsbank64.dll", "libfsbvorbis64.dll")
$copied = 0
if (Test-Path $FmodSource) {
    New-Item -ItemType Directory -Force -Path $fmodDir | Out-Null
    foreach ($name in $fmodFiles) {
        $src = Join-Path $FmodSource $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $fmodDir $name) -Force
            $copied++
        }
    }
}
if ($copied -gt 0) {
    Write-Host "   Copied $copied/$($fmodFiles.Count) FMOD DLLs -> $fmodDir"
    $missing = $fmodFiles | Where-Object { -not (Test-Path (Join-Path $fmodDir $_)) }
    if ($missing) {
        Write-Warning "   Missing: $($missing -join ', ') -- audio decode/encode will be limited."
    }
} else {
    Write-Warning "   $FmodSource not found; package will NOT contain FMOD DLLs."
    Write-Warning "   Put fmod64.dll / fsbank64.dll / libfsbvorbis64.dll into third_party/fmod/ and repack."
}

Write-Host "== 3/4 Verify output =="

$exe = Join-Path $OutputDir "LimbusModEditor.App.exe"
if (-not (Test-Path $exe)) { throw "Publish output is missing LimbusModEditor.App.exe." }
Write-Host "   $exe"

Write-Host "== 4/4 Verify package is clean =="

foreach ($relative in $runtimeStateDirs) {
    if (Test-Path (Join-Path $OutputDir $relative)) {
        throw "Clean-package check failed: $relative is present in ${OutputDir} (runtime state must not ship)."
    }
}
Write-Host "   No runtime state (cache/config/logs/projects/WebView2Data/wwwroot/data)."

Write-Host "Publish complete -> $OutputDir"
