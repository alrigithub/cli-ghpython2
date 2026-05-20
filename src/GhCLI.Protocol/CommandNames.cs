namespace GhCLI.Protocol;

public static class CommandNames
{
    public const string Status = "status";
    public const string CanvasSummary = "canvas.summary";
    public const string NodeRead = "node.read";
    public const string TxnApply = "txn.apply";
    public const string GraphApply = "graph.apply";
    public const string SolveRun = "solve.run";
    public const string DebugRead = "debug.read";
    public const string Sessions = "sessions";
}

public static class TransactionOps
{
    public const string UpsertPythonNode = "upsert_python_node";
    public const string UpsertComponent = "upsert_component";
    public const string UpsertSlider = "upsert_slider";
    public const string UpsertToggle = "upsert_toggle";
    public const string UpsertPanel = "upsert_panel";
    public const string UpsertNote = "upsert_note";
    public const string SetWires = "set_wires";
    public const string MoveNode = "move_node";
    public const string SetValue = "set_value";
    public const string SetPreview = "set_preview";
    public const string SetLayerVisibility = "set_layer_visibility";
}
