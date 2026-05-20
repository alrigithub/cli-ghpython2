using System.Text.Json;
using GhCLI.Core.Errors;
using GhCLI.Core.Json;
using GhCLI.Protocol;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCLI.Plugin.Runtime;

internal sealed partial class GrasshopperRuntime
{
    private sealed class PlannedNodePorts
    {
        public PlannedNodePorts(string nodeId, string kind)
        {
            NodeId = nodeId;
            Kind = kind;
        }

        public string NodeId { get; }
        public string Kind { get; set; }
        public bool UnknownInputs { get; set; }
        public bool UnknownOutputs { get; set; }
        public HashSet<string> Inputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasPort(bool isOutput, string selector)
        {
            if (isOutput && UnknownOutputs)
            {
                return true;
            }

            if (!isOutput && UnknownInputs)
            {
                return true;
            }

            var ports = isOutput ? Outputs : Inputs;
            return ports.Contains(selector);
        }

        public string DescribePorts(bool isOutput)
        {
            if (isOutput && UnknownOutputs)
            {
                return "unknown until Grasshopper materializes the node";
            }

            if (!isOutput && UnknownInputs)
            {
                return "unknown until Grasshopper materializes the node";
            }

            var ports = isOutput ? Outputs : Inputs;
            return ports.Count == 0
                ? "(none)"
                : string.Join(", ", ports.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
    }

    private void PreValidateTransaction(GH_Document doc, TransactionApplyRequest request)
    {
        var plannedNodes = BuildInitialPlannedNodes(doc);
        var initiallyKnownNodeIds = plannedNodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in request.Operations)
        {
            ValidateOperationShape(operation);
        }

        foreach (var operation in request.Operations)
        {
            if (IsUpsertOperation(operation.Op))
            {
                PreValidateUpsertOperation(doc, operation, plannedNodes);
            }
        }

        var availableForOrderedOps = initiallyKnownNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in request.Operations)
        {
            switch (operation.Op)
            {
                case TransactionOps.UpsertPythonNode:
                case TransactionOps.UpsertComponent:
                case TransactionOps.UpsertSlider:
                case TransactionOps.UpsertToggle:
                case TransactionOps.UpsertPanel:
                case TransactionOps.UpsertNote:
                    availableForOrderedOps.Add(operation.Args.RequireString("node_id"));
                    break;

                case TransactionOps.MoveNode:
                    {
                        var nodeId = operation.Args.RequireString("node_id");
                        if (!availableForOrderedOps.Contains(nodeId))
                        {
                            throw new CommandValidationException(
                                $"move_node references unknown node_id '{nodeId}'. It must refer to an existing node or an earlier upsert in the same transaction.");
                        }

                        break;
                    }

                case TransactionOps.SetValue:
                    {
                        var nodeId = operation.Args.RequireString("node_id");
                        if (!availableForOrderedOps.Contains(nodeId))
                        {
                            throw new CommandValidationException(
                                $"set_value references unknown node_id '{nodeId}'. It must refer to an existing node or an earlier upsert in the same transaction.");
                        }

                        if (!operation.Args.TryGetPropertyIgnoreCase("value", out _))
                        {
                            throw new CommandValidationException("set_value requires a value field.");
                        }

                        ValidateSetValueTarget(plannedNodes[nodeId]);
                        break;
                    }

                case TransactionOps.SetWires:
                    ValidateWireOperation(operation.Args, plannedNodes);
                    break;

                case TransactionOps.SetPreview:
                    ValidatePreviewOperation(operation.Args, plannedNodes);
                    break;

                case TransactionOps.SetLayerVisibility:
                    ValidateLayerVisibilityOperation(operation.Args);
                    break;

                default:
                    throw new CommandValidationException($"Unsupported transaction op '{operation.Op}'.");
            }
        }

        foreach (var nodeId in request.DebugAfter)
        {
            if (!plannedNodes.ContainsKey(nodeId))
            {
                throw new CommandValidationException($"Unknown node_id '{nodeId}' in debugAfter.");
            }
        }
    }

    private Dictionary<string, PlannedNodePorts> BuildInitialPlannedNodes(GH_Document doc)
    {
        var result = new Dictionary<string, PlannedNodePorts>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in doc.Objects.OfType<IGH_DocumentObject>())
        {
            var kind = DetermineKind(obj);
            var nodeId = _nodeMetadata.GetOrCreateNodeId(obj, KindPrefix(kind));
            result[nodeId] = BuildLivePorts(nodeId, kind, obj);
        }

        return result;
    }

    private static void ValidateOperationShape(TransactionOperationModel operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Op))
        {
            throw new CommandValidationException("Transaction operation requires an op field.");
        }

        if (operation.Args.ValueKind != JsonValueKind.Object)
        {
            throw new CommandValidationException($"Operation '{operation.Op}' requires args object.");
        }
    }

    private void PreValidateUpsertOperation(
        GH_Document doc,
        TransactionOperationModel operation,
        Dictionary<string, PlannedNodePorts> plannedNodes)
    {
        var nodeId = operation.Args.RequireString("node_id");
        var existing = ResolveNode(doc, nodeId, null);
        if (existing is not null && !ExistingNodeMatchesUpsert(existing, operation.Op))
        {
            throw new CommandValidationException(
                $"node_id '{nodeId}' exists but is not compatible with op '{operation.Op}'. Existing node type is {existing.GetType().Name}.");
        }

        if (operation.Op == TransactionOps.UpsertPythonNode)
        {
            ValidatePythonFile(nodeId, operation.Args);
        }

        var planned = BuildPlannedPortsForUpsert(nodeId, operation, existing);
        if (plannedNodes.TryGetValue(nodeId, out var previous) &&
            existing is null &&
            !string.Equals(previous.Kind, planned.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandValidationException(
                $"node_id '{nodeId}' is created more than once with incompatible kinds '{previous.Kind}' and '{planned.Kind}'.");
        }

        plannedNodes[nodeId] = planned;
    }

    private void ValidatePythonFile(string nodeId, JsonElement args)
    {
        var filePath = args.RequireString("file_path");
        var resolved = _fileResolver.ResolvePath(filePath);
        if (!File.Exists(resolved))
        {
            throw new CommandValidationException(
                $"Python source file not found for node_id '{nodeId}': file_path '{filePath}' resolved to '{resolved}'.");
        }
    }

    private PlannedNodePorts BuildPlannedPortsForUpsert(
        string nodeId,
        TransactionOperationModel operation,
        IGH_DocumentObject? existing)
    {
        return operation.Op switch
        {
            TransactionOps.UpsertPythonNode => BuildPlannedPythonPorts(nodeId, operation.Args, existing),
            TransactionOps.UpsertComponent => BuildPlannedComponentPorts(nodeId, operation.Args, existing),
            TransactionOps.UpsertSlider => BuildControlPorts(nodeId, "slider", hasInput: false, hasOutput: true),
            TransactionOps.UpsertToggle => BuildControlPorts(nodeId, "toggle", hasInput: false, hasOutput: true),
            TransactionOps.UpsertPanel => BuildControlPorts(nodeId, "panel", hasInput: true, hasOutput: true),
            TransactionOps.UpsertNote => new PlannedNodePorts(nodeId, "group"),
            _ => throw new CommandValidationException($"Unsupported transaction op '{operation.Op}'.")
        };
    }

    private PlannedNodePorts BuildPlannedPythonPorts(string nodeId, JsonElement args, IGH_DocumentObject? existing)
    {
        var planned = new PlannedNodePorts(nodeId, "python_cpython3");
        var live = existing is null ? null : BuildLivePorts(nodeId, DetermineKind(existing), existing);
        var inputSchema = GetOptionalArray(args, "inputs");
        var outputSchema = GetOptionalArray(args, "outputs");

        if (inputSchema is null)
        {
            if (live is null)
            {
                planned.UnknownInputs = true;
            }
            else
            {
                CopyPorts(live.Inputs, planned.Inputs);
            }
        }
        else
        {
            AddSchemaPorts(planned.Inputs, inputSchema, "x");
        }

        if (outputSchema is null)
        {
            if (live is null)
            {
                planned.UnknownOutputs = true;
            }
            else
            {
                CopyPorts(live.Outputs, planned.Outputs);
            }
        }
        else
        {
            AddSchemaPorts(planned.Outputs, outputSchema, "a");
        }

        return planned;
    }

    private PlannedNodePorts BuildPlannedComponentPorts(string nodeId, JsonElement args, IGH_DocumentObject? existing)
    {
        var planned = new PlannedNodePorts(nodeId, "native_component");
        var live = existing is null ? null : BuildLivePorts(nodeId, DetermineKind(existing), existing);
        var inputSchema = GetOptionalArray(args, "inputs");
        var outputSchema = GetOptionalArray(args, "outputs");

        if (inputSchema is null)
        {
            if (live is null)
            {
                planned.UnknownInputs = true;
            }
            else
            {
                CopyPorts(live.Inputs, planned.Inputs);
            }
        }
        else
        {
            AddSchemaPorts(planned.Inputs, inputSchema, "in");
        }

        if (outputSchema is null)
        {
            if (live is null)
            {
                planned.UnknownOutputs = true;
            }
            else
            {
                CopyPorts(live.Outputs, planned.Outputs);
            }
        }
        else
        {
            AddSchemaPorts(planned.Outputs, outputSchema, "out");
        }

        return planned;
    }

    private static PlannedNodePorts BuildControlPorts(
        string nodeId,
        string kind,
        bool hasInput,
        bool hasOutput)
    {
        var planned = new PlannedNodePorts(nodeId, kind);
        if (hasInput)
        {
            planned.Inputs.Add("0");
        }

        if (hasOutput)
        {
            planned.Outputs.Add("0");
        }

        return planned;
    }

    private static PlannedNodePorts BuildLivePorts(string nodeId, string kind, IGH_DocumentObject obj)
    {
        var planned = new PlannedNodePorts(nodeId, kind);
        AddLivePorts(planned.Inputs, GetInputParams(obj));
        AddLivePorts(planned.Outputs, GetOutputParams(obj));
        return planned;
    }

    private static void AddLivePorts(HashSet<string> target, IEnumerable<IGH_Param> ports)
    {
        var index = 0;
        foreach (var port in ports)
        {
            target.Add(index.ToString());
            AddPortName(target, port.Name);
            AddPortName(target, port.NickName);
            AddPortName(target, SafePortName(port, "port"));
            index++;
        }
    }

    private static void AddSchemaPorts(HashSet<string> target, IReadOnlyList<JsonElement> schema, string fallbackPrefix)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            target.Add(i.ToString());

            var entry = schema[i];
            if (entry.ValueKind == JsonValueKind.String)
            {
                AddPortName(target, entry.GetString());
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new CommandValidationException("Port schema entries must be strings or objects.");
            }

            if (entry.TryGetPropertyIgnoreCase("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                AddPortName(target, name.GetString());
            }
            else
            {
                target.Add($"{fallbackPrefix}{i}");
            }
        }
    }

    private static void CopyPorts(IEnumerable<string> source, HashSet<string> target)
    {
        foreach (var port in source)
        {
            target.Add(port);
        }
    }

    private static void AddPortName(HashSet<string> target, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            target.Add(name);
        }
    }

    private static bool IsUpsertOperation(string op)
    {
        return op is TransactionOps.UpsertPythonNode
            or TransactionOps.UpsertComponent
            or TransactionOps.UpsertSlider
            or TransactionOps.UpsertToggle
            or TransactionOps.UpsertPanel
            or TransactionOps.UpsertNote;
    }

    private static bool ExistingNodeMatchesUpsert(IGH_DocumentObject existing, string op)
    {
        return op switch
        {
            TransactionOps.UpsertPythonNode => IsPythonNode(existing),
            TransactionOps.UpsertComponent => IsGenericNativeNode(existing),
            TransactionOps.UpsertSlider => existing is GH_NumberSlider,
            TransactionOps.UpsertToggle => existing is GH_BooleanToggle,
            TransactionOps.UpsertPanel => existing is GH_Panel,
            TransactionOps.UpsertNote => existing is GH_Scribble,
            _ => false
        };
    }

    private static void ValidateSetValueTarget(PlannedNodePorts target)
    {
        if (target.Kind is "slider" or "toggle" or "panel")
        {
            return;
        }

        throw new CommandValidationException(
            $"set_value does not support node_id '{target.NodeId}' of kind '{target.Kind}'. Supported kinds are slider, toggle, and panel.");
    }

    private static void ValidateWireOperation(
        JsonElement args,
        IReadOnlyDictionary<string, PlannedNodePorts> plannedNodes)
    {
        foreach (var wire in GetWireArray(args, "disconnect"))
        {
            ValidateWireEndpointPorts(wire, plannedNodes, TransactionOps.SetWires);
        }

        foreach (var wire in GetWireArray(args, "connect"))
        {
            ValidateWireEndpointPorts(wire, plannedNodes, TransactionOps.SetWires);
        }
    }

    private static IReadOnlyList<JsonElement> GetWireArray(JsonElement args, string name)
    {
        if (!args.TryGetPropertyIgnoreCase(name, out var value))
        {
            return Array.Empty<JsonElement>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CommandValidationException($"set_wires field '{name}' must be an array when provided.");
        }

        return value.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static void ValidateWireEndpointPorts(
        JsonElement wire,
        IReadOnlyDictionary<string, PlannedNodePorts> plannedNodes,
        string opName)
    {
        if (wire.ValueKind != JsonValueKind.Object)
        {
            throw new CommandValidationException($"Wire entry in '{opName}' must be an object.");
        }

        var sourceId = wire.RequireString("source_node_id");
        var sourcePort = wire.GetOptionalString("source_port") ?? "0";
        var targetId = wire.RequireString("target_node_id");
        var targetPort = wire.GetOptionalString("target_port") ?? "0";

        if (!plannedNodes.TryGetValue(sourceId, out var source))
        {
            throw new CommandValidationException($"Unknown source_node_id '{sourceId}' in {opName}.");
        }

        if (!plannedNodes.TryGetValue(targetId, out var target))
        {
            throw new CommandValidationException($"Unknown target_node_id '{targetId}' in {opName}.");
        }

        if (!source.HasPort(isOutput: true, sourcePort))
        {
            throw new CommandValidationException(
                $"Wire source port not found: source_node_id '{sourceId}' has no output port '{sourcePort}'. Available output ports: {source.DescribePorts(isOutput: true)}.");
        }

        if (!target.HasPort(isOutput: false, targetPort))
        {
            throw new CommandValidationException(
                $"Wire target port not found: target_node_id '{targetId}' has no input port '{targetPort}'. Available input ports: {target.DescribePorts(isOutput: false)}.");
        }
    }

    private static void ValidatePreviewOperation(
        JsonElement args,
        IReadOnlyDictionary<string, PlannedNodePorts> plannedNodes)
    {
        var mode = (TryGetJsonString(args, "mode", "action") ?? "show").Trim().ToLowerInvariant();
        if (mode is not ("isolate" or "restore" or "show" or "hide"))
        {
            throw new CommandValidationException("set_preview mode must be isolate, restore, show, or hide.");
        }

        if (mode == "restore")
        {
            return;
        }

        var nodeIds = ReadStringArray(args, "node_ids", "nodes");
        if (nodeIds.Count == 0)
        {
            throw new CommandValidationException($"set_preview {mode} requires node_ids.");
        }

        foreach (var nodeId in nodeIds)
        {
            if (!plannedNodes.ContainsKey(nodeId))
            {
                throw new CommandValidationException($"Unknown node_id '{nodeId}' in set_preview.");
            }
        }
    }

    private static void ValidateLayerVisibilityOperation(JsonElement args)
    {
        var mode = (TryGetJsonString(args, "mode", "action") ?? "show").Trim().ToLowerInvariant();
        if (mode is not ("isolate" or "restore" or "show" or "hide"))
        {
            throw new CommandValidationException("set_layer_visibility mode must be isolate, restore, show, or hide.");
        }

        if (mode != "restore" && string.IsNullOrWhiteSpace(TryGetJsonString(args, "layer_root", "root", "layer")))
        {
            throw new CommandValidationException($"set_layer_visibility {mode} requires layer_root.");
        }
    }
}
