[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build-windows.ps1")
}

$sourceRoot = Join-Path $repoRoot "Windows\bin"
Copy-Item -LiteralPath (Join-Path $repoRoot "Windows\Install-AgentUsage.ps1") -Destination $sourceRoot -Force
& (Join-Path $sourceRoot "Install-AgentUsage.ps1")
