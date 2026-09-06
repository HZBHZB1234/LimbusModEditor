# Publish script: dotnet publish + bundle FMOD DLLs into the package.
#
# The FMOD binaries (fmod64.dll / fsbank64.dll / libfsbvorbis64.dll) are NOT
# stored in git. Put legally obtained copies into third_party/fmod/ (or pass
# -FmodSource); this script copies them into <output>/fmod so they ship with
# the package. The editor auto-discovers them at startup (program dir\fmod ->
# program dir -> game directory), so users need no configuration.
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

Write-Host "== 1/3 dotnet publish (Release, win-x64, framework-dependent) =="

dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj `
    -c Release -r win-x64 --self-contained false `
    -o $OutputDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

Write-Host "== 2/3 Bundle FMOD DLLs =="

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

Write-Host "== 3/3 Verify output =="

$exe = Join-Path $OutputDir "LimbusModEditor.App.exe"
if (-not (Test-Path $exe)) { throw "Publish output is missing LimbusModEditor.App.exe." }
Write-Host "Publish complete -> $OutputDir"
