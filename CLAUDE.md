# CLAUDE.md

GhCLI is an agent-to-Grasshopper fast loop. You are here to build or edit live Grasshopper definitions by writing files in this folder, applying one graph payload to the canvas, reading the result, and iterating.

Do not treat this as a human CLI UX. Treat it as a machine I/O rail for Claude Code, Codex, or another coding agent.

## Intelligence Model

Use the model's baked-in coding, geometry, and debugging intelligence first. Do not bury the task under skill reading, reference hunting, or over-formal planning.

Skills and references are edge-case amplifiers:

- use them when Grasshopper/Rhino has a known sharp edge
- use them when a live failure needs a specific workaround
- use them when the transaction schema or port typing is unclear
- do not use them as a substitute for normal model reasoning

Use `.claude/commands/geo-dag.md` only when the user intentionally calls for a computational design DAG, for example `/geo-dag make me a unitized facade panel`.

The ideal behavior is quick: infer the likely graph, write the Python, zap it to Grasshopper, read what happened, and patch.

## Mission

When the user says something like:

> make a Grasshopper script that does X and Y

you should:

1. Read the current canvas.
2. Write CPython 3 Grasshopper Python files in the repo.
3. Create or patch Python nodes, sliders, panels, and wires with one `graph.apply`.
4. Read the `debugAfter` data returned by `graph.apply`; use `debug.read` / `node.read` only for follow-up inspection.
5. Patch the Python file and re-apply until the live canvas is correct.

The thinking happens in this folder. Grasshopper receives compact transactions.

## CLI

The CLI is `GhCLI`.

Prefer an already built binary when available:

```powershell
src/GhCLI/bin/Debug/net8.0/GhCLI.exe status
```

If developing locally with the .NET SDK:

```powershell
dotnet run --project src/GhCLI -- status
```

Rules:

- stdout is final JSON only.
- non-zero exit means failure.
- exit code `2` means the Grasshopper plugin is unavailable.
- never claim success unless the JSON response proves it.

## Fast Loop

Use this loop every time:

```text
status -> canvas.summary -> write .py + graph.json -> graph.apply -> patch if needed
```

Commands currently implemented:

- `sessions`
- `status`
- `canvas.summary`
- `node.read`
- `txn.apply`
- `graph.apply`
- `solve.run`
- `debug.read`

Transaction ops currently implemented:

- `upsert_python_node`
- `upsert_component`
- `upsert_slider`
- `upsert_toggle`
- `upsert_panel`
- `upsert_note`
- `set_wires`
- `move_node`
- `set_value`
- `set_preview`
- `set_layer_visibility`

Do not use planned commands or ops unless the source shows they now exist.

When multiple Rhino/Revit hosts are open, run `sessions` and pass `--session <alias|pid|session-id|pipe-name>` to target the intended canvas. If exactly one registered session exists, GhCLI auto-targets it. If no registered session exists, the CLI falls back to the legacy `ghcli.v1` pipe for compatibility with older loaded plugins.

## Component Catalog

Native Grasshopper component inventory lives in:

```text
resources/grasshopper-components/
```

Use it as a narrow lookup cache when you need an installed native component or plugin component. Search `components.jsonl` and return only a few matching records. Do not load the whole catalog into context.

The current cache is generated from legacy ghpythonlib text dumps. Refresh it from those dumps with:

```powershell
python resources/grasshopper-components/tools/build_catalog.py
```

Future CLI commands may expose `catalog.refresh`, `catalog.search`, and `catalog.read`, but do not use them until `status.capabilities` reports them.

## Online References

RhinoCommon sample links live in:

```text
resources/rhinocommon-samples/rhinocommon-samples-llms.txt
```

Prefer opening or searching official online docs over loading copied local sample packs into context.

## Grasshopper Python Rules

Always create Python nodes as Rhino 8 CPython 3:

```json
{
  "op": "upsert_python_node",
  "args": {
    "node_id": "make_panels",
    "nickname": "Make Panels",
    "runtime": "cpython3",
    "file_path": "C:/absolute/path/to/make_panels.py",
    "inputs": [
      {"name": "surfaces", "typeName": "Brep", "access": "list"},
      {"name": "count", "typeName": "int", "access": "item"}
    ],
    "outputs": [
      {"name": "panels", "typeName": "Brep", "access": "list"},
      {"name": "dbg", "typeName": "string", "access": "item"}
    ],
    "position": {"x": 320, "y": 120}
  }
}
```

Python files must be file-based. Do not embed source in JSON.

Use this script shape:

```python
#! python 3
import Rhino
import Rhino.Geometry as rg
import System

debug_lines = []

def log(message):
    debug_lines.append(str(message))

def extract_geo(param_index):
    comp = ghenv.Component
    param = comp.Params.Input[param_index]
    results = []
    for pi in range(param.VolatileData.PathCount):
        branch = param.VolatileData.get_Branch(pi)
        for goo in branch:
            if goo is None:
                continue
            geo = getattr(goo, "Value", None)
            if geo is not None and isinstance(geo, rg.GeometryBase):
                results.append(geo)
                continue
            if isinstance(geo, System.Guid):
                rhobj = Rhino.RhinoDoc.ActiveDoc.Objects.FindId(geo)
                if rhobj:
                    results.append(rhobj.Geometry)
                    continue
            if hasattr(goo, "ScriptVariable"):
                sv = goo.ScriptVariable()
                if isinstance(sv, rg.GeometryBase):
                    results.append(sv)
    return results

try:
    # Inputs are variables named by the transaction schema.
    if count is None or count < 1:
        count = 1

    raw_surfaces = extract_geo(0)
    log("received surfaces: {}".format(len(raw_surfaces)))

    panels = []
    for geo in raw_surfaces:
        if isinstance(geo, rg.Brep):
            panels.append(geo)
        elif isinstance(geo, rg.Surface):
            panels.append(geo.ToBrep())
        elif isinstance(geo, rg.Extrusion):
            panels.append(geo.ToBrep())

    log("panels: {}".format(len(panels)))
except Exception as exc:
    panels = []
    log("ERROR: {}".format(exc))

dbg = "\n".join(debug_lines)
```

Hard rules:

- Use `Rhino.Geometry`, not `rhinoscriptsyntax`, inside GH Python nodes.
- Every Python node has a `dbg` string output.
- Guard every input against `None`.
- Convert `Surface` and `Extrusion` to `Brep` before outputting Brep lists.
- Use `"access": "list"` for lists and `"access": "tree"` only when you intentionally build a `DataTree`.

## Graph Apply Pattern

Prefer `graph.apply` for agent-created definitions. It creates controls, Python nodes, panels, wires, solves once, and returns compact debug reads in one response:

```json
{
  "transactionId": "make-panels-001",
  "solveAfter": true,
  "debugAfter": ["make_panels"],
  "sliders": [
    {
      "node_id": "SLIDER_count",
      "nickname": "Count",
      "min": 1,
      "max": 40,
      "value": 12,
      "position": {"x": 40, "y": 120}
    }
  ],
  "pythonNodes": [
    {
      "node_id": "make_panels",
      "nickname": "Make Panels",
      "runtime": "cpython3",
      "file_path": "C:/absolute/path/to/make_panels.py",
      "inputs": [{"name": "count", "typeName": "int", "access": "item"}],
      "outputs": [
        {"name": "panels", "typeName": "Brep", "access": "list"},
        {"name": "dbg", "typeName": "string", "access": "item"}
      ],
      "position": {"x": 320, "y": 120}
    }
  ],
  "panels": [
    {
      "node_id": "PANEL_debug_make_panels",
      "nickname": "Debug",
      "position": {"x": 680, "y": 160}
    }
  ],
  "wires": [
    {"source_node_id": "SLIDER_count", "source_port": "0", "target_node_id": "make_panels", "target_port": "count"},
    {"source_node_id": "make_panels", "source_port": "dbg", "target_node_id": "PANEL_debug_make_panels", "target_port": "0"}
  ],
  "solveAfter": true
}
```

Use raw `txn.apply` only for lower-level patches or unusual operation ordering. `graph.apply` internally stages node creation, materializes Python ports, wires after ports exist, solves once, and returns `debugReads` for `debugAfter`.

Optional display helpers:

- Use `components` / `upsert_component` for native display helpers such as `CustomPreview`, `TextTag`, `PointList`, `VectorDisplay`, and `Colour Swatch`.
- Use `preview: { "mode": "isolate", "node_ids": [...] }` only when the user needs a focused viewport. Restore with `set_preview` mode `restore` using the same `state_id`.
- Use `set_layer_visibility` for baked Rhino layer focus/restore. Keep bake side effects explicit and under a dedicated layer root.

Wiring rules:

- Python node ports use their schema names.
- Slider and toggle output port is `"0"`.
- Panel input port is `"0"`.
- Existing canvas nodes must be addressed by IDs from `canvas.summary`.

## Debugging

If something fails:

1. Read `debug.read --node-id <id>`.
2. Inspect `runtimeMessages`.
3. Inspect `dbg`.
4. Inspect `outputSummaries`.
5. If the script says it produced geometry but GH shows zero items, run `node.read --node-id <id>` and check output port `typeName` and `access`.
6. Patch the Python file, re-run `graph.apply` or `txn.apply` with the same `node_id`, and read again.

Do not debug Python logic before checking live port schema. Grasshopper port typing can be stale.

## Design Bias

Prefer 1-3 chunky Python nodes per user task:

- one node for generating or extracting base geometry
- one node for the main geometric operation
- one optional node for assembly, annotation, or debug structure

Avoid one huge script when intermediate geometry matters. Avoid many tiny nodes when one typed Python node is clearer and faster.
