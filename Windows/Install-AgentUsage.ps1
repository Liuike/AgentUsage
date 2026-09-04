[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$packageRoot = $PSScriptRoot
$executable = Join-Path $packageRoot "AgentUsage.exe"
$configuration = Join-Path $packageRoot "AgentUsage.exe.config"
if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $configuration)) {
    throw "Run this installer from the extracted AgentUsage Windows release folder."
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\AgentUsage"
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -LiteralPath $executable -Destination $installRoot -Force
Copy-Item -LiteralPath $configuration -Destination $installRoot -Force

$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "AgentUsage.lnk"
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installRoot "AgentUsage.exe"
$shortcut.WorkingDirectory = $installRoot
$shortcut.Description = "Codex usage in the Windows notification area"
$shortcut.Save()

Start-Process -FilePath (Join-Path $installRoot "AgentUsage.exe") -ArgumentList "--show"
Write-Host "Installed AgentUsage to $installRoot"
