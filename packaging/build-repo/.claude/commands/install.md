---
description: Install or update ghcli from this build-only package
argument-hint: ""
---

# install ghcli

Run the bundled installer:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
```

If files are locked, tell the user to close Rhino completely and rerun `/install`.

After install, tell the user to restart Rhino, open Grasshopper, and verify:

```powershell
.\GhCLI.exe status
```
