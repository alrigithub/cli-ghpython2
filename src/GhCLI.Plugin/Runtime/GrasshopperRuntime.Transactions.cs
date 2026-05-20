using System.Drawing;
using System.Reflection;
using System.Text.Json;
using GhCLI.Core.Errors;
using GhCLI.Core.Hashing;
using GhCLI.Core.Json;
using GhCLI.Protocol;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Rhino;
using Rhino.DocObjects;

namespace GhCLI.Plugin.Runtime;

internal sealed partial class GrasshopperRuntime
{
    public TransactionApplyData ApplyTransaction(TransactionApplyRequest request)
    {
        lock (_gate)
        {
            if (request.Operations.Count == 0)
            {
                throw new CommandValidationException("txn.apply requires at least one operation.");
            }

            var doc = RequireActiveDocument();
            PreValidateTransaction(doc, request);

            var result = new TransactionApplyData();
            var wireOperations = new List<TransactionOperationModel>();
            var postSolveOperations = new List<TransactionOperationModel>();

            foreach (var operation in request.Operations)
            {
                if (operation.Op == TransactionOps.SetWires)
                {
                    wireOperations.Add(operation);
                    continue;
                }

                if (operation.Op is TransactionOps.SetPreview or TransactionOps.SetLayerVisibility)
                {
                    postSolveOperations.Add(operation);
                    continue;
                }

                ApplyTransactionOperation(doc, operation, result);
            }

            foreach (var operation in wireOperations)
            {
                ApplySetWires(doc, operation.Args, result);
            }

            SolveAfterTransaction(request);

            foreach (var operation in postSolveOperations)
            {
                ApplyTransactionOperation(doc, operation, result);
            }

            var summary = BuildCanvasSummary(doc, "full");
            result.Applied = true;
            result.GraphHash = summary.GraphHash;
            foreach (var nodeId in request.DebugAfter)
            {
                var target = ResolveNode(doc, nodeId, null)
                             ?? throw new CommandValidationException($"debugAfter node_id not found: {nodeId}");
                result.DebugReads.Add(BuildDebugRead(target));
            }

            return result;
        }
    }

    private void SolveAfterTransaction(TransactionApplyRequest request)
    {
        if (request.DebugAfter.Count > 0)
        {
            _ = SolveRun(new SolveRunRequest());
            return;
        }

        if (request.SolveAfter)
        {
            _ = SolveRun(new SolveRunRequest());
        }
    }

    public TransactionApplyData ApplyGraph(GraphApplyRequest request)
    {
        var operations = new List<TransactionOperationModel>();
        operations.AddRange(request.Sliders.Select(x => CreateOperation(TransactionOps.UpsertSlider, x)));
        operations.AddRange(request.Toggles.Select(x => CreateOperation(TransactionOps.UpsertToggle, x)));
        operations.AddRange(request.Panels.Select(x => CreateOperation(TransactionOps.UpsertPanel, x)));
        operations.AddRange(request.Notes.Select(x => CreateOperation(TransactionOps.UpsertNote, x)));
        operations.AddRange(request.PythonNodes.Select(x => CreateOperation(TransactionOps.UpsertPythonNode, x)));
        operations.AddRange(request.Components.Select(x => CreateOperation(TransactionOps.UpsertComponent, x)));

        if (request.Wires.Count > 0)
        {
            operations.Add(new TransactionOperationModel
            {
                Op = TransactionOps.SetWires,
                Args = ToJsonElement(new { connect = request.Wires })
            });
        }

        if (request.Preview is { ValueKind: JsonValueKind.Object } preview)
        {
            operations.Add(CreateOperation(TransactionOps.SetPreview, preview));
        }

        if (request.LayerVisibility is { ValueKind: JsonValueKind.Object } layerVisibility)
        {
            operations.Add(CreateOperation(TransactionOps.SetLayerVisibility, layerVisibility));
        }

        return ApplyTransaction(new TransactionApplyRequest
        {
            TransactionId = request.TransactionId,
            Operations = operations,
            SolveAfter = request.SolveAfter,
            DebugAfter = request.DebugAfter
        });
    }

    private static TransactionOperationModel CreateOperation(string op, JsonElement args)
    {
        return new TransactionOperationModel
        {
            Op = op,
            Args = args.Clone()
        };
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, ProtocolJson.Options));
        return doc.RootElement.Clone();
    }

    private void ApplyTransactionOperation(GH_Document doc, TransactionOperationModel operation, TransactionApplyData result)
    {
        switch (operation.Op)
        {
            case TransactionOps.UpsertPythonNode:
                ApplyUpsertPythonNode(doc, operation.Args, result);
                break;
            case TransactionOps.UpsertComponent:
                ApplyUpsertComponent(doc, operation.Args, result);
                break;
            case TransactionOps.UpsertSlider:
                ApplyUpsertSlider(doc, operation.Args, result);
                break;
            case TransactionOps.UpsertToggle:
                ApplyUpsertToggle(doc, operation.Args, result);
                break;
            case TransactionOps.UpsertPanel:
                ApplyUpsertPanel(doc, operation.Args, result);
                break;
            case TransactionOps.UpsertNote:
                ApplyUpsertNote(doc, operation.Args, result);
                break;
            case TransactionOps.MoveNode:
                ApplyMoveNode(doc, operation.Args, result);
                break;
            case TransactionOps.SetValue:
                ApplySetValue(doc, operation.Args, result);
                break;
            case TransactionOps.SetPreview:
                ApplySetPreview(doc, operation.Args, result);
                break;
            case TransactionOps.SetLayerVisibility:
                ApplySetLayerVisibility(operation.Args, result);
                break;
            default:
                throw new CommandValidationException($"Unsupported transaction op '{operation.Op}'.");
        }
    }

    private void ApplyUpsertPythonNode(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var runtime = args.GetOptionalString("runtime") ?? "cpython3";
        var filePath = args.RequireString("file_path");
        var resolvedPath = _fileResolver.ResolvePath(filePath);
        var source = File.ReadAllText(resolvedPath);
        var nickname = args.GetOptionalString("nickname") ?? nodeId;
        var position = ReadPosition(args, 160, 120);

        var node = ResolveNode(doc, nodeId, null);
        if (node is null)
        {
            node = CreatePythonNode(runtime);
            if (node is null)
            {
                throw new CommandValidationException("Could not create a Rhino 8 Python script component.");
            }

            node.CreateAttributes();
            doc.AddObject(node, false);
            _nodeMetadata.Bind(node.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else
        {
            if (!IsPythonNode(node))
            {
                throw new CommandValidationException($"node_id '{nodeId}' exists but is not a Python node.");
            }

            result.Patched.Add(nodeId);
        }

        node.NickName = nickname;
        SetPosition(node, position);

        if (!TrySetPythonSource(node, source))
        {
            result.Warnings.Add($"Could not set Python source for '{nodeId}' via known script APIs.");
        }

        ApplyPortSchema(node, GetOptionalArray(args, "inputs"), GetOptionalArray(args, "outputs"), result.Warnings);

        _nodeMetadata.SetPythonMetadata(nodeId, new PythonNodeMetadata
        {
            Runtime = runtime,
            FilePath = resolvedPath,
            SourceHash = Sha256Hasher.ShortHash(source)
        });

        if (node is IGH_ActiveObject active)
        {
            active.ExpireSolution(false);
        }
    }

    private void ApplyUpsertComponent(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var componentName = TryGetJsonString(args, "component", "component_name", "name", "type");
        var nickname = args.GetOptionalString("nickname") ?? componentName ?? nodeId;
        var position = ReadPosition(args, 160, 120);

        var existing = ResolveNode(doc, nodeId, null);
        IGH_DocumentObject node;

        if (existing is null)
        {
            node = CreateNativeComponent(args)
                   ?? throw new CommandValidationException(
                       $"Could not create native Grasshopper component for node_id '{nodeId}'. " +
                       "Provide a valid component_guid or component/name such as CustomPreview, TextTag, PointList, or VectorDisplay.");
            node.CreateAttributes();
            doc.AddObject(node, false);
            _nodeMetadata.Bind(node.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else if (IsGenericNativeNode(existing))
        {
            node = existing;
            result.Patched.Add(nodeId);
        }
        else
        {
            throw new CommandValidationException($"node_id '{nodeId}' exists but is not a native Grasshopper component/parameter.");
        }

        node.NickName = nickname;
        SetPosition(node, position);

        if (TryReadColor(args, out var color))
        {
            ApplyColorValue(node, color, result.Warnings, nodeId);
        }

        if (args.TryGetPropertyIgnoreCase("hidden", out var hiddenElement) &&
            hiddenElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            SetPreviewHidden(node, hiddenElement.GetBoolean());
        }

        if (node is IGH_ActiveObject active)
        {
            active.ExpireSolution(false);
        }
    }

    private void ApplyUpsertSlider(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var nickname = args.GetOptionalString("nickname") ?? nodeId;
        var position = ReadPosition(args, 80, 80);
        var min = args.GetOptionalDouble("min") ?? 0d;
        var max = args.GetOptionalDouble("max") ?? 1d;
        var value = args.GetOptionalDouble("value") ?? min;

        var existing = ResolveNode(doc, nodeId, null);
        GH_NumberSlider slider;

        if (existing is null)
        {
            slider = new GH_NumberSlider();
            slider.CreateAttributes();
            doc.AddObject(slider, false);
            _nodeMetadata.Bind(slider.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else if (existing is GH_NumberSlider existingSlider)
        {
            slider = existingSlider;
            result.Patched.Add(nodeId);
        }
        else
        {
            throw new CommandValidationException($"node_id '{nodeId}' exists but is not a slider.");
        }

        slider.NickName = nickname;
        SetPosition(slider, position);
        SetSliderRangeAndValue(slider, min, max, value);
    }

    private void ApplyUpsertToggle(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var nickname = args.GetOptionalString("nickname") ?? nodeId;
        var position = ReadPosition(args, 80, 120);
        var value = args.GetOptionalBool("value");

        var existing = ResolveNode(doc, nodeId, null);
        GH_BooleanToggle toggle;

        if (existing is null)
        {
            toggle = new GH_BooleanToggle();
            toggle.CreateAttributes();
            doc.AddObject(toggle, false);
            _nodeMetadata.Bind(toggle.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else if (existing is GH_BooleanToggle existingToggle)
        {
            toggle = existingToggle;
            result.Patched.Add(nodeId);
        }
        else
        {
            throw new CommandValidationException($"node_id '{nodeId}' exists but is not a toggle.");
        }

        toggle.NickName = nickname;
        SetPosition(toggle, position);
        TrySetPropertyValue(toggle, "Value", value);
    }

    private void ApplyUpsertPanel(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var nickname = args.GetOptionalString("nickname") ?? nodeId;
        var position = ReadPosition(args, 80, 160);
        var text = args.GetOptionalString("text") ?? string.Empty;

        var existing = ResolveNode(doc, nodeId, null);
        GH_Panel panel;

        if (existing is null)
        {
            panel = new GH_Panel();
            panel.CreateAttributes();
            doc.AddObject(panel, false);
            _nodeMetadata.Bind(panel.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else if (existing is GH_Panel existingPanel)
        {
            panel = existingPanel;
            result.Patched.Add(nodeId);
        }
        else
        {
            throw new CommandValidationException($"node_id '{nodeId}' exists but is not a panel.");
        }

        panel.NickName = nickname;
        SetPosition(panel, position);
        if (!TrySetPropertyValue(panel, "UserText", text))
        {
            _ = TrySetPropertyValue(panel, "Text", text);
        }
    }

    private void ApplyUpsertNote(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var nickname = args.GetOptionalString("nickname") ?? nodeId;
        var position = ReadPosition(args, 80, 220);
        var text = args.GetOptionalString("text") ?? nickname;

        var existing = ResolveNode(doc, nodeId, null);
        GH_Scribble note;

        if (existing is null)
        {
            note = new GH_Scribble();
            note.CreateAttributes();
            doc.AddObject(note, false);
            _nodeMetadata.Bind(note.InstanceGuid, nodeId);
            result.Created.Add(nodeId);
        }
        else if (existing is GH_Scribble existingNote)
        {
            note = existingNote;
            result.Patched.Add(nodeId);
        }
        else
        {
            throw new CommandValidationException($"node_id '{nodeId}' exists but is not a note.");
        }

        note.NickName = nickname;
        SetPosition(note, position);
        _ = TrySetPropertyValue(note, "Text", text);
    }

    private void ApplySetWires(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        foreach (var entry in args.GetArray("disconnect"))
        {
            ApplyWireOperation(doc, entry, result, connect: false);
        }

        foreach (var entry in args.GetArray("connect"))
        {
            ApplyWireOperation(doc, entry, result, connect: true);
        }
    }

    private void ApplyWireOperation(GH_Document doc, JsonElement entry, TransactionApplyData result, bool connect)
    {
        var sourceId = entry.RequireString("source_node_id");
        var sourcePort = entry.GetOptionalString("source_port") ?? "0";
        var targetId = entry.RequireString("target_node_id");
        var targetPort = entry.GetOptionalString("target_port") ?? "0";

        var sourceNode = ResolveNode(doc, sourceId, null)
                         ?? throw new CommandValidationException($"Wire source node not found: {sourceId}");
        var targetNode = ResolveNode(doc, targetId, null)
                         ?? throw new CommandValidationException($"Wire target node not found: {targetId}");

        var sourceParam = ResolvePort(sourceNode, isOutput: true, sourcePort)
                          ?? throw new CommandValidationException($"Wire source port not found: {sourceId}.{sourcePort}");
        var targetParam = ResolvePort(targetNode, isOutput: false, targetPort)
                          ?? throw new CommandValidationException($"Wire target port not found: {targetId}.{targetPort}");

        if (connect)
        {
            if (targetParam.Sources.Contains(sourceParam))
            {
                return;
            }

            targetParam.AddSource(sourceParam);
        }
        else
        {
            if (!targetParam.Sources.Contains(sourceParam))
            {
                return;
            }

            targetParam.RemoveSource(sourceParam);
        }

        result.WireChanges++;
    }

    private void ApplyMoveNode(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var node = ResolveNode(doc, nodeId, null)
                   ?? throw new CommandValidationException($"Unknown node_id '{nodeId}' for move_node.");

        var position = ReadPosition(args, node.Attributes?.Pivot.X ?? 0, node.Attributes?.Pivot.Y ?? 0);
        SetPosition(node, position);
        result.Patched.Add(nodeId);
    }

    private void ApplySetValue(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var nodeId = args.RequireString("node_id");
        var node = ResolveNode(doc, nodeId, null)
                   ?? throw new CommandValidationException($"Unknown node_id '{nodeId}' for set_value.");

        if (!args.TryGetPropertyIgnoreCase("value", out var valueElement))
        {
            throw new CommandValidationException("set_value requires a value field.");
        }

        if (node is GH_NumberSlider slider)
        {
            var value = valueElement.ValueKind == JsonValueKind.Number
                ? valueElement.GetDouble()
                : double.Parse(valueElement.GetString() ?? "0");
            SetSliderValue(slider, value);
        }
        else if (node is GH_BooleanToggle toggle)
        {
            var value = valueElement.ValueKind == JsonValueKind.True ||
                        (valueElement.ValueKind == JsonValueKind.String &&
                         bool.Parse(valueElement.GetString() ?? "false"));
            _ = TrySetPropertyValue(toggle, "Value", value);
        }
        else if (node is GH_Panel panel)
        {
            var text = valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString() ?? string.Empty
                : valueElement.ToString();
            if (!TrySetPropertyValue(panel, "UserText", text))
            {
                _ = TrySetPropertyValue(panel, "Text", text);
            }
        }
        else
        {
            throw new CommandValidationException(
                $"set_value does not support node '{nodeId}' of type {node.GetType().Name}.");
        }

        if (node is IGH_ActiveObject active)
        {
            active.ExpireSolution(false);
        }

        result.Patched.Add(nodeId);
    }

    private void ApplySetPreview(GH_Document doc, JsonElement args, TransactionApplyData result)
    {
        var mode = (TryGetJsonString(args, "mode", "action") ?? "show").Trim().ToLowerInvariant();
        var stateId = TryGetJsonString(args, "state_id", "stateId") ?? "default";
        var allObjects = doc.Objects.OfType<IGH_DocumentObject>().ToList();
        var nodeIds = ReadStringArray(args, "node_ids", "nodes");

        switch (mode)
        {
            case "isolate":
                if (nodeIds.Count == 0)
                {
                    throw new CommandValidationException("set_preview isolate requires node_ids.");
                }

                var targetGuids = ResolveNodeIds(doc, nodeIds).Select(x => x.InstanceGuid).ToHashSet();
                _previewStates[stateId] = allObjects.ToDictionary(x => x.InstanceGuid, GetPreviewHidden);
                foreach (var obj in allObjects)
                {
                    SetPreviewHidden(obj, !targetGuids.Contains(obj.InstanceGuid));
                }

                result.Patched.Add($"preview:{stateId}:isolate");
                break;

            case "restore":
                if (_previewStates.TryGetValue(stateId, out var previous))
                {
                    foreach (var obj in allObjects)
                    {
                        if (previous.TryGetValue(obj.InstanceGuid, out var hidden))
                        {
                            SetPreviewHidden(obj, hidden);
                        }
                    }

                    _previewStates.Remove(stateId);
                }

                result.Patched.Add($"preview:{stateId}:restore");
                break;

            case "show":
            case "hide":
                if (nodeIds.Count == 0)
                {
                    throw new CommandValidationException($"set_preview {mode} requires node_ids.");
                }

                foreach (var node in ResolveNodeIds(doc, nodeIds))
                {
                    SetPreviewHidden(node, mode == "hide");
                    result.Patched.Add(_nodeMetadata.GetOrCreateNodeId(node, KindPrefix(DetermineKind(node))));
                }

                break;

            default:
                throw new CommandValidationException("set_preview mode must be isolate, restore, show, or hide.");
        }

        RefreshPreview(doc);
    }

    private void ApplySetLayerVisibility(JsonElement args, TransactionApplyData result)
    {
        var rhinoDoc = RhinoDoc.ActiveDoc
                       ?? throw new CommandValidationException("set_layer_visibility requires an active Rhino document.");
        var mode = (TryGetJsonString(args, "mode", "action") ?? "show").Trim().ToLowerInvariant();
        var stateId = TryGetJsonString(args, "state_id", "stateId") ?? "default";
        var root = TryGetJsonString(args, "layer_root", "root", "layer");
        var layers = rhinoDoc.Layers.Where(x => !x.IsDeleted).ToList();

        switch (mode)
        {
            case "isolate":
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new CommandValidationException("set_layer_visibility isolate requires layer_root.");
                }

                _layerVisibilityStates[stateId] = layers.ToDictionary(x => x.Id, x => x.IsVisible);
                foreach (var layer in layers)
                {
                    SetLayerVisible(rhinoDoc, layer, IsLayerUnderRoot(layer, root));
                }

                result.Patched.Add($"layers:{stateId}:isolate");
                break;

            case "restore":
                if (_layerVisibilityStates.TryGetValue(stateId, out var previous))
                {
                    foreach (var layer in layers)
                    {
                        if (previous.TryGetValue(layer.Id, out var visible))
                        {
                            SetLayerVisible(rhinoDoc, layer, visible);
                        }
                    }

                    _layerVisibilityStates.Remove(stateId);
                }

                result.Patched.Add($"layers:{stateId}:restore");
                break;

            case "show":
            case "hide":
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new CommandValidationException($"set_layer_visibility {mode} requires layer_root.");
                }

                foreach (var layer in layers.Where(x => IsLayerUnderRoot(x, root)))
                {
                    SetLayerVisible(rhinoDoc, layer, mode == "show");
                }

                result.Patched.Add($"layers:{root}:{mode}");
                break;

            default:
                throw new CommandValidationException("set_layer_visibility mode must be isolate, restore, show, or hide.");
        }

        rhinoDoc.Views.Redraw();
    }

    private static IReadOnlyList<JsonElement>? GetOptionalArray(JsonElement element, string name)
    {
        if (!element.TryGetPropertyIgnoreCase(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CommandValidationException($"'{name}' must be an array when provided.");
        }

        return value.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static PositionModel ReadPosition(JsonElement args, double defaultX, double defaultY)
    {
        if (args.TryGetPropertyIgnoreCase("position", out var position) && position.ValueKind == JsonValueKind.Object)
        {
            return new PositionModel
            {
                X = position.GetOptionalDouble("x") ?? defaultX,
                Y = position.GetOptionalDouble("y") ?? defaultY
            };
        }

        return new PositionModel { X = defaultX, Y = defaultY };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement args, params string[] names)
    {
        foreach (var name in names)
        {
            if (!args.TryGetPropertyIgnoreCase(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new CommandValidationException($"'{name}' must be an array of strings.");
            }

            return value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<IGH_DocumentObject> ResolveNodeIds(GH_Document doc, IReadOnlyList<string> nodeIds)
    {
        var nodes = new List<IGH_DocumentObject>();
        foreach (var nodeId in nodeIds)
        {
            var node = ResolveNode(doc, nodeId, null)
                       ?? throw new CommandValidationException($"Unknown node_id '{nodeId}' in preview operation.");
            nodes.Add(node);
        }

        return nodes;
    }

    private static void SetPosition(IGH_DocumentObject node, PositionModel position)
    {
        node.CreateAttributes();
        if (node.Attributes is null)
        {
            return;
        }

        node.Attributes.Pivot = new PointF((float)position.X, (float)position.Y);
        node.Attributes.ExpireLayout();
    }

    private static bool GetPreviewHidden(IGH_DocumentObject obj)
    {
        return obj is IGH_PreviewObject preview
            ? preview.Hidden
            : TryGetBoolPropertyValue(obj, "Hidden");
    }

    private static void SetPreviewHidden(IGH_DocumentObject obj, bool hidden)
    {
        if (obj is IGH_PreviewObject preview)
        {
            preview.Hidden = hidden;
        }
        else
        {
            _ = TrySetPropertyValue(obj, "Hidden", hidden);
        }

        _ = TryInvokeNoArg(obj, "ExpirePreview", "OnDisplayExpired", "ExpireLayout");
    }

    private static void RefreshPreview(GH_Document doc)
    {
        RhinoDoc.ActiveDoc?.Views.Redraw();
        _ = TryInvokeNoArg(doc, "OnDisplayExpired");
    }

    private static bool IsLayerUnderRoot(Layer layer, string root)
    {
        var fullPath = layer.FullPath ?? layer.Name;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(root + "::", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetLayerVisible(RhinoDoc doc, Layer layer, bool visible)
    {
        if (layer.IsVisible == visible)
        {
            return;
        }

        layer.IsVisible = visible;
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private IGH_DocumentObject? CreatePythonNode(string runtime)
    {
        var runtimeLower = runtime.ToLowerInvariant();
        var proxies = Instances.ComponentServer?.ObjectProxies;
        if (proxies is null)
        {
            return null;
        }

        Guid? selectedGuid = null;
        Guid? fallbackGuid = null;

        foreach (var proxy in proxies)
        {
            var name = proxy.Desc?.Name ?? string.Empty;
            if (!name.Contains("Python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            fallbackGuid ??= proxy.Guid;
            if (runtimeLower.Contains("cpython", StringComparison.OrdinalIgnoreCase) ||
                runtimeLower.Contains("python3", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Contains("Python 3", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CPython", StringComparison.OrdinalIgnoreCase))
                {
                    selectedGuid = proxy.Guid;
                    break;
                }
            }
            else if (name.Contains("IronPython", StringComparison.OrdinalIgnoreCase))
            {
                selectedGuid = proxy.Guid;
                break;
            }
        }

        var guid = selectedGuid ?? fallbackGuid;
        if (guid is null)
        {
            return null;
        }

        return Instances.ComponentServer?.EmitObject(guid.Value) as IGH_DocumentObject;
    }

    private IGH_DocumentObject? CreateNativeComponent(JsonElement args)
    {
        if (TryGetJsonString(args, "component_guid", "guid", "id") is { } guidText &&
            Guid.TryParse(guidText, out var guid))
        {
            return Instances.ComponentServer?.EmitObject(guid) as IGH_DocumentObject;
        }

        var requested = TryGetJsonString(args, "component", "component_name", "name", "type");
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new CommandValidationException("upsert_component requires component/component_name/name or component_guid.");
        }

        if (IsButtonName(requested))
        {
            var button = TryCreateGrasshopperSpecial("Grasshopper.Kernel.Special.GH_Button")
                         ?? TryCreateGrasshopperSpecialByName("Button");
            if (button is not null)
            {
                return button;
            }
        }

        var proxies = Instances.ComponentServer?.ObjectProxies;
        if (proxies is not null)
        {
            foreach (var candidate in EnumerateComponentNameCandidates(requested))
            {
                var exact = proxies.FirstOrDefault(proxy => ComponentProxyMatches(proxy, candidate, exact: true));
                if (exact is not null)
                {
                    return Instances.ComponentServer?.EmitObject(exact.Guid) as IGH_DocumentObject;
                }
            }

            foreach (var candidate in EnumerateComponentNameCandidates(requested))
            {
                var fuzzy = proxies.FirstOrDefault(proxy => ComponentProxyMatches(proxy, candidate, exact: false));
                if (fuzzy is not null)
                {
                    return Instances.ComponentServer?.EmitObject(fuzzy.Guid) as IGH_DocumentObject;
                }
            }
        }

        if (IsColourSwatchName(requested))
        {
            return TryCreateGrasshopperSpecial("Grasshopper.Kernel.Special.GH_ColourSwatch")
                   ?? TryCreateGrasshopperSpecial("Grasshopper.Kernel.Special.GH_ColorSwatch")
                   ?? TryCreateGrasshopperSpecialByName("ColourSwatch")
                   ?? TryCreateGrasshopperSpecialByName("ColorSwatch");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateComponentNameCandidates(string requested)
    {
        var normalized = NormalizeComponentName(requested);
        yield return requested;

        foreach (var alias in normalized switch
                 {
                     "custompreview" => new[] { "Custom Preview", "CustomPreview" },
                     "colour_swatch" or "colourswatch" or "colorswatch" => new[] { "Colour Swatch", "Color Swatch", "ColourSwatch", "ColorSwatch" },
                     "button" or "pushbutton" => new[] { "Button", "Push Button", "PushButton" },
                     "pointlist" => new[] { "Point List", "PointList" },
                     "vectordisplay" => new[] { "Vector Display", "VectorDisplay" },
                     "vectordisplayex" => new[] { "Vector Display Ex", "VectorDisplayEx" },
                     "texttag" => new[] { "Text Tag", "TextTag" },
                     "texttag3d" => new[] { "Text Tag 3D", "TextTag3D" },
                     _ => Array.Empty<string>()
                 })
        {
            yield return alias;
        }
    }

    private static bool ComponentProxyMatches(object proxy, string requested, bool exact)
    {
        var desc = TryGetPropertyValue(proxy, "Desc");
        var values = new[]
        {
            TryGetPropertyValue(desc, "Name")?.ToString(),
            TryGetPropertyValue(desc, "NickName")?.ToString(),
            TryGetPropertyValue(desc, "Description")?.ToString()
        };

        var requestedNormalized = NormalizeComponentName(requested);
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var candidate = NormalizeComponentName(value!);
            if (exact && string.Equals(candidate, requestedNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!exact &&
                (candidate.Contains(requestedNormalized, StringComparison.OrdinalIgnoreCase) ||
                 requestedNormalized.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeComponentName(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool IsColourSwatchName(string value)
    {
        var normalized = NormalizeComponentName(value);
        return normalized is "colourswatch" or "colorswatch";
    }

    private static bool IsButtonName(string value)
    {
        var normalized = NormalizeComponentName(value);
        return normalized is "button" or "pushbutton";
    }

    private static IGH_DocumentObject? TryCreateGrasshopperSpecial(string fullTypeName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(x => x.GetType(fullTypeName, throwOnError: false))
            .FirstOrDefault(x => x is not null);

        if (type is null)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type) as IGH_DocumentObject;
        }
        catch
        {
            return null;
        }
    }

    private static IGH_DocumentObject? TryCreateGrasshopperSpecialByName(string typeNamePart)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            var type = types.FirstOrDefault(x =>
                typeof(IGH_DocumentObject).IsAssignableFrom(x) &&
                x.GetConstructor(Type.EmptyTypes) is not null &&
                x.Name.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase));

            if (type is null)
            {
                continue;
            }

            try
            {
                return Activator.CreateInstance(type) as IGH_DocumentObject;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsGenericNativeNode(IGH_DocumentObject obj)
    {
        if (IsPythonNode(obj) ||
            obj is GH_NumberSlider ||
            obj is GH_BooleanToggle ||
            obj is GH_Panel ||
            obj is GH_Scribble)
        {
            return false;
        }

        return obj is IGH_Component or IGH_Param;
    }

    private static bool TryReadColor(JsonElement args, out Color color)
    {
        color = Color.Empty;
        if (!args.TryGetPropertyIgnoreCase("color", out var value) &&
            !args.TryGetPropertyIgnoreCase("colour", out value))
        {
            return false;
        }

        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim() ?? string.Empty;
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    text = text[1..];
                }

                if (text.Length == 6 || text.Length == 8)
                {
                    var offset = text.Length == 8 ? 2 : 0;
                    var alpha = text.Length == 8 ? Convert.ToInt32(text[..2], 16) : 255;
                    var red = Convert.ToInt32(text.Substring(offset, 2), 16);
                    var green = Convert.ToInt32(text.Substring(offset + 2, 2), 16);
                    var blue = Convert.ToInt32(text.Substring(offset + 4, 2), 16);
                    color = Color.FromArgb(alpha, red, green, blue);
                    return true;
                }
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var values = value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetInt32())
                    .ToArray();
                if (values.Length >= 3)
                {
                    color = Color.FromArgb(values.Length >= 4 ? values[3] : 255, values[0], values[1], values[2]);
                    return true;
                }
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var red = value.GetOptionalInt("r") ?? value.GetOptionalInt("red") ?? 0;
                var green = value.GetOptionalInt("g") ?? value.GetOptionalInt("green") ?? 0;
                var blue = value.GetOptionalInt("b") ?? value.GetOptionalInt("blue") ?? 0;
                var alpha = value.GetOptionalInt("a") ?? value.GetOptionalInt("alpha") ?? 255;
                color = Color.FromArgb(alpha, red, green, blue);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void ApplyColorValue(IGH_DocumentObject node, Color color, List<string> warnings, string nodeId)
    {
        var applied = TrySetPropertyValue(node, "SwatchColour", color)
                      || TrySetPropertyValue(node, "SwatchColor", color)
                      || TrySetPropertyValue(node, "Colour", color)
                      || TrySetPropertyValue(node, "Color", color)
                      || TrySetPropertyValue(node, "Value", color);

        if (!applied)
        {
            warnings.Add($"Could not apply color value to native component '{nodeId}'.");
        }
    }

    private static bool TrySetPythonSource(IGH_DocumentObject node, string source)
    {
        string[] propertyNames = { "Code", "SourceCode", "Script", "ScriptSource", "Source" };
        foreach (var property in propertyNames)
        {
            if (TrySetPropertyValue(node, property, source))
            {
                return true;
            }
        }

        string[] methodNames = { "SetCode", "SetSource", "SetScript", "SetScriptSource" };
        foreach (var methodName in methodNames)
        {
            var methods = node.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(x => x.Name == methodName)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                {
                    continue;
                }

                method.Invoke(node, new object[] { source });
                return true;
            }
        }

        return false;
    }

    private static string? TryGetPythonSource(IGH_DocumentObject node)
    {
        string[] propertyNames = { "Code", "SourceCode", "Script", "ScriptSource", "Source" };
        foreach (var property in propertyNames)
        {
            if (TryGetPropertyValue(node, property) is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static void SetSliderRangeAndValue(GH_NumberSlider slider, double min, double max, double value)
    {
        var sliderCore = TryGetPropertyValue(slider, "Slider");
        if (sliderCore is not null)
        {
            _ = TrySetPropertyValue(sliderCore, "Minimum", ConvertToType(min, TryGetPropertyType(sliderCore, "Minimum")));
            _ = TrySetPropertyValue(sliderCore, "Maximum", ConvertToType(max, TryGetPropertyType(sliderCore, "Maximum")));
        }

        SetSliderValue(slider, value);
    }

    private static void SetSliderValue(GH_NumberSlider slider, double value)
    {
        if (TryInvokeNumeric(slider, "SetSliderValue", value))
        {
            return;
        }

        var sliderCore = TryGetPropertyValue(slider, "Slider");
        if (sliderCore is not null)
        {
            _ = TrySetPropertyValue(sliderCore, "Value", ConvertToType(value, TryGetPropertyType(sliderCore, "Value")));
        }
    }
}
