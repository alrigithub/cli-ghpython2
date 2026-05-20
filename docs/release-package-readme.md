<img src="assets/ghcli-logo.png" alt="GhCLI" width="200">

# GhCLI Release Package

GhCLI is a local JSON bridge between an agent and a live Rhino 8 Grasshopper canvas.

Requirements: Windows, Rhino 8 with Grasshopper, and the .NET 8 runtime.

## Contents

```text
GhCLI/
  GhCLI.exe
  README.md
  assets/
    ghcli-logo.png
  plugin/
    GhCLI.Plugin.gha
    GhCLI.Plugin.deps.json
    GhCLI.Core.dll
    GhCLI.Protocol.dll
```

## Install

1. Close Rhino.
2. Copy every file from `plugin/` into:

```text
%APPDATA%\Grasshopper\Libraries\GhCLI\
```

3. Start Rhino 8 and open Grasshopper.
4. Verify the bridge from this package folder:

```powershell
.\GhCLI.exe status
```

If copying plugin files fails, Rhino or Grasshopper is probably still locking the previous plugin. Close Rhino completely and retry.

## Agent Loop

Use the stable public commands:

```text
status -> canvas.summary -> graph.apply -> debug.read/node.read -> patch
```

Supported commands:

- `sessions`
- `status`
- `canvas.summary`
- `graph.apply`
- `debug.read`
- `node.read`
- `solve.run`
- `txn.apply` for lower-level advanced/internal patches

`graph.apply` Python nodes must use Rhino 8 CPython 3 with `"runtime": "cpython3"`, and Python source should be referenced by `file_path` rather than embedded in JSON.

## Multiple Sessions

Run:

```powershell
.\GhCLI.exe sessions
```

Then target a host explicitly:

```powershell
.\GhCLI.exe --session rhino-87520 status
.\GhCLI.exe --session revit-54620 canvas.summary
```

Each plugin host uses a unique pipe by default. If several Rhino/Revit hosts are active, GhCLI requires `--session` or `--pipe` so commands do not land in the wrong canvas.
