<img src="assets/ghcli-logo.png" alt="ghcli" width="180">

# ghcli

python nodes. fast loop.

ghcli is a small local bridge for pushing file-backed Rhino 8 CPython nodes into Grasshopper, solving the canvas, and reading the result back.

The point is narrow: let the model use normal coding and filesystem tools, then send compact graph edits to Grasshopper.

## requirements

- Windows
- Rhino 8 with Grasshopper
- .NET SDK 8+

## install for local development

```powershell
scripts\install-plugin.ps1
```

Restart Rhino, open Grasshopper, then verify:

```powershell
src\GhCLI\bin\Release\net8.0\GhCLI.exe status
```

## smoke test

```powershell
src\GhCLI\bin\Release\net8.0\GhCLI.exe graph.apply --file samples\massing-stack-smoke\graph.json
```

The smoke test builds a small architectural graph:

```text
massing -> levels/grids -> slabs -> area preview
```

It is preview-only by default. The canvas includes a manual bake button, but automated smoke runs do not bake Rhino geometry.

## release package

```powershell
scripts\package-release.ps1
```

The zip is intentionally small:

```text
GhCLI/
  GhCLI.exe
  README.md
  assets/ghcli-logo.png
  plugin/
    GhCLI.Plugin.gha
    GhCLI.Plugin.deps.json
    GhCLI.Core.dll
    GhCLI.Protocol.dll
```

For a cloneable build-only repo:

```powershell
scripts\package-build-repo.ps1 -OutputPath C:\path\to\ghcli-build
```

## docs

- `docs/command-contract.md` - command and JSON contract
- `samples/` - runnable graph examples
- `CLAUDE.md` / `AGENTS.md` - agent workflow notes
