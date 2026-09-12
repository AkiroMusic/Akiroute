# Downloads the xray-core engine assets into Akiroute/Assets (engine + rules).
# Source: official XTLS/Xray-core GitHub releases (Xray-windows-64.zip).
#
# Integrity: the release's published digest file (Xray-windows-64.zip.dgst) is
# downloaded alongside the zip and the computed SHA256 must match one of its
# entries — a corrupted or tampered download aborts the script instead of
# shipping a broken engine. Pin $XrayVersion to a fixed release for reproducible
# builds; 'latest' always re-verifies the digest but tracks upstream.
#
# Idempotent: skips re-download when the zip already exists and its hash matches
# the digest file.
param(
    # Pinned xray-core release tag (e.g. 'v25.8.29'); 'latest' tracks upstream.
    [string]$XrayVersion = 'latest'
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$engine  = Join-Path $root 'Akiroute\Assets\engine'
$rules   = Join-Path $root 'Akiroute\Assets\rules'
$extract = Join-Path $PSScriptRoot 'xray-extract'
$zip     = Join-Path $PSScriptRoot 'xray-windows-64.zip'
$dgst    = Join-Path $PSScriptRoot 'xray-windows-64.zip.dgst'

New-Item -ItemType Directory -Force -Path $engine, $rules | Out-Null

$base = if ($XrayVersion -eq 'latest') {
    'https://github.com/XTLS/Xray-core/releases/latest/download'
} else {
    "https://github.com/XTLS/Xray-core/releases/download/$XrayVersion"
}

if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 1000000) {
    $zipUrl = "$base/Xray-windows-64.zip"
    Write-Host "Downloading $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing
} else {
    Write-Host "Zip already present, reusing: $zip"
}

# --- Integrity verification against the release's published digest file. ---
Write-Host "Downloading digest file for verification ..."
Invoke-WebRequest -Uri "$base/Xray-windows-64.zip.dgst" -OutFile $dgst -UseBasicParsing

$actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$published = (Get-Content $dgst) |
    Where-Object { $_ -match '^[0-9a-fA-F]{64}' } |
    ForEach-Object { ($_ -split '\s+')[0].ToLowerInvariant() }

if ($published -notcontains $actual) {
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    throw ("SHA256 mismatch: downloaded zip hash {0} is not in the release digest file. " +
           "The download was deleted; check your network or pin a known release via " +
           "-XrayVersion.") -f $actual
}
Write-Host "SHA256 verified: $actual"

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

# Cleanup extraction dir (keep the zip + digest for idempotent re-runs)
Remove-Item $extract -Recurse -Force
Write-Host 'Done.'
