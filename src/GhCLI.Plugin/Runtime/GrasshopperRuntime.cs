using System.Diagnostics;
using GhCLI.Core.Errors;
using GhCLI.Core.Files;
using GhCLI.Core.Sessions;
using GhCLI.Protocol;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;

namespace GhCLI.Plugin.Runtime;

internal sealed partial class GrasshopperRuntime
{
    private readonly object _gate = new();
    private readonly string _sessionId;
    private readonly SessionRecord _session;
    private readonly NodeMetadataStore _nodeMetadata = new();
    private readonly WorkspaceFileResolver _fileResolver = new();
    private readonly Dictionary<string, Dictionary<Guid, bool>> _previewStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<Guid, bool>> _layerVisibilityStates = new(StringComparer.OrdinalIgnoreCase);
    private double _lastSolveMs;

    public GrasshopperRuntime(SessionRecord session)
    {
        _session = session;
        _sessionId = session.SessionId;
    }

    public StatusData GetStatus()
    {
        lock (_gate)
        {
            GhCliSessionRegistry.Touch(_sessionId);
            var doc = TryGetActiveDocument();
            return new StatusData
            {
                PluginLoaded = true,
                PipeConnected = true,
                Session = _session,
                RhinoVersion = RhinoApp.Version.ToString(),
                GrasshopperLoaded = Instances.ActiveCanvas is not null || doc is not null,
                ActiveDocument = doc is null
                    ? null
                    : new ActiveDocumentModel
                    {
                        Id = TryGetPropertyValue(doc, "DocumentID")?.ToString(),
                        Name = doc.DisplayName,
                        FilePath = TryGetPropertyValue(doc, "FilePath")?.ToString()
                    },
                Capabilities = new List<string>
                {
                    CommandNames.Status,
                    CommandNames.CanvasSummary,
                    CommandNames.NodeRead,
                    CommandNames.TxnApply,
                    CommandNames.GraphApply,
                    CommandNames.SolveRun,
                    CommandNames.DebugRead,
                    TransactionOps.UpsertPythonNode,
                    TransactionOps.UpsertComponent,
                    TransactionOps.UpsertSlider,
                    TransactionOps.UpsertToggle,
                    TransactionOps.UpsertPanel,
                    TransactionOps.UpsertNote,
                    TransactionOps.SetWires,
                    TransactionOps.MoveNode,
                    TransactionOps.SetValue,
                    TransactionOps.SetPreview,
                    TransactionOps.SetLayerVisibility
                }
            };
        }
    }

    public CanvasSummaryData GetCanvasSummary(CanvasSummaryRequest request)
    {
        lock (_gate)
        {
            var doc = RequireActiveDocument();
            return BuildCanvasSummary(doc, request.Scope);
        }
    }

    public NodeReadData NodeRead(NodeReadRequest request)
    {
        lock (_gate)
        {
            var doc = RequireActiveDocument();
            return BuildNodeRead(doc, request);
        }
    }

    public SolveRunData SolveRun(SolveRunRequest request)
    {
        lock (_gate)
        {
            var doc = RequireActiveDocument();
            PrepareDocumentForSolution(doc);
            SeedSpecialSourceParams(doc);
            if (!string.IsNullOrWhiteSpace(request.NodeId))
            {
                var target = ResolveNode(doc, request.NodeId, null);
                if (target is IGH_ActiveObject active)
                {
                    active.ExpireSolution(true);
                }
            }
            else
            {
                ExpireComputedObjects(doc);
            }

            var sw = Stopwatch.StartNew();
            doc.NewSolution(false);
            sw.Stop();
            _lastSolveMs = sw.Elapsed.TotalMilliseconds;

            var runtimeMessages = string.IsNullOrWhiteSpace(request.NodeId)
                ? CollectRuntimeMessages(doc.Objects.OfType<IGH_DocumentObject>())
                : CollectRuntimeMessages(ResolveNode(doc, request.NodeId, null)).ToList();

            return new SolveRunData
            {
                Success = true,
                SolveMs = _lastSolveMs,
                RuntimeMessages = runtimeMessages.Take(100).ToList()
            };
        }
    }

    public DebugReadData DebugRead(DebugReadRequest request)
    {
        lock (_gate)
        {
            var doc = RequireActiveDocument();
            var target = ResolveNode(doc, request.NodeId, null);
            if (target is null)
            {
                throw new CommandValidationException("debug.read requires a valid node_id.");
            }

            return BuildDebugRead(target);
        }
    }

    private GH_Document RequireActiveDocument()
    {
        return TryGetActiveDocument()
               ?? TryCreateActiveDocument()
               ?? throw new PluginUnavailableException("No active Grasshopper document is available.");
    }

    private static GH_Document? TryGetActiveDocument()
    {
        if (Instances.ActiveCanvas?.Document is GH_Document active)
        {
            PrepareDocumentForSolution(active);
            return active;
        }

        var server = Instances.DocumentServer;
        if (server is not null)
        {
            foreach (var doc in server)
            {
                if (doc is GH_Document typed)
                {
                    PrepareDocumentForSolution(typed);
                    return typed;
                }
            }
        }

        return null;
    }

    private static GH_Document? TryCreateActiveDocument()
    {
        var server = Instances.DocumentServer;
        if (server is null)
        {
            return null;
        }

        var doc = new GH_Document();
        try
        {
            server.AddDocument(doc);
            PrepareDocumentForSolution(doc);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private static void PrepareDocumentForSolution(GH_Document doc)
    {
        _ = TrySetPropertyValue(doc, "Enabled", true);
        _ = TrySetPropertyValue(doc, "EnableSolutions", true);
        _ = TrySetPropertyValue(doc, "SolutionMode", "Silent");

        var canvas = Instances.ActiveCanvas;
        if (canvas is not null)
        {
            _ = TrySetPropertyValue(canvas, "Document", doc);
            _ = TryInvokeNoArg(canvas, "Refresh");
        }
    }

    private static void ExpireComputedObjects(GH_Document doc)
    {
        foreach (var obj in doc.Objects.OfType<IGH_DocumentObject>())
        {
            if (IsSeededSpecialSource(obj))
            {
                continue;
            }

            if (obj is IGH_ActiveObject active)
            {
                active.ExpireSolution(false);
            }
        }
    }

    private static bool IsSeededSpecialSource(IGH_DocumentObject obj)
    {
        return (obj is GH_NumberSlider or GH_BooleanToggle or GH_Panel || IsColourSwatchObject(obj) || IsButtonObject(obj))
               && obj is IGH_Param { SourceCount: 0 };
    }

    private static void SeedSpecialSourceParams(GH_Document doc)
    {
        foreach (var obj in doc.Objects.OfType<IGH_DocumentObject>())
        {
            if (obj is IGH_Param { SourceCount: > 0 })
            {
                continue;
            }

            if (obj is GH_NumberSlider slider)
            {
                var value = TryGetPropertyValue(slider, "CurrentValue")
                            ?? TryGetPropertyValue(slider, "Value")
                            ?? TryGetPropertyValue(TryGetPropertyValue(slider, "Slider"), "Value");
                if (TryConvertDouble(value, out var number))
                {
                    SeedParam(slider, new GH_Number(number));
                }
            }
            else if (obj is GH_BooleanToggle toggle)
            {
                var value = TryGetPropertyValue(toggle, "Value");
                if (TryConvertBool(value, out var boolean))
                {
                    SeedParam(toggle, new GH_Boolean(boolean));
                }
            }
            else if (obj is GH_Panel panel)
            {
                var text = (TryGetPropertyValue(panel, "UserText") ?? TryGetPropertyValue(panel, "Text"))?.ToString() ?? string.Empty;
                SeedParam(panel, new GH_String(text));
            }
            else if (obj is IGH_Param colourParam &&
                     IsColourSwatchObject(obj) &&
                     TryGetColourValue(obj, out var colour))
            {
                SeedParam(colourParam, new GH_Colour(colour));
            }
            else if (obj is IGH_Param buttonParam && IsButtonObject(obj))
            {
                SeedParam(buttonParam, new GH_Boolean(false));
            }
        }
    }

    private static bool IsColourSwatchObject(IGH_DocumentObject obj)
    {
        var typeName = obj.GetType().Name;
        var fullName = obj.GetType().FullName ?? string.Empty;
        return typeName.Contains("ColourSwatch", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("ColorSwatch", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("ColourSwatch", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("ColorSwatch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsButtonObject(IGH_DocumentObject obj)
    {
        var typeName = obj.GetType().Name;
        var fullName = obj.GetType().FullName ?? string.Empty;
        return typeName.Contains("Button", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetColourValue(object target, out System.Drawing.Color colour)
    {
        foreach (var property in new[] { "SwatchColour", "SwatchColor", "Colour", "Color", "Value" })
        {
            if (TryGetPropertyValue(target, property) is System.Drawing.Color propertyColour)
            {
                colour = propertyColour;
                return true;
            }
        }

        colour = System.Drawing.Color.Empty;
        return false;
    }

    private static void SeedParam(IGH_Param param, object value)
    {
        try
        {
            param.ClearData();
            param.AddVolatileData(new GH_Path(0), 0, value);
        }
        catch
        {
            // Best effort. Normal Grasshopper solving still handles these params when the document is fully active.
        }
    }

    private static bool TryConvertDouble(object? value, out double number)
    {
        switch (value)
        {
            case double typed:
                number = typed;
                return true;
            case decimal typed:
                number = (double)typed;
                return true;
            case int typed:
                number = typed;
                return true;
            case float typed:
                number = typed;
                return true;
            case string text when double.TryParse(text, out var parsed):
                number = parsed;
                return true;
            default:
                try
                {
                    number = value is null ? 0d : Convert.ToDouble(value);
                    return value is not null;
                }
                catch
                {
                    number = 0d;
                    return false;
                }
        }
    }

    private static bool TryConvertBool(object? value, out bool boolean)
    {
        switch (value)
        {
            case bool typed:
                boolean = typed;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                boolean = parsed;
                return true;
            default:
                try
                {
                    boolean = value is not null && Convert.ToBoolean(value);
                    return value is not null;
                }
                catch
                {
                    boolean = false;
                    return false;
                }
        }
    }
}
