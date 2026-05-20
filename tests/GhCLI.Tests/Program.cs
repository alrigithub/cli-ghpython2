using System.Text.Json;
using GhCLI;
using GhCLI.Core.Files;
using GhCLI.Core.Graph;
using GhCLI.Core.Sessions;
using GhCLI.Protocol;

var tests = new (string Name, Action Run)[]
{
    ("CLI argument parsing", TestCliArgumentParsing),
    ("Protocol camel/snake compatibility", TestProtocolJsonCompatibility),
    ("Payload file_path resolution", TestPayloadFilePathResolution),
    ("Session selector resolution", TestSessionSelectorResolution),
    ("Graph hash stability", TestGraphHashStability),
    ("Exit code mapping", TestExitCodeMapping)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void TestCliArgumentParsing()
{
    var parsed = CliArguments.Parse(new[]
    {
        "graph.apply",
        "--file",
        "graph.json",
        "--timeout-ms",
        "15000",
        "--flag"
    });

    AssertEqual("graph.apply", parsed.Command, "command");
    AssertEqual("graph.json", parsed.Options["file"], "file option");
    AssertEqual("15000", parsed.Options["timeout-ms"], "timeout option");
    AssertEqual("true", parsed.Options["flag"], "boolean flag");

    var parsedWithGlobalOption = CliArguments.Parse(new[]
    {
        "--session",
        "rhino-123",
        "canvas.summary",
        "--scope",
        "selected"
    });

    AssertEqual("canvas.summary", parsedWithGlobalOption.Command, "global option command");
    AssertEqual("rhino-123", parsedWithGlobalOption.Options["session"], "global session option");
    AssertEqual("selected", parsedWithGlobalOption.Options["scope"], "post-command option");
}

static void TestProtocolJsonCompatibility()
{
    const string snake = """
        {
          "transaction_id": "snake-1",
          "solve_after": false,
          "debug_after": ["node_a"],
          "python_nodes": [{ "node_id": "node_a" }],
          "components": [{ "node_id": "custom_preview", "component": "CustomPreview" }],
          "preview": { "mode": "isolate", "node_ids": ["custom_preview"] },
          "layer_visibility": { "mode": "show", "layer_root": "GHCLI_SMOKE" }
        }
        """;

    var snakeRequest = JsonSerializer.Deserialize<GraphApplyRequest>(snake, ProtocolJson.Options)!;
    AssertEqual("snake-1", snakeRequest.TransactionId, "snake transaction id");
    AssertEqual(false, snakeRequest.SolveAfter, "snake solve after");
    AssertEqual("node_a", snakeRequest.DebugAfter[0], "snake debug after");
    AssertEqual(1, snakeRequest.PythonNodes.Count, "snake python nodes");
    AssertEqual(1, snakeRequest.Components.Count, "snake components");
    AssertEqual(JsonValueKind.Object, snakeRequest.Preview?.ValueKind ?? JsonValueKind.Undefined, "snake preview");
    AssertEqual(JsonValueKind.Object, snakeRequest.LayerVisibility?.ValueKind ?? JsonValueKind.Undefined, "snake layer visibility");

    const string camel = """
        {
          "transactionId": "camel-1",
          "solveAfter": false,
          "debugAfter": ["node_b"],
          "pythonNodes": [{ "node_id": "node_b" }],
          "components": [{ "node_id": "text_tag", "component": "TextTag" }],
          "preview": { "mode": "restore", "state_id": "demo" },
          "layerVisibility": { "mode": "restore", "state_id": "demo" }
        }
        """;

    var camelRequest = JsonSerializer.Deserialize<GraphApplyRequest>(camel, ProtocolJson.Options)!;
    AssertEqual("camel-1", camelRequest.TransactionId, "camel transaction id");
    AssertEqual(false, camelRequest.SolveAfter, "camel solve after");
    AssertEqual("node_b", camelRequest.DebugAfter[0], "camel debug after");
    AssertEqual(1, camelRequest.PythonNodes.Count, "camel python nodes");
    AssertEqual(1, camelRequest.Components.Count, "camel components");
    AssertEqual(JsonValueKind.Object, camelRequest.Preview?.ValueKind ?? JsonValueKind.Undefined, "camel preview");
    AssertEqual(JsonValueKind.Object, camelRequest.LayerVisibility?.ValueKind ?? JsonValueKind.Undefined, "camel layer visibility");
}

static void TestPayloadFilePathResolution()
{
    var payloadPath = Path.Combine(Path.GetTempPath(), "ghcli-tests", "sample", "graph.json");
    using var doc = JsonDocument.Parse("""
        {
          "pythonNodes": [
            { "node_id": "n", "file_path": "script.py" }
          ],
          "nested": {
            "filePath": "nested.py"
          }
        }
        """);

    var resolved = JsonPayloadPathResolver.ResolveFilePaths(doc.RootElement, payloadPath);
    var pythonPath = resolved
        .GetProperty("pythonNodes")[0]
        .GetProperty("file_path")
        .GetString();
    var nestedPath = resolved
        .GetProperty("nested")
        .GetProperty("filePath")
        .GetString();

    AssertEqual(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(payloadPath)!, "script.py")),
        pythonPath,
        "python file_path");
    AssertEqual(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(payloadPath)!, "nested.py")),
        nestedPath,
        "nested filePath");
}

static void TestGraphHashStability()
{
    var nodesA = new[]
    {
        Node("b", "B", 20, 10, new[] { "i" }, new[] { "o" }),
        Node("a", "A", 10, 20, Array.Empty<string>(), new[] { "out" })
    };
    var nodesB = nodesA.Reverse().ToArray();

    var edgesA = new[]
    {
        new CanvasEdgeSummaryModel
        {
            SourceNodeId = "a",
            SourcePort = "out",
            TargetNodeId = "b",
            TargetPort = "i"
        }
    };
    var edgesB = edgesA.Reverse().ToArray();

    AssertEqual(
        GraphHashBuilder.Build(nodesA, edgesA),
        GraphHashBuilder.Build(nodesB, edgesB),
        "graph hash order stability");
}

static void TestSessionSelectorResolution()
{
    var sessions = new[]
    {
        new SessionRecord
        {
            SessionId = "abcdef123456",
            Alias = "rhino-100",
            PipeName = "ghcli.v1.100.abcdef12",
            ProcessId = 100,
            HostKind = "rhino",
            ProcessName = "Rhino"
        },
        new SessionRecord
        {
            SessionId = "fedcba654321",
            Alias = "revit-200",
            PipeName = "ghcli.v1.200.fedcba65",
            ProcessId = 200,
            HostKind = "revit",
            ProcessName = "Revit"
        }
    };

    AssertEqual("ghcli.v1.100.abcdef12", GhCliSessionRegistry.ResolveSession("rhino-100", sessions).PipeName, "alias selector");
    AssertEqual("ghcli.v1.200.fedcba65", GhCliSessionRegistry.ResolveSession("200", sessions).PipeName, "process selector");
    AssertEqual("ghcli.v1.100.abcdef12", GhCliSessionRegistry.ResolveSession("abcdef", sessions).PipeName, "session prefix selector");
}

static void TestExitCodeMapping()
{
    AssertEqual(CliExitCodes.Success, CliExitCodes.FromResponse(new RpcResponseEnvelope { Ok = true }), "ok");
    AssertEqual(
        CliExitCodes.PluginUnavailable,
        CliExitCodes.FromResponse(RpcResponse.Failure("status", "plugin_unavailable", "missing")),
        "plugin unavailable");
    AssertEqual(
        CliExitCodes.Validation,
        CliExitCodes.FromResponse(RpcResponse.Failure("graph.apply", "validation", "bad")),
        "validation");
    AssertEqual(
        CliExitCodes.SolveTimeout,
        CliExitCodes.FromResponse(RpcResponse.Failure("solve.run", "solve_timeout", "slow")),
        "solve timeout");
    AssertEqual(
        CliExitCodes.RuntimeFailure,
        CliExitCodes.FromResponse(RpcResponse.Failure("status", "runtime_failure", "bad")),
        "runtime failure");
    AssertEqual(CliExitCodes.RuntimeFailure, CliExitCodes.FromResponse(null), "null response");
}

static CanvasNodeSummaryModel Node(
    string nodeId,
    string nickname,
    double x,
    double y,
    IEnumerable<string> inputs,
    IEnumerable<string> outputs)
{
    return new CanvasNodeSummaryModel
    {
        NodeId = nodeId,
        Id = nodeId,
        Kind = "test",
        Nickname = nickname,
        Position = new PositionModel { X = x, Y = y },
        Inputs = inputs.Select(name => new PortSummaryModel { Name = name, Direction = "in" }).ToList(),
        Outputs = outputs.Select(name => new PortSummaryModel { Name = name, Direction = "out" }).ToList()
    };
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
}
