[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $windowsRoot
$bundle = Join-Path $repoRoot "dist\.dependencies-$RuntimeIdentifier"
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'Arm64' } else { 'X64' }
$manifest = Get-Content -LiteralPath (Join-Path $windowsRoot 'dependencies.json') -Raw | ConvertFrom-Json
$packages = @($manifest | Where-Object { $_.architecture -eq $architecture })
if (@($packages).Count -ne 3) { throw "Incomplete dependency manifest for $RuntimeIdentifier" }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null

function Get-VerifiedArchive($package) {
    $name = [IO.Path]::GetFileName(([uri]$package.url).AbsolutePath)
    $destination = Join-Path $bundle $name
    if ((Test-Path -LiteralPath $destination) -and
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $package.sha256) { return }

    $partial = "$destination.partial"
    try {
        Write-Host "Downloading $name for the offline installer"
        Invoke-WebRequest -Uri $package.url -OutFile $partial -UseBasicParsing -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $package.sha256) {
            throw "SHA-256 mismatch for $name"
        }
        Move-Item -LiteralPath $partial -Destination $destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

foreach ($package in $packages) { Get-VerifiedArchive $package }

# Keep upstream copyright and license texts with the redistributed executables.
$licenses = @{
    'tun2socks-LICENSE.txt' = 'https://raw.githubusercontent.com/xjasonlyu/tun2socks/v2.6.0/LICENSE'
    'sing-box-LICENSE.txt' = 'https://raw.githubusercontent.com/SagerNet/sing-box/v1.13.12/LICENSE'
}
foreach ($name in $licenses.Keys) {
    $path = Join-Path $bundle $name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri $licenses[$name] -OutFile "$path.partial" -UseBasicParsing -TimeoutSec 30
        Move-Item -LiteralPath "$path.partial" -Destination $path -Force
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $archivePath = Join-Path $bundle ([IO.Path]::GetFileName(([uri]$package.url).AbsolutePath))
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($null -eq $archive.GetEntry($package.archiveEntry)) {
            throw "Missing $($package.archiveEntry) in $archivePath"
        }
        if ($package.id -eq 'wintun') {
            $license = $archive.GetEntry('wintun/LICENSE.txt')
            if ($null -eq $license) { throw 'Wintun license is missing from the archive' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($license, (Join-Path $bundle 'wintun-LICENSE.txt'), $true)
        }
    }
    finally { $archive.Dispose() }
}

@'
Third-party dependencies distributed without modification:

tun2socks v2.6.0 (MIT): https://github.com/xjasonlyu/tun2socks/tree/v2.6.0
Source: https://github.com/xjasonlyu/tun2socks/archive/refs/tags/v2.6.0.zip
Wintun 0.14.1: https://www.wintun.net/
sing-box v1.13.12 (GPL-3.0-or-later): https://github.com/SagerNet/sing-box/tree/v1.13.12
Source: https://github.com/SagerNet/sing-box/archive/refs/tags/v1.13.12.zip

See the accompanying *-LICENSE.txt files for copyright and license terms.
'@ | Set-Content -LiteralPath (Join-Path $bundle 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8

Write-Host "Verified dependency bundle: $bundle"
