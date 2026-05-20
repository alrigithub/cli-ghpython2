<img src="assets/ghcli-logo.png" alt="ghcli" width="180">

# ghcli

python nodes. fast loop.

## install

Close Rhino, then run:

```powershell
.\install.ps1
```

Restart Rhino and open Grasshopper.

In Claude Code, run:

```text
/install
```

In Codex, ask it to install ghcli from this folder. `AGENTS.md` tells it to run the same installer.

## clone directly into Grasshopper

If you have Git installed, you can install without copying:

```powershell
git clone https://github.com/alrigithub/ghcli-build "%APPDATA%\Grasshopper\Libraries\GhCLI"
```

To update later:

```powershell
cd "%APPDATA%\Grasshopper\Libraries\GhCLI"
git pull
```

## verify

```powershell
.\GhCLI.exe status
```

If multiple Rhino/Revit sessions are open:

```powershell
.\GhCLI.exe sessions
.\GhCLI.exe --session rhino-12345 status
```
