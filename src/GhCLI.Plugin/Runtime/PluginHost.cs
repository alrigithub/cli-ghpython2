using Rhino;
using GhCLI.Core.Sessions;
using GhCLI.Protocol;
using System.Reflection;

namespace GhCLI.Plugin.Runtime;

internal static class PluginHost
{
    private static readonly object Gate = new();
    private static bool _started;
    private static NamedPipeJsonServer? _server;
    private static CommandRouter? _router;
    private static GrasshopperRuntime? _runtime;
    private static SessionRecord? _session;

    public static string SessionId { get; } = Guid.NewGuid().ToString("N");
    public static string PipeName =>
        Environment.GetEnvironmentVariable("GHCLI_PIPE_NAME")?.Trim() is { Length: > 0 } envName
            ? envName
            : GhCliSessionRegistry.CreateDefaultPipeName(SessionId, Environment.ProcessId);

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _session = GhCliSessionRegistry.CreateCurrentProcessSession(
                SessionId,
                PipeName,
                RhinoApp.Version.ToString());
            GhCliSessionRegistry.Register(_session);

            _runtime = new GrasshopperRuntime(_session);
            _router = new CommandRouter(_runtime);
            _server = new NamedPipeJsonServer(PipeName, _router.HandleAsync);
            _server.Start();
            _started = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            AppDomain.CurrentDomain.DomainUnload += (_, _) => Stop();
            WriteStartupBanner();
            RhinoApp.WriteLine($"[GhCLI] pipe server started on '{PipeName}'.");
        }
    }

    private static void WriteStartupBanner()
    {
        var version = typeof(PluginHost).Assembly
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? typeof(PluginHost).Assembly.GetName().Version?.ToString()
                      ?? "dev";
        var session = _session is null
            ? SessionId
            : $"{_session.Alias} | {_session.PipeName}";

        string[] lines =
        {
            @"+--------------------------------+",
            @"| ghcli                          |",
            @"| python nodes. fast loop.       |",
            @"+--------------------------------+",
            $@" version  {version}",
            $@" updated  {DateTime.Now:dd.MM.yyyy}",
            $@" session  {session}",
            @"+--------------------------------+"
        };

        foreach (var line in lines)
        {
            RhinoApp.WriteLine(line);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                _server?.Dispose();
            }
            catch
            {
                // no-op
            }

            _server = null;
            _router = null;
            _runtime = null;
            GhCliSessionRegistry.Unregister(SessionId);
            _session = null;
            _started = false;
        }
    }
}
