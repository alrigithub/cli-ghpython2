[CmdletBinding()]
param(
  [string]$TargetPath = (Join-Path $env:APPDATA "Grasshopper\Libraries\GhCLI")
)

$ErrorActionPreference = "Stop"

$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RequiredFiles = @(
  "GhCLI.exe",
  "GhCLI.Plugin.gha",
  "GhCLI.Plugin.deps.json",
  "GhCLI.Core.dll",
  "GhCLI.Protocol.dll"
)

foreach ($File in $RequiredFiles) {
  $Path = Join-Path $SourceRoot $File
  if (-not (Test-Path -LiteralPath $Path)) {
    throw "Missing required file: $Path"
  }
}

$SourceFull = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd("\")
$TargetFull = [System.IO.Path]::GetFullPath($TargetPath).TrimEnd("\")

if ($SourceFull -ieq $TargetFull) {
  Write-Host "ghcli is already in the Grasshopper Libraries folder:"
  Write-Host $TargetFull
  Write-Host "Restart Rhino, open Grasshopper, then run: .\GhCLI.exe status"
  return
}

New-Item -ItemType Directory -Force -Path $TargetFull | Out-Null

foreach ($File in $RequiredFiles) {
  Copy-Item -LiteralPath (Join-Path $SourceRoot $File) -Destination (Join-Path $TargetFull $File) -Force
}

if (Test-Path -LiteralPath (Join-Path $SourceRoot "README.md")) {
  Copy-Item -LiteralPath (Join-Path $SourceRoot "README.md") -Destination (Join-Path $TargetFull "README.md") -Force
}

if (Test-Path -LiteralPath (Join-Path $SourceRoot "VERSION.txt")) {
  Copy-Item -LiteralPath (Join-Path $SourceRoot "VERSION.txt") -Destination (Join-Path $TargetFull "VERSION.txt") -Force
}

if (Test-Path -LiteralPath (Join-Path $SourceRoot "assets")) {
  Copy-Item -LiteralPath (Join-Path $SourceRoot "assets") -Destination $TargetFull -Recurse -Force
}

Write-Host "Installed ghcli to $TargetFull"
Write-Host "Restart Rhino, open Grasshopper, then verify with:"
Write-Host "  $TargetFull\GhCLI.exe status"
