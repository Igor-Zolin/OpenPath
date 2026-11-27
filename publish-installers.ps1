[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$InnoCompiler,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '1.7.0',
    [switch]$UseExistingPayload
)

$ErrorActionPreference = 'Stop'

if (-not $InnoCompiler) {
    $InnoCompiler = (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1).Source
}
if (-not $InnoCompiler) {
    $compilerRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))
    foreach ($root in $compilerRoots | Where-Object { $_ }) {
        foreach ($innoVersion in @('7', '6')) {
            $candidate = Join-Path $root "Inno Setup $innoVersion\ISCC.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $InnoCompiler = $candidate
                break
            }
        }
        if ($InnoCompiler) { break }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6.3 or later is required. Install it or pass -InnoCompiler with the path to ISCC.exe.'
}

$compiler = (Get-Item -LiteralPath $InnoCompiler).FullName
$dist = Join-Path $PSScriptRoot 'dist'
$recipe = Join-Path $PSScriptRoot 'windows\installer\SshEnv.iss'
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64os' }

if (-not $UseExistingPayload) {
    & (Join-Path $PSScriptRoot "publish-winui.ps1") -RuntimeIdentifier $RuntimeIdentifier -Version $Version
}

$portable = Join-Path $dist "OpenPath-$RuntimeIdentifier.exe"
$info = (Get-Item -LiteralPath $portable).VersionInfo
if ($info.FileVersion -ne "$Version.0") {
    throw "Expected version $Version.0 in $portable, found $($info.FileVersion)."
}
$setupName = "OpenPath-$RuntimeIdentifier-Setup"
& $compiler '/Qp' "/O$dist" "/F$setupName" "/DArchitecture=$architecture" "/DRuntimeIdentifier=$RuntimeIdentifier" "/DPayload=$portable" $recipe
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed to build $setupName with exit code $LASTEXITCODE."
}

Write-Host "Installer created: $(Join-Path $dist "$setupName.exe")"
