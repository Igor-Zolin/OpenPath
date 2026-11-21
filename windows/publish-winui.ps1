[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$stagingName = ".publish-winui-$RuntimeIdentifier-$([Guid]::NewGuid().ToString('N'))"
$publishDir = [IO.Path]::GetFullPath((Join-Path $distRoot $stagingName))
if (-not $publishDir.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "WinUI publish directory is outside dist: $publishDir"
}

$project = Join-Path $PSScriptRoot "src\SshEnv.WinUI\SshEnv.WinUI.csproj"
$bundleDirectory = Join-Path $distRoot ".dependencies-$RuntimeIdentifier"
$executablePath = Join-Path $distRoot "OpenPath-$RuntimeIdentifier.exe"
$platform = if ($RuntimeIdentifier -eq "win-arm64") { "ARM64" } else { "x64" }

& (Join-Path $PSScriptRoot "scripts\prepare-dependencies.ps1") -RuntimeIdentifier $RuntimeIdentifier

try {
    dotnet publish $project `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        "-p:Platform=$platform" `
        "-p:Version=$Version" `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:DependencyBundleDirectory=$bundleDirectory" `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "WinUI publish failed with exit code $LASTEXITCODE."
    }

    $executable = Join-Path $publishDir ([IO.Path]::GetFileName($executablePath))
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "WinUI publish did not produce $([IO.Path]::GetFileName($executablePath))."
    }

    $looseFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object FullName -ne $executable)
    if ($looseFiles.Count -gt 0) {
        throw "WinUI publish left files outside the executable: $($looseFiles.Name -join ', ')."
    }

    Move-Item -LiteralPath $executable -Destination $executablePath -Force
    Write-Host "Executable created: $executablePath"
}
finally {
    if (Test-Path -LiteralPath $publishDir -PathType Container) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
}
