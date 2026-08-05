# Downloads the xray-core engine assets into Akiroute/Assets (engine + rules).
# Source: official XTLS/Xray-core GitHub latest release (Xray-windows-64.zip).
# Idempotent: skips re-download when the zip already exists and is non-trivial in size.
$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $root 'Akiroute\Assets\engine'
$rules  = Join-Path $root 'Akiroute\Assets\rules'
$extract = Join-Path $PSScriptRoot 'xray-extract'
$zip   = Join-Path $PSScriptRoot 'xray-windows-64.zip'

New-Item -ItemType Directory -Force -Path $engine, $rules | Out-Null

$url = 'https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip'
if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 1000000) {
    Write-Host "Downloading $url ..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
} else {
    Write-Host "Zip already present, reusing: $zip"
}

Write-Host 'Extracting ...'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $extract -Force

$map = @{
    'xray.exe'    = $engine
    'wintun.dll'  = $engine
    'geoip.dat'   = $rules
    'geosite.dat' = $rules
}
foreach ($name in $map.Keys) {
    $srcFile = Join-Path $extract $name
    if (Test-Path $srcFile) {
        Copy-Item $srcFile (Join-Path $map[$name] $name) -Force
        Write-Host ("OK  " + (Join-Path $map[$name] $name) + "  " + (Get-Item $srcFile).Length + " bytes")
    } else {
        Write-Warning "Asset not found in archive: $name"
    }
}

# Cleanup extraction dir (keep the zip for idempotent re-runs)
Remove-Item $extract -Recurse -Force
Write-Host 'Done.'
