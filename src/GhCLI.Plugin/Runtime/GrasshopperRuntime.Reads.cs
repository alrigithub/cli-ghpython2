using System.Collections;
using System.Drawing;
using System.Reflection;
using GhCLI.Core.Graph;
using GhCLI.Core.Hashing;
using GhCLI.Protocol;
using Grasshopper.Kernel;

namespace GhCLI.Plugin.Runtime;

internal sealed partial class GrasshopperRuntime
{
    private CanvasSummaryData BuildCanvasSummary(GH_Document doc, string? scope)
    {
        var scopeValue = string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase)
            ? "selected"
            : "full";

        var allObjects = doc.Objects.OfType<IGH_DocumentObject>().ToList();
        var selected = allObjects.Where(IsSelected).ToList();
        var scopeObjects = scopeValue == "selected" ? selected : allObjects;
        var inScope = new HashSet<Guid>(scopeObjects.Select(x => x.InstanceGuid));
        var ownerMap = BuildParamOwnerMap(allObjects);

        var nodes = scopeObjects.Select(BuildNodeSummary).ToList();
        var edges = BuildEdges(scopeObjects, inScope, ownerMap).ToList();
        var graphHash = GraphHashBuilder.Build(nodes, edges);

        return new CanvasSummaryData
        {
            SessionId = _sessionId,
            DocumentId = TryGetPropertyValue(doc, "DocumentID")?.ToString(),
            DocumentName = doc.DisplayName,
            GraphHash = graphHash,
            NodeCount = nodes.Count,
            EdgeCount = edges.Count,
            SelectedNodeIds = selected
                .Select(obj => _nodeMetadata.GetOrCreateNodeId(obj, KindPrefix(DetermineKind(obj))))
                .ToList(),
            Nodes = nodes,
            Edges = edges
        };
    }

    private List<CanvasEdgeSummaryModel> BuildEdges(
        IReadOnlyCollection<IGH_DocumentObject> scopeObjects,
        HashSet<Guid> inScope,
        IReadOnlyDictionary<IGH_Param, IGH_DocumentObject> ownerMap)
    {
        var result = new List<CanvasEdgeSummaryModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var targetObject in scopeObjects)
        {
            foreach (var targetParam in GetInputParams(targetObject))
            {
                foreach (var sourceParam in targetParam.Sources)
                {
                    var sourceObject = TryGetOwnerObject(sourceParam, ownerMap);
                    if (sourceObject is null)
                    {
                        continue;
                    }

                    if (!inScope.Contains(sourceObject.InstanceGuid) || !inScope.Contains(targetObject.InstanceGuid))
                    {
                        continue;
                    }

                    var sourceNodeId = _nodeMetadata.GetOrCreateNodeId(sourceObject, KindPrefix(DetermineKind(sourceObject)));
                    var targetNodeId = _nodeMetadata.GetOrCreateNodeId(targetObject, KindPrefix(DetermineKind(targetObject)));

                    var edge = new CanvasEdgeSummaryModel
                    {
                        SourceNodeId = sourceNodeId,
                        SourcePort = SafePortName(sourceParam, "out"),
                        TargetNodeId = targetNodeId,
                        TargetPort = SafePortName(targetParam, "in")
                    };

                    var key = $"{edge.SourceNodeId}|{edge.SourcePort}|{edge.TargetNodeId}|{edge.TargetPort}";
                    if (seen.Add(key))
                    {
                        result.Add(edge);
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<IGH_Param, IGH_DocumentObject> BuildParamOwnerMap(
        IEnumerable<IGH_DocumentObject> objects)
    {
        var result = new Dictionary<IGH_Param, IGH_DocumentObject>();
        foreach (var obj in objects)
        {
            foreach (var input in GetInputParams(obj))
            {
                result[input] = obj;
            }

            foreach (var output in GetOutputParams(obj))
            {
                result[output] = obj;
            }
        }

        return result;
    }

    private CanvasNodeSummaryModel BuildNodeSummary(IGH_DocumentObject obj)
    {
        var kind = DetermineKind(obj);
        var nodeId = _nodeMetadata.GetOrCreateNodeId(obj, KindPrefix(kind));
        var position = obj.Attributes?.Pivot ?? PointF.Empty;

        var summary = new CanvasNodeSummaryModel
        {
            Id = nodeId,
            NodeId = nodeId,
            ComponentId = obj.InstanceGuid.ToString("D"),
            Kind = kind,
            TypeName = obj.GetType().Name,
            Nickname = string.IsNullOrWhiteSpace(obj.NickName) ? obj.Name : obj.NickName,
            Position = new PositionModel
            {
                X = position.X,
                Y = position.Y
            },
            Inputs = GetInputParams(obj)
                .Select(p => BuildPortSummary(p, "in"))
                .ToList(),
            Outputs = GetOutputParams(obj)
                .Select(p => BuildPortSummary(p, "out"))
                .ToList(),
            Flags = new NodeFlagsModel
            {
                Selected = IsSelected(obj),
                Locked = TryGetBoolPropertyValue(obj, "Locked"),
                Hidden = GetPreviewHidden(obj)
            }
        };

        if (IsPythonNode(obj))
        {
            _nodeMetadata.TryGetPythonMetadata(nodeId, out var pythonMeta);
            summary.Python = new PythonSummaryModel
            {
                Runtime = pythonMeta?.Runtime ?? (kind == "python_ironpython" ? "ironpython" : "cpython3"),
                SourceHash = pythonMeta?.SourceHash ?? GetSourceHashFromObject(obj),
                InputCount = summary.Inputs.Count,
                OutputCount = summary.Outputs.Count
            };
        }

        return summary;
    }

    private NodeReadData BuildNodeRead(GH_Document doc, NodeReadRequest request)
    {
        var node = ResolveNode(doc, request.NodeId, request.ComponentId);
        if (node is null)
        {
            throw new GhCLI.Core.Errors.CommandValidationException(
                "node.read could not find a node for the provided identifier.");
        }

        var summary = BuildNodeSummary(node);
        var outputSummaries = SummarizeOutputs(node);
        var runtimeMessages = CollectRuntimeMessages(node);

        string? source = null;
        string? sourceHash = null;

        if (IsPythonNode(node))
        {
            var nodeId = summary.NodeId;
            if (_nodeMetadata.TryGetPythonMetadata(nodeId, out var meta))
            {
                sourceHash = meta.SourceHash;
            }

            var liveSource = TryGetPythonSource(node);
            if (!string.IsNullOrWhiteSpace(liveSource))
            {
                sourceHash ??= Sha256Hasher.ShortHash(liveSource);
                if (request.IncludeSource)
                {
                    source = liveSource;
                }
            }
        }

        return new NodeReadData
        {
            Node = summary,
            RuntimeMessages = runtimeMessages,
            VariableParams = GetVariableParams(node),
            ValueState = ReadValueState(summary.NodeId, node),
            OutputSummaries = outputSummaries,
            Source = source,
            SourceHash = sourceHash
        };
    }

    private DebugReadData BuildDebugRead(IGH_DocumentObject target)
    {
        var summary = BuildNodeSummary(target);
        var outputSummaries = SummarizeOutputs(target);
        var debugValues = outputSummaries.ToDictionary(
            x => x.Port,
            x => x.Preview.FirstOrDefault() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        string? sourceHash = null;
        if (_nodeMetadata.TryGetPythonMetadata(summary.NodeId, out var meta))
        {
            sourceHash = meta.SourceHash;
        }
        else if (IsPythonNode(target))
        {
            var source = TryGetPythonSource(target);
            if (!string.IsNullOrWhiteSpace(source))
            {
                sourceHash = Sha256Hasher.ShortHash(source);
            }
        }

        return new DebugReadData
        {
            NodeId = summary.NodeId,
            Success = true,
            RuntimeMessages = CollectRuntimeMessages(target),
            OutputSummaries = outputSummaries,
            DebugValues = debugValues,
            SourceHash = sourceHash,
            SolveMs = _lastSolveMs
        };
    }

    private VariableParamsModel GetVariableParams(IGH_DocumentObject obj)
    {
        var inputs = GetInputParams(obj).Select(p => SafePortName(p, "in")).ToList();
        var outputs = GetOutputParams(obj).Select(p => SafePortName(p, "out")).ToList();

        return new VariableParamsModel
        {
            SupportsVariableParams = obj is IGH_VariableParameterComponent,
            InputCount = inputs.Count,
            OutputCount = outputs.Count,
            InputNames = inputs,
            OutputNames = outputs
        };
    }

    private Dictionary<string, object?> ReadValueState(string nodeId, IGH_DocumentObject obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["nodeId"] = nodeId
        };

        if (obj is Grasshopper.Kernel.Special.GH_NumberSlider slider)
        {
            result["kind"] = "slider";
            result["value"] = TryGetPropertyValue(slider, "CurrentValue")
                              ?? TryGetPropertyValue(slider, "Value")
                              ?? TryGetPropertyValue(TryGetPropertyValue(slider, "Slider"), "Value");
            result["minimum"] = TryGetPropertyValue(TryGetPropertyValue(slider, "Slider"), "Minimum");
            result["maximum"] = TryGetPropertyValue(TryGetPropertyValue(slider, "Slider"), "Maximum");
        }
        else if (obj is Grasshopper.Kernel.Special.GH_BooleanToggle toggle)
        {
            result["kind"] = "toggle";
            result["value"] = TryGetPropertyValue(toggle, "Value");
        }
        else if (obj is Grasshopper.Kernel.Special.GH_Panel panel)
        {
            result["kind"] = "panel";
            result["text"] = TryGetPropertyValue(panel, "UserText") ?? TryGetPropertyValue(panel, "Text");
        }
        else if (obj is Grasshopper.Kernel.Special.GH_Scribble note)
        {
            result["kind"] = "note";
            result["text"] = TryGetPropertyValue(note, "Text");
        }

        if (_nodeMetadata.TryGetPythonMetadata(nodeId, out var meta))
        {
            result["filePath"] = meta.FilePath;
            result["sourceHash"] = meta.SourceHash;
        }

        return result;
    }

    private List<RuntimeMessageModel> CollectRuntimeMessages(IGH_DocumentObject? obj)
    {
        if (obj is not IGH_ActiveObject active)
        {
            return new List<RuntimeMessageModel>();
        }

        var result = new List<RuntimeMessageModel>();
        AddMessages(active, GH_RuntimeMessageLevel.Remark, "remark", result);
        AddMessages(active, GH_RuntimeMessageLevel.Warning, "warning", result);
        AddMessages(active, GH_RuntimeMessageLevel.Error, "error", result);
        return result;
    }

    private List<RuntimeMessageModel> CollectRuntimeMessages(IEnumerable<IGH_DocumentObject> objects)
    {
        var result = new List<RuntimeMessageModel>();
        foreach (var obj in objects)
        {
            result.AddRange(CollectRuntimeMessages(obj));
        }

        return result;
    }

    private static void AddMessages(
        IGH_ActiveObject active,
        GH_RuntimeMessageLevel level,
        string levelName,
        List<RuntimeMessageModel> target)
    {
        foreach (var message in active.RuntimeMessages(level))
        {
            target.Add(new RuntimeMessageModel
            {
                Level = levelName,
                Message = message
            });
        }
    }

    private List<OutputSummaryModel> SummarizeOutputs(IGH_DocumentObject obj)
    {
        var outputs = GetOutputParams(obj).ToList();
        var summaries = new List<OutputSummaryModel>(outputs.Count);

        foreach (var output in outputs)
        {
            var summary = new OutputSummaryModel
            {
                Port = SafePortName(output, "out"),
                ItemCount = output.VolatileDataCount,
                BranchCount = GetBranchCount(output),
                Preview = GetOutputPreview(output, 3)
            };

            summaries.Add(summary);
        }

        return summaries;
    }

    private static int GetBranchCount(IGH_Param param)
    {
        var volatileData = TryGetPropertyValue(param, "VolatileData");
        if (volatileData is null)
        {
            return 0;
        }

        if (TryGetPropertyValue(volatileData, "PathCount") is int pathCount)
        {
            return pathCount;
        }

        return 0;
    }

    private static List<string> GetOutputPreview(IGH_Param param, int maxItems)
    {
        var preview = new List<string>();
        var volatileData = TryGetPropertyValue(param, "VolatileData");
        if (volatileData is null)
        {
            return preview;
        }

        var allData = volatileData
            .GetType()
            .GetMethod("AllData", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null)?
            .Invoke(volatileData, new object[] { true }) as IEnumerable;

        if (allData is null)
        {
            return preview;
        }

        foreach (var item in allData)
        {
            if (item is null)
            {
                continue;
            }

            preview.Add(item.ToString() ?? string.Empty);
            if (preview.Count >= maxItems)
            {
                break;
            }
        }

        return preview;
    }

    private static IEnumerable<IGH_Param> GetInputParams(IGH_DocumentObject obj)
    {
        if (obj is IGH_Component component)
        {
            return component.Params.Input;
        }

        if (obj is IGH_Param param)
        {
            return new[] { param };
        }

        return Array.Empty<IGH_Param>();
    }

    private static IEnumerable<IGH_Param> GetOutputParams(IGH_DocumentObject obj)
    {
        if (obj is IGH_Component component)
        {
            return component.Params.Output;
        }

        if (obj is IGH_Param param)
        {
            return new[] { param };
        }

        return Array.Empty<IGH_Param>();
    }

    private static IGH_DocumentObject? TryGetOwnerObject(
        IGH_Param param,
        IReadOnlyDictionary<IGH_Param, IGH_DocumentObject> ownerMap)
    {
        if (ownerMap.TryGetValue(param, out var mappedOwner))
        {
            return mappedOwner;
        }

        if (param.Attributes?.DocObject is IGH_DocumentObject owner)
        {
            return owner;
        }

        return param as IGH_DocumentObject;
    }

    private IGH_DocumentObject? ResolveNode(GH_Document doc, string? nodeId, string? componentId)
    {
        if (!string.IsNullOrWhiteSpace(componentId) && Guid.TryParse(componentId, out var componentGuid))
        {
            if (doc.FindObject(componentGuid, true) is IGH_DocumentObject byComponentId)
            {
                return byComponentId;
            }
        }

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        if (_nodeMetadata.TryResolveGuid(nodeId, out var guid) && doc.FindObject(guid, true) is IGH_DocumentObject known)
        {
            return known;
        }

        foreach (var obj in doc.Objects.OfType<IGH_DocumentObject>())
        {
            var currentNodeId = _nodeMetadata.GetOrCreateNodeId(obj, KindPrefix(DetermineKind(obj)));
            if (string.Equals(currentNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return obj;
            }
        }

        return null;
    }

    private static IGH_Param? ResolvePort(IGH_DocumentObject node, bool isOutput, string selector)
    {
        if (node is IGH_Component component)
        {
            var ports = isOutput ? component.Params.Output : component.Params.Input;
            if (int.TryParse(selector, out var index) && index >= 0 && index < ports.Count)
            {
                return ports[index];
            }

            return ports.FirstOrDefault(p =>
                string.Equals(p.Name, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.NickName, selector, StringComparison.OrdinalIgnoreCase));
        }

        return node as IGH_Param;
    }
}
