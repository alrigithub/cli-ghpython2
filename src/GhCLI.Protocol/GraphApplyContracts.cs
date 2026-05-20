using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhCLI.Protocol;

public sealed class GraphApplyRequest
{
    public string TransactionId { get; set; } = string.Empty;
    public bool SolveAfter { get; set; } = true;
    public List<string> DebugAfter { get; set; } = new();
    public List<JsonElement> Sliders { get; set; } = new();
    public List<JsonElement> Toggles { get; set; } = new();
    public List<JsonElement> Panels { get; set; } = new();
    public List<JsonElement> Notes { get; set; } = new();
    public List<JsonElement> PythonNodes { get; set; } = new();
    public List<JsonElement> Components { get; set; } = new();
    public List<JsonElement> Wires { get; set; } = new();
    public JsonElement? Preview { get; set; }
    public JsonElement? LayerVisibility { get; set; }

    [JsonPropertyName("transaction_id")]
    public string TransactionIdSnake
    {
        get => TransactionId;
        set => TransactionId = value;
    }

    [JsonPropertyName("solve_after")]
    public bool SolveAfterSnake
    {
        get => SolveAfter;
        set => SolveAfter = value;
    }

    [JsonPropertyName("debug_after")]
    public List<string> DebugAfterSnake
    {
        get => DebugAfter;
        set => DebugAfter = value;
    }

    [JsonPropertyName("python_nodes")]
    public List<JsonElement> PythonNodesSnake
    {
        get => PythonNodes;
        set => PythonNodes = value;
    }

    [JsonPropertyName("layer_visibility")]
    public JsonElement? LayerVisibilitySnake
    {
        get => LayerVisibility;
        set => LayerVisibility = value;
    }
}
