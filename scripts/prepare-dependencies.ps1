[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $repoRoot 'windows\scripts\prepare-dependencies.ps1') @PSBoundParameters
