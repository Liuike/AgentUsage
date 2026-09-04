[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$windowsRoot = Join-Path $repoRoot "Windows"
$outputRoot = Join-Path $windowsRoot "bin"
$objectRoot = Join-Path $windowsRoot "obj"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found. Enable .NET Framework 4.7.2 or later."
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $objectRoot | Out-Null

# ICO files can contain PNG-encoded frames on every supported Windows version.
# Wrapping the existing 1024px artwork keeps the Windows executable on-brand.
$pngPath = Join-Path $repoRoot "Assets\AgentUsage-logo.png"
$iconPath = Join-Path $objectRoot "AgentUsage.ico"
$png = [System.IO.File]::ReadAllBytes($pngPath)
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]1)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length)
    $writer.Write([UInt32]22)
    $writer.Write($png)
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

$sources = @(
    (Join-Path $windowsRoot "Models.cs"),
    (Join-Path $windowsRoot "SettingsStore.cs"),
    (Join-Path $windowsRoot "CodexClient.cs"),
    (Join-Path $windowsRoot "AgentUsageForm.cs"),
    (Join-Path $windowsRoot "Program.cs")
)
$arguments = @(
    "/nologo",
    "/target:winexe",
    "/main:AgentUsage.Windows.Program",
    "/platform:anycpu",
    "/optimize+",
    "/win32manifest:$windowsRoot\app.manifest",
    "/win32icon:$iconPath",
    "/out:$outputRoot\AgentUsage.exe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Web.Extensions.dll",
    "/reference:System.Net.Http.dll"
) + $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw "Windows build failed with exit code $LASTEXITCODE." }

$probeArguments = @(
    "/nologo",
    "/target:exe",
    "/platform:anycpu",
    "/optimize+",
    "/main:AgentUsage.Windows.ProbeProgram",
    "/out:$outputRoot\AgentUsage.Probe.exe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Web.Extensions.dll"
) + @(
    (Join-Path $windowsRoot "Models.cs"),
    (Join-Path $windowsRoot "CodexClient.cs"),
    (Join-Path $windowsRoot "ProbeProgram.cs")
)
& $compiler $probeArguments
if ($LASTEXITCODE -ne 0) { throw "Windows probe build failed with exit code $LASTEXITCODE." }
& (Join-Path $outputRoot "AgentUsage.Probe.exe") --self-test
if ($LASTEXITCODE -ne 0) { throw "Windows self-test failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $windowsRoot "AgentUsage.exe.config") -Destination $outputRoot -Force
Write-Host "Built $outputRoot\AgentUsage.exe"
