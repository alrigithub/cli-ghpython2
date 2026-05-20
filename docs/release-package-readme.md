<img src="assets/ghcli-logo.png" alt="ghcli" width="180">

# ghcli

python nodes. fast loop.

## requirements

- Windows
- Rhino 8 with Grasshopper
- .NET 8 runtime

## install

1. Close Rhino.
2. Copy every file from `plugin/` into:

```text
%APPDATA%\Grasshopper\Libraries\GhCLI\
```

3. Start Rhino 8 and open Grasshopper.
4. Verify from this folder:

```powershell
.\GhCLI.exe status
```

If copy fails, Rhino is probably still locking the old plugin. Close Rhino completely and retry.

## commands

```text
sessions
status
canvas.summary
graph.apply
debug.read
node.read
solve.run
txn.apply
```

For multiple Rhino/Revit sessions:

```powershell
.\GhCLI.exe sessions
.\GhCLI.exe --session rhino-87520 status
```
