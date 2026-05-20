[CmdletBinding()]
param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release",

  [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot "GhCLI.sln"
$PluginSource = Join-Path $RepoRoot "src\GhCLI.Plugin\bin\$Configuration\net7.0-windows"
$PluginTarget = Join-Path $env:APPDATA "Grasshopper\Libraries\GhCLI"

$RequiredPluginFiles = @(
  "GhCLI.Plugin.gha",
  "GhCLI.Plugin.deps.json",
  "GhCLI.Core.dll",
  "GhCLI.Protocol.dll"
)

if (-not $NoBuild) {
  dotnet build $Solution --configuration $Configuration --nologo
}

New-Item -ItemType Directory -Force -Path $PluginTarget | Out-Null

foreach ($File in $RequiredPluginFiles) {
  $From = Join-Path $PluginSource $File
  if (-not (Test-Path -LiteralPath $From)) {
    throw "Missing plugin build output: $From"
  }

  try {
    Copy-Item -LiteralPath $From -Destination $PluginTarget -Force
  }
  catch {
    $Message = $_.Exception.Message
    throw "Could not replace '$File' in '$PluginTarget'. Close Rhino/Grasshopper if it is running, then retry. Original error: $Message"
  }
}

Write-Host "Installed GhCLI plugin to $PluginTarget"
Write-Host "Copied required files only: $($RequiredPluginFiles -join ', ')"
Write-Host "Restart Rhino, then verify with: src\GhCLI\bin\$Configuration\net8.0\GhCLI.exe status"
