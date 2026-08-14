<#
.SYNOPSIS
  Authenticode-sign the published Marco.exe with a timestamp, and emit its SHA-256.

.DESCRIPTION
  An unsigned network scanner that prompts for credentials is behaviorally indistinguishable from malware to EDR
  and will be quarantined. Signing is what makes Marco distributable. This wraps signtool; if signtool or the
  certificate is unavailable it WARNS and skips rather than failing the build (as on a dev box without the SDK).

.PARAMETER Exe
  Path to the published Marco.exe (default: artifacts/publish/Marco.exe).

.PARAMETER CertThumbprint
  Thumbprint of a code-signing cert in the current user's store. If omitted, uses -PfxPath.

.PARAMETER PfxPath / -PfxPassword
  Alternatively sign from a PFX file.

.PARAMETER TimestampUrl
  RFC 3161 timestamp authority (default: DigiCert).

.EXAMPLE
  pwsh build/sign.ps1 -CertThumbprint ABC123...
#>
param(
    [string]$Exe = "$PSScriptRoot\..\artifacts\publish\Marco.exe",
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $found = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    return $null
}

if (-not (Test-Path $Exe)) { throw "Executable not found: $Exe. Run build/publish.ps1 first." }

$signtool = Find-SignTool
if (-not $signtool) {
    Write-Warning "signtool.exe not found (Windows SDK not installed). SKIPPING signing."
    Write-Warning "Install the Windows SDK and re-run to produce a distributable (EDR-safe) binary."
} else {
    $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
    if ($CertThumbprint) { $args += @("/sha1", $CertThumbprint) }
    elseif ($PfxPath)    { $args += @("/f", $PfxPath); if ($PfxPassword) { $args += @("/p", $PfxPassword) } }
    else { Write-Warning "No -CertThumbprint or -PfxPath given; signtool will try to auto-select a cert." }
    $args += $Exe

    Write-Host "Signing $Exe ..." -ForegroundColor Cyan
    & $signtool @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
    Write-Host "Signed and timestamped." -ForegroundColor Green
}

# Always (re)emit the SHA-256 so releases are verifiable.
$hash = (Get-FileHash $Exe -Algorithm SHA256).Hash
"$hash  $(Split-Path $Exe -Leaf)" | Out-File "$Exe.sha256" -Encoding ascii
Write-Host "SHA256 : $hash"
