# Massing Stack Smoke Test

Architectural workflow smoke test for GhCLI:

```text
01 Massing + Levels + Grids -> 02 Floor Slabs -> 03 Area Preview -> 04 Bake
```

Default behavior is preview-only. The bake node is present, but it is driven by a manual Grasshopper `Bake Button` that starts as `false` during automated smoke runs.

The graph also creates a focused display layer on the Grasshopper canvas:

- `Scribble` notes for the workflow title and manual bake reminder
- `Custom Preview` for translucent slab curves
- `Text Tag` for floor area labels
- `Point List` for centroid checking
- `Vector Display` for vertical readback markers
- `Colour Swatch` as a visible palette anchor

The payload uses `preview.mode = isolate` so unrelated Grasshopper previews are hidden while the smoke test is active. Restore previous Grasshopper preview states with `set_preview` mode `restore` and `state_id` `massing-stack-smoke`.

To bake, press `SMOKE_BAKE_button` / `Bake Button` on the Grasshopper canvas. Baked objects go only under:

```text
GHCLI_SMOKE::00_Massing
GHCLI_SMOKE::01_Levels
GHCLI_SMOKE::02_Grids
GHCLI_SMOKE::03_Slabs
GHCLI_SMOKE::04_Area_Labels
```

When `Bake Replace` is true, the bake node deletes only objects tagged with `ghcli_smoke=true` before rebaking.

Baked Rhino layers can be isolated/restored with `set_layer_visibility` using `layer_root` `GHCLI_SMOKE`.
