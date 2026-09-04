[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build-windows.ps1")
}

$builtExecutable = Join-Path $repoRoot "Windows\bin\AgentUsage.exe"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([Version](Get-Item -LiteralPath $builtExecutable).VersionInfo.FileVersion).ToString(3)
}

$distRoot = Join-Path $repoRoot "dist"
$packageName = "AgentUsage-Windows-v$Version"
$stageRoot = Join-Path $distRoot $packageName
$archivePath = Join-Path $distRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

$resolvedRepo = (Resolve-Path -LiteralPath $repoRoot).Path
foreach ($candidate in @($stageRoot, $archivePath, $checksumPath)) {
    $fullCandidate = [IO.Path]::GetFullPath($candidate)
    if (-not $fullCandidate.StartsWith($resolvedRepo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to package outside the repository: $fullCandidate"
    }
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
if (Test-Path -LiteralPath $checksumPath) { Remove-Item -LiteralPath $checksumPath -Force }
New-Item -ItemType Directory -Path $stageRoot | Out-Null

$windowsRoot = Join-Path $repoRoot "Windows"
$binaryRoot = Join-Path $windowsRoot "bin"
Copy-Item -LiteralPath (Join-Path $binaryRoot "AgentUsage.exe") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $binaryRoot "AgentUsage.exe.config") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $windowsRoot "Install-AgentUsage.ps1") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $windowsRoot "README.md") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $stageRoot "LICENSE.txt")

Compress-Archive -LiteralPath $stageRoot -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash  $packageName.zip" -Encoding ascii

$expectedVersion = [Version]$Version
$actualVersion = [Version](Get-Item -LiteralPath (Join-Path $stageRoot "AgentUsage.exe")).VersionInfo.FileVersion
if ($actualVersion.ToString(3) -ne $expectedVersion.ToString(3)) {
    throw "Packaged executable version $actualVersion does not match release version $expectedVersion."
}

Write-Host "Packaged $archivePath"
Write-Host "SHA256 $hash"
