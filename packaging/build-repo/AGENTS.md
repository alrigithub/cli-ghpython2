# ghcli build install

This is a build-only ghcli package.

If the user asks to install, update, or repair ghcli:

1. Close Rhino if plugin files are locked.
2. Run `powershell.exe -ExecutionPolicy Bypass -File .\install.ps1`.
3. Tell the user to restart Rhino and open Grasshopper.
4. Verify with `.\GhCLI.exe status` after Grasshopper loads.

Do not build from source in this repo. It only contains release binaries.
