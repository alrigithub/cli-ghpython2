[CmdletBinding()]
param(
  [string]$Version,

  [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistRoot = Join-Path $RepoRoot "dist"
$StageRoot = Join-Path $DistRoot "GhCLI"
$CliPublish = Join-Path $DistRoot "publish-cli"
$Solution = Join-Path $RepoRoot "GhCLI.sln"
$CliProject = Join-Path $RepoRoot "src\GhCLI\GhCLI.csproj"
$PluginSource = Join-Path $RepoRoot "src\GhCLI.Plugin\bin\Release\net7.0-windows"
$LogoSource = Join-Path $RepoRoot "assets\ghcli-logo.png"
$ReleaseReadme = Join-Path $RepoRoot "docs\release-package-readme.md"

$RequiredPluginFiles = @(
  "GhCLI.Plugin.gha",
  "GhCLI.Plugin.deps.json",
  "GhCLI.Core.dll",
  "GhCLI.Protocol.dll"
)

function Get-ReleaseVersion {
  if (-not [string]::IsNullOrWhiteSpace($Version)) {
    return $Version
  }

  $GitVersion = ""
  try {
    $GitVersion = (& git -C $RepoRoot describe --tags --dirty --always 2>$null).Trim()
  }
  catch {
    $GitVersion = ""
  }

  if ([string]::IsNullOrWhiteSpace($GitVersion)) {
    return (Get-Date -Format "yyyyMMdd-HHmmss")
  }

  return $GitVersion
}

function Assert-Exists {
  param([string]$Path)

  if (-not (Test-Path -LiteralPath $Path)) {
    throw "Expected file does not exist: $Path"
  }
}

function New-ZipFromDirectory {
  param(
    [string]$SourceDirectory,
    [string]$DestinationPath
  )

  Add-Type -AssemblyName System.IO.Compression

  $SourceFullPath = [System.IO.Path]::GetFullPath($SourceDirectory)
  $SourceName = Split-Path -Leaf $SourceFullPath

  $ZipStream = [System.IO.File]::Open(
    $DestinationPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)

  try {
    $Archive = [System.IO.Compression.ZipArchive]::new(
      $ZipStream,
      [System.IO.Compression.ZipArchiveMode]::Create,
      $false)
    $SourceUri = [System.Uri]($SourceFullPath.TrimEnd("\") + "\")

    try {
      Get-ChildItem -LiteralPath $SourceFullPath -Recurse -File | ForEach-Object {
        $Relative = [System.Uri]::UnescapeDataString($SourceUri.MakeRelativeUri([System.Uri]$_.FullName).ToString())
        $EntryName = "$SourceName/$Relative"
        $Entry = $Archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $EntryStream = $Entry.Open()
        $InputStream = [System.IO.File]::Open(
          $_.FullName,
          [System.IO.FileMode]::Open,
          [System.IO.FileAccess]::Read,
          [System.IO.FileShare]::ReadWrite)

        try {
          $InputStream.CopyTo($EntryStream)
        }
        finally {
          $InputStream.Dispose()
          $EntryStream.Dispose()
        }
      }
    }
    finally {
      $Archive.Dispose()
    }
  }
  finally {
    $ZipStream.Dispose()
  }
}

$ResolvedVersion = Get-ReleaseVersion
$SafeVersion = $ResolvedVersion -replace "[^A-Za-z0-9._-]", "-"
$ZipPath = Join-Path $DistRoot "GhCLI-$SafeVersion.zip"

if (-not $SkipBuild) {
  dotnet build $Solution --configuration Release --nologo
  dotnet publish $CliProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $CliPublish `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    --nologo
}

if (Test-Path -LiteralPath $StageRoot) {
  Remove-Item -LiteralPath $StageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $StageRoot "assets") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $StageRoot "plugin") | Out-Null

$CliExe = Join-Path $CliPublish "GhCLI.exe"
Assert-Exists $CliExe
Assert-Exists $LogoSource
Assert-Exists $ReleaseReadme

Copy-Item -LiteralPath $CliExe -Destination (Join-Path $StageRoot "GhCLI.exe") -Force
Copy-Item -LiteralPath $ReleaseReadme -Destination (Join-Path $StageRoot "README.md") -Force
Copy-Item -LiteralPath $LogoSource -Destination (Join-Path $StageRoot "assets\ghcli-logo.png") -Force

foreach ($File in $RequiredPluginFiles) {
  $From = Join-Path $PluginSource $File
  Assert-Exists $From
  Copy-Item -LiteralPath $From -Destination (Join-Path $StageRoot "plugin\$File") -Force
}

$RequiredStageFiles = @(
  "GhCLI.exe",
  "README.md",
  "assets\ghcli-logo.png",
  "plugin\GhCLI.Plugin.gha",
  "plugin\GhCLI.Plugin.deps.json",
  "plugin\GhCLI.Core.dll",
  "plugin\GhCLI.Protocol.dll"
)

foreach ($File in $RequiredStageFiles) {
  Assert-Exists (Join-Path $StageRoot $File)
}

if (Test-Path -LiteralPath $ZipPath) {
  try {
    Remove-Item -LiteralPath $ZipPath -Force
  }
  catch {
    $FallbackVersion = "{0}-{1}" -f $SafeVersion, (Get-Date -Format "yyyyMMdd-HHmmss")
    $ZipPath = Join-Path $DistRoot "GhCLI-$FallbackVersion.zip"
    Write-Warning "Existing zip is locked; writing '$ZipPath' instead."
  }
}

New-ZipFromDirectory -SourceDirectory $StageRoot -DestinationPath $ZipPath
Assert-Exists $ZipPath

$Hash = Get-FileHash -Algorithm SHA256 -LiteralPath $ZipPath

Write-Host "Release package: $ZipPath"
Write-Host "SHA256: $($Hash.Hash)"
