# Command Contract

The CLI is a local JSON bridge to the Grasshopper plugin.

## Rules

- stdout is reserved for the final JSON response
- stderr is reserved for diagnostics
- failures exit non-zero
- `--verbose` must not change stdout shape
- Python source is referenced by `file_path`, not embedded
- writes should be batched through `graph.apply`

## Response Envelope

Every command returns this shape:

```json
{
  "ok": true,
  "id": "request-id",
  "command": "graph.apply",
  "data": {},
  "warnings": []
}
```

## Commands

### `sessions`

Lists registered GhCLI plugin sessions from `%LOCALAPPDATA%\GhCLI\sessions.json`.

```powershell
src\GhCLI\bin\Release\net8.0\GhCLI.exe sessions
```

Key response fields:

- `registryPath`
- `sessions`
- `sessions[].alias`
- `sessions[].pipeName`
- `sessions[].processId`
- `sessions[].hostKind`
- `sessions[].windowTitle`

When more than one session is active, pass `--session <alias|pid|session-id|pipe-name>` to any pipe-backed command.

### `status`

Checks whether Rhino, Grasshopper, and the plugin are reachable.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe status --timeout-ms 5000
```

Key response fields:

- `pluginLoaded`
- `pipeConnected`
- `session`
- `rhinoVersion`
- `grasshopperLoaded`
- `activeDocument`
- `capabilities`

### `canvas.summary`

Returns a compact live DAG summary.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe canvas.summary --scope full
```

Key response fields:

- `graphHash`
- `nodeCount`
- `edgeCount`
- `nodes`
- `edges`
- `selectedNodeIds`
- `warnings`

### `graph.apply`

Preferred write command for agents. It creates or updates controls, Python nodes, native display components, panels, notes, and wires in one staged operation.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe graph.apply --file templates\graph-apply-basic.json --timeout-ms 15000
```

Input fields:

- `transactionId`
- `solveAfter`
- `debugAfter`
- `sliders`
- `toggles`
- `panels`
- `notes`
- `pythonNodes`
- `components`
- `wires`
- `preview`
- `layerVisibility` / `layer_visibility`

Python node fields:

- `node_id`
- `nickname`
- `runtime`: use `cpython3`
- `file_path`: absolute path recommended; when using `GhCLI.exe graph.apply --file <graph.json>`, relative paths are resolved from the JSON file's folder before the request is sent
- `position`
- `inputs`
- `outputs`

Native component fields:

- `node_id`
- `component`: component name such as `CustomPreview`, `TextTag`, `PointList`, `VectorDisplay`, or `Colour Swatch`
- `component_guid`: optional explicit Grasshopper component GUID
- `nickname`
- `position`
- `color` / `colour`: optional swatch value for colour swatches

Preview focus fields:

- `mode`: `isolate`, `restore`, `show`, or `hide`
- `state_id`: optional key used to restore a previous isolate state
- `node_ids`: required for `isolate`, `show`, and `hide`

Layer visibility fields:

- `mode`: `isolate`, `restore`, `show`, or `hide`
- `state_id`: optional key used to restore a previous bake-layer isolate state
- `layer_root`: required except for `restore`

Wire fields:

- `source_node_id`
- `source_port`
- `target_node_id`
- `target_port`

Response fields:

- `applied`
- `created`
- `patched`
- `deleted`
- `wireChanges`
- `graphHash`
- `runtimeMessages`
- `debugReads`

### `txn.apply`

Lower-level mutation command. It remains supported for compatibility and advanced/internal patches, but agents should prefer `graph.apply`.

Input fields:

- `transactionId`
- `operations`
- `solveAfter`
- `debugAfter`

Supported operation types:

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

### `debug.read`

Reads compact solved evidence from one node.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe debug.read --node-id example_node
```

Key response fields:

- `runtimeMessages`
- `outputSummaries`
- `debugValues`
- `sourceHash`

### `node.read`

Reads one node in more detail.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe node.read --node-id example_node
```

Key response fields:

- `node`
- `runtimeMessages`
- `variableParams`
- `valueState`
- `outputSummaries`

### `solve.run`

Forces a solve.

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe solve.run
```

## Minimal `graph.apply`

```json
{
  "transactionId": "example-001",
  "solveAfter": true,
  "debugAfter": ["example_node"],
  "sliders": [
    {
      "node_id": "SLIDER_count",
      "nickname": "Count",
      "min": 1,
      "max": 20,
      "value": 5,
      "position": { "x": 60, "y": 100 }
    }
  ],
  "pythonNodes": [
    {
      "node_id": "example_node",
      "nickname": "Example Node",
      "runtime": "cpython3",
      "file_path": "C:/absolute/path/to/example.py",
      "position": { "x": 340, "y": 100 },
      "inputs": [
        { "name": "count", "typeName": "int", "access": "item" }
      ],
      "outputs": [
        { "name": "result", "typeName": "Generic Data", "access": "list" },
        { "name": "dbg", "typeName": "string", "access": "item" }
      ]
    }
  ],
  "panels": [
    {
      "node_id": "PANEL_example_debug",
      "nickname": "Debug",
      "position": { "x": 700, "y": 160 }
    }
  ],
  "wires": [
    {
      "source_node_id": "SLIDER_count",
      "source_port": "0",
      "target_node_id": "example_node",
      "target_port": "count"
    },
    {
      "source_node_id": "example_node",
      "source_port": "dbg",
      "target_node_id": "PANEL_example_debug",
      "target_port": "0"
    }
  ]
}
```

## Exit Codes

- `0` success
- `1` runtime failure
- `2` plugin unavailable
- `3` validation/input error
- `4` timeout
