# Samples

Runnable end-to-end GhCLI sample definitions.

Each sample folder follows:

```text
samples/<sample-name>/
  script.py
  graph.json
```

Rules:

- folder names use kebab-case
- Python file is always `script.py`
- graph payload is always `graph.json`
- `graph.json` references `script.py` with a portable relative `file_path`
- each Python node exposes `dbg`

Run a sample:

```powershell
src\GhCLI\bin\Debug\net8.0\GhCLI.exe graph.apply --file samples\<sample-name>\graph.json --timeout-ms 15000
```

When a payload is loaded with `--file`, GhCLI resolves relative `file_path` values from the folder containing that JSON file before sending the request to Grasshopper. Generated graph files can also use absolute `file_path` values resolved at runtime by the agent or calling script.
