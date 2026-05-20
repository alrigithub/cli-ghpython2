namespace GhCLI.Protocol;

public sealed class StatusData
{
    public bool PluginLoaded { get; set; }
    public bool PipeConnected { get; set; }
    public SessionRecord? Session { get; set; }
    public string? RhinoVersion { get; set; }
    public bool GrasshopperLoaded { get; set; }
    public ActiveDocumentModel? ActiveDocument { get; set; }
    public List<string> Capabilities { get; set; } = new();
}
