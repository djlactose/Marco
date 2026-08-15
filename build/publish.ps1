<#
.SYNOPSIS
  Publish Marco as a single, self-contained, portable Windows x64 executable.

.DESCRIPTION
  Produces one .exe with the .NET runtime embedded — no framework prerequisite on the operator's machine and
  nothing at all on the targets (Marco is agentless). Size is intentionally not optimized; portability is the goal.

  PACKAGING SPIKE OUTCOME: WPF single-file publish always emits 5 native DLLs beside the exe
  (D3DCompiler_47_cor3, PenImc_cor3, PresentationNative_cor3, wpfgfx_cor3, vcruntime140_cor3) unless
  IncludeNativeLibrariesForSelfExtract=true, which embeds them into the exe and extracts them to a temp cache at
  runtime. Since the goal is ONE sendable exe, that flag is ON by default here. Pass -NoExtract for the
  multi-file folder variant (no runtime extraction, but no longer a single file).

  Signing is a separate step (build/sign.ps1) and is intentionally not required here — but an unsigned scanner
  that prompts for credentials will be quarantined by EDR, so ship a signed build for real distribution.

.EXAMPLE
  pwsh build/publish.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoExtract,
    [string]$OutputDir = "$PSScriptRoot\..\artifacts",
    # Overrides the version from Directory.Build.props (CI passes X.Y.Z-beta.N for develop builds).
    [string]$Version
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\Marco.App\Marco.App.csproj"
$publishDir = Join-Path $OutputDir "publish"

Write-Host "Publishing Marco ($Configuration, $Runtime, single-file, self-contained)..." -ForegroundColor Cyan

$props = @(
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",    # auto-update downloads every release per machine, so size matters more than the slightly slower first launch
    "-p:DebugType=embedded",
    "-p:SatelliteResourceLanguages=en"
)
if ($Version) { $props += "-p:Version=$Version" }
if ($NoExtract) {
    Write-Host "  (multi-file: WPF native libs emitted beside the exe, no runtime extraction)" -ForegroundColor Yellow
} else {
    # Spike-confirmed: required for a TRUE single exe with WPF. Extracts native libs to a temp cache at runtime.
    $props += "-p:IncludeNativeLibrariesForSelfExtract=true"
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    @props

$exe = Join-Path $publishDir "Marco.exe"
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }

$size = (Get-Item $exe).Length / 1MB
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$sha = Join-Path $publishDir "Marco.exe.sha256"
"$hash  Marco.exe" | Out-File -FilePath $sha -Encoding ascii

Write-Host ""
Write-Host "Output : $exe" -ForegroundColor Green
Write-Host ("Size   : {0:N1} MB" -f $size)
Write-Host "SHA256 : $hash"
Write-Host "        (written to $sha)"
Write-Host ""
Write-Host "Next: sign it with build/sign.ps1 before distributing (unsigned scanners get quarantined by EDR)." -ForegroundColor Yellow
