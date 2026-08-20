# Regenerates Marco.Core\Lifecycle\Resources\os-eol.json from the endoflife.date API.
# Usage:  .\build\refresh-eol.ps1        (then review the diff and commit)
# The table ships embedded in Marco.Core; its "Updated" stamp is shown wherever lifecycle data appears,
# so refreshing it periodically keeps EOL verdicts honest. Windows builds are mapped by hand below because
# endoflife.date keys Windows cycles by release name, not build number.
$ErrorActionPreference = 'Stop'

$out = Join-Path $PSScriptRoot "..\Marco.Core\Lifecycle\Resources\os-eol.json"

function Api($product) {
    Invoke-RestMethod "https://endoflife.date/api/$product.json" -Headers @{ Accept = 'application/json' }
}

# release-name -> build-number map (extend when new releases ship).
$clientBuilds = @{
    '1809' = '17763'; '1909' = '18363'; '2004' = '19041'; '20H2' = '19042'; '21H1' = '19043'
    '21H2' = '19044'; '22H2' = '19045'
    '21H2-W11' = '22000'; '22H2-W11' = '22621'; '23H2' = '22631'; '24H2' = '26100'; '25H2' = '26200'
}
$serverBuilds = @{ '2012-R2' = '9600'; '2016' = '14393'; '2019' = '17763'; '2022' = '20348'; '2025' = '26100' }

$windows = @()
foreach ($c in Api 'windows') {
    $key = if ($c.cycle -match '(21H2|22H2)' -and $c.cycle -match '11') { "$($Matches[1])-W11" } else { ($c.cycle -split ' ')[-1] }
    if (-not $clientBuilds.ContainsKey($key)) { continue }
    $product = if ($c.cycle -match '11') { 'Windows 11' } else { 'Windows 10' }
    $windows += [ordered]@{
        Product = $product; Release = ($c.cycle -split ' ')[-1] -replace '\(.*',''; Build = $clientBuilds[$key]; Kind = 'client'
        EosHomePro = $c.eol; EosEnterprise = if ($c.PSObject.Properties['extendedSupport']) { $c.extendedSupport } else { $c.eol }
    }
}
foreach ($c in Api 'windows-server') {
    $key = $c.cycle -replace ' ', '-'
    if (-not $serverBuilds.ContainsKey($key)) { continue }
    $windows += [ordered]@{
        Product = "Windows Server $($c.cycle)"; Build = $serverBuilds[$key]; Kind = 'server'
        EosMainstream = $c.support; EosExtended = $c.eol
    }
}

$linux = @()
$distros = @(
    @{ Api = 'ubuntu'; Name = 'Ubuntu' }, @{ Api = 'debian'; Name = 'Debian' },
    @{ Api = 'rhel'; Name = 'Red Hat Enterprise Linux' }, @{ Api = 'almalinux'; Name = 'AlmaLinux' },
    @{ Api = 'rocky-linux'; Name = 'Rocky Linux' })
foreach ($d in $distros) {
    foreach ($c in Api $d.Api) {
        $eos = if ($c.eol -is [string]) { $c.eol } else { $null }
        if (-not $eos) { continue }
        $ext = if ($c.PSObject.Properties['extendedSupport'] -and $c.extendedSupport -is [string]) { $c.extendedSupport } else { $null }
        $linux += [ordered]@{ Distro = $d.Name; VersionMatch = "$($c.cycle)"; Eos = $eos; EosExtended = $ext }
    }
}

$doc = [ordered]@{
    SchemaVersion = 1
    Updated = (Get-Date -Format 'yyyy-MM-dd')
    Windows = $windows
    Linux = $linux
}
$json = $doc | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Resolve-Path $out), $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $out — review the diff before committing (the API's shape drifts; the hand-curated file is the fallback)."
