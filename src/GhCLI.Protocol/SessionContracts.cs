namespace GhCLI.Protocol;

public sealed class SessionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string HostKind { get; set; } = string.Empty;
    public string? WindowTitle { get; set; }
    public string? RhinoVersion { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class SessionsData
{
    public string RegistryPath { get; set; } = string.Empty;
    public List<SessionRecord> Sessions { get; set; } = new();
}
