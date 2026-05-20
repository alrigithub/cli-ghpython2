---
description: Build GhCLI and install the Grasshopper plugin into the local Grasshopper Libraries folder
argument-hint: "[debug|release]"
---

# Install GhCLI

Use this command when the user asks to install, update, or reinstall the local GhCLI Grasshopper plugin.

Build configuration:

```text
$ARGUMENTS
```

## Behavior

Install means:

1. Build `GhCLI.sln`.
2. Copy the plugin output files into `%APPDATA%\Grasshopper\Libraries\GhCLI\`.
3. Tell the user to restart Rhino if Rhino was open.
4. After Rhino restarts, verify with `GhCLI.exe status`.

Default to `Release` unless the user explicitly passes `debug`.

## Required Files

Copy these files from:

```text
src\GhCLI.Plugin\bin\<Configuration>\net7.0-windows\
```

into:

```text
%APPDATA%\Grasshopper\Libraries\GhCLI\
```

Required files:

- `GhCLI.Plugin.gha`
- `GhCLI.Plugin.deps.json`
- `GhCLI.Core.dll`
- `GhCLI.Protocol.dll`

## PowerShell Install

Run this from the repo root:

```powershell
$Configuration = "Release"
if ("$ARGUMENTS".Trim().ToLowerInvariant() -eq "debug") {
  $Configuration = "Debug"
}

scripts\install-plugin.ps1 -Configuration $Configuration
```

## Failure Handling

If copy fails because files are locked, Rhino is probably still open. Tell the user to close Rhino, then rerun `/install`.

If `status` fails after restart, check:

- the plugin folder contains all required files
- Rhino loaded Grasshopper after restart
- the CLI and plugin agree on pipe name `ghcli.v1`
- custom pipe overrides use `GHCLI_PIPE_NAME`
