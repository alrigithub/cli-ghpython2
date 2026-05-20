[CmdletBinding()]
param(
  [string]$Version,
  [string]$OutputPath,
  [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistRoot = Join-Path $RepoRoot "dist"
$StageRoot = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  Join-Path $DistRoot "GhCLI-build"
}
else {
  $OutputPath
}

$Solution = Join-Path $RepoRoot "GhCLI.sln"
$CliProject = Join-Path $RepoRoot "src\GhCLI\GhCLI.csproj"
$CliPublish = Join-Path $DistRoot "publish-cli"
$PluginSource = Join-Path $RepoRoot "src\GhCLI.Plugin\bin\Release\net7.0-windows"
$TemplateRoot = Join-Path $RepoRoot "packaging\build-repo"
$LogoSource = Join-Path $RepoRoot "assets\ghcli-logo.png"

$RequiredPluginFiles = @(
  "GhCLI.Plugin.gha",
  "GhCLI.Plugin.deps.json",
  "GhCLI.Core.dll",
  "GhCLI.Protocol.dll"
)

function Get-BuildVersion {
  if (-not [string]::IsNullOrWhiteSpace($Version)) {
    return $Version
  }

  try {
    $GitVersion = (& git -C $RepoRoot describe --tags --dirty --always 2>$null).Trim()
    if (-not [string]::IsNullOrWhiteSpace($GitVersion)) {
      return $GitVersion
    }
  }
  catch {
    # fall through to timestamp
  }

  return (Get-Date -Format "yyyyMMdd-HHmmss")
}

function Assert-Exists {
  param([string]$Path)

  if (-not (Test-Path -LiteralPath $Path)) {
    throw "Expected file does not exist: $Path"
  }
}

function Copy-File {
  param(
    [string]$From,
    [string]$To
  )

  Assert-Exists $From
  $Parent = Split-Path -Parent $To
  if (-not [string]::IsNullOrWhiteSpace($Parent)) {
    New-Item -ItemType Directory -Force -Path $Parent | Out-Null
  }
  Copy-Item -LiteralPath $From -Destination $To -Force
}

$ResolvedVersion = Get-BuildVersion

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

New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null

Copy-File -From (Join-Path $CliPublish "GhCLI.exe") -To (Join-Path $StageRoot "GhCLI.exe")
Copy-File -From (Join-Path $TemplateRoot "README.md") -To (Join-Path $StageRoot "README.md")
Copy-File -From (Join-Path $TemplateRoot "AGENTS.md") -To (Join-Path $StageRoot "AGENTS.md")
Copy-File -From (Join-Path $TemplateRoot "install.ps1") -To (Join-Path $StageRoot "install.ps1")
Copy-File -From (Join-Path $TemplateRoot ".claude\commands\install.md") -To (Join-Path $StageRoot ".claude\commands\install.md")
Copy-File -From $LogoSource -To (Join-Path $StageRoot "assets\ghcli-logo.png")

foreach ($File in $RequiredPluginFiles) {
  Copy-File -From (Join-Path $PluginSource $File) -To (Join-Path $StageRoot $File)
}

$VersionText = @(
  "version=$ResolvedVersion",
  "built=$(Get-Date -Format o)"
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $StageRoot "VERSION.txt") -Value $VersionText -Encoding UTF8

$RequiredStageFiles = @(
  "GhCLI.exe",
  "GhCLI.Plugin.gha",
  "GhCLI.Plugin.deps.json",
  "GhCLI.Core.dll",
  "GhCLI.Protocol.dll",
  "README.md",
  "AGENTS.md",
  "install.ps1",
  "VERSION.txt",
  "assets\ghcli-logo.png",
  ".claude\commands\install.md"
)

foreach ($File in $RequiredStageFiles) {
  Assert-Exists (Join-Path $StageRoot $File)
}

Write-Host "Build repo staged: $StageRoot"
Write-Host "Version: $ResolvedVersion"
Write-Host "Direct clone target: %APPDATA%\Grasshopper\Libraries\GhCLI"
