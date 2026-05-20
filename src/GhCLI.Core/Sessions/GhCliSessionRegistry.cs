using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhCLI.Protocol;

namespace GhCLI.Core.Sessions;

public static class GhCliSessionRegistry
{
    public const string LegacyPipeName = "ghcli.v1";

    private const string RegistryMutexName = @"Local\GhCLI.SessionRegistry";
    private static readonly JsonSerializerOptions JsonOptions = new(ProtocolJson.Options)
    {
        WriteIndented = true
    };

    public static string RegistryDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhCLI");

    public static string RegistryPath => Path.Combine(RegistryDirectory, "sessions.json");

    public static string CreateDefaultPipeName(string sessionId, int processId)
    {
        var suffix = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")[..8]
            : sessionId[..Math.Min(8, sessionId.Length)];
        return $"{LegacyPipeName}.{processId}.{suffix}";
    }

    public static SessionRecord CreateCurrentProcessSession(
        string sessionId,
        string pipeName,
        string? rhinoVersion = null)
    {
        using var process = Process.GetCurrentProcess();
        var processName = process.ProcessName;
        var hostKind = DetermineHostKind(processName);
        var now = DateTimeOffset.UtcNow;

        return new SessionRecord
        {
            SessionId = sessionId,
            Alias = BuildAlias(hostKind, process.Id),
            PipeName = pipeName,
            ProcessId = process.Id,
            ProcessName = processName,
            HostKind = hostKind,
            WindowTitle = TryGetMainWindowTitle(process),
            RhinoVersion = rhinoVersion,
            StartedUtc = now,
            LastSeenUtc = now
        };
    }

    public static SessionsData ReadSessions(bool pruneStale = true)
    {
        return WithRegistryLock(() =>
        {
            var doc = ReadDocument();
            if (pruneStale)
            {
                var pruned = PruneStale(doc.Sessions).ToList();
                if (pruned.Count != doc.Sessions.Count)
                {
                    doc.Sessions = pruned;
                    WriteDocument(doc);
                }
            }

            return new SessionsData
            {
                RegistryPath = RegistryPath,
                Sessions = doc.Sessions
                    .OrderBy(x => x.HostKind, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.ProcessId)
                    .ThenBy(x => x.StartedUtc)
                    .ToList()
            };
        });
    }

    public static void Register(SessionRecord session)
    {
        WithRegistryLock(() =>
        {
            Directory.CreateDirectory(RegistryDirectory);
            var doc = ReadDocument();
            doc.Sessions = PruneStale(doc.Sessions)
                .Where(x => !IsSameSessionSlot(x, session))
                .ToList();
            session.LastSeenUtc = DateTimeOffset.UtcNow;
            doc.Sessions.Add(session);
            WriteDocument(doc);
        });
    }

    public static void Touch(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        WithRegistryLock(() =>
        {
            var doc = ReadDocument();
            var changed = false;
            foreach (var session in doc.Sessions)
            {
                if (!string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                session.LastSeenUtc = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (changed)
            {
                WriteDocument(doc);
            }
        });
    }

    public static void Unregister(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        WithRegistryLock(() =>
        {
            var doc = ReadDocument();
            doc.Sessions = doc.Sessions
                .Where(x => !string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WriteDocument(doc);
        });
    }

    public static SessionRecord ResolveSession(string selector, IEnumerable<SessionRecord> sessions)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new ArgumentException("Session selector is required.", nameof(selector));
        }

        var normalized = selector.Trim();
        var matches = sessions
            .Where(x =>
                string.Equals(x.Alias, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.PipeName, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.SessionId, normalized, StringComparison.OrdinalIgnoreCase) ||
                x.SessionId.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                x.ProcessId.ToString() == normalized)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No GhCLI session matches '{selector}'. Run `GhCLI sessions` to list active sessions."),
            _ => throw new InvalidOperationException($"Session selector '{selector}' is ambiguous. Use a full sessionId, alias, process id, or pipe name.")
        };
    }

    private static string DetermineHostKind(string processName)
    {
        if (processName.Contains("revit", StringComparison.OrdinalIgnoreCase))
        {
            return "revit";
        }

        if (processName.Contains("rhino", StringComparison.OrdinalIgnoreCase))
        {
            return "rhino";
        }

        return Sanitize(processName).ToLowerInvariant();
    }

    private static string BuildAlias(string hostKind, int processId)
    {
        return $"{Sanitize(hostKind).ToLowerInvariant()}-{processId}";
    }

    private static string Sanitize(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9._-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "host" : sanitized;
    }

    private static string? TryGetMainWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? null
                : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameSessionSlot(SessionRecord existing, SessionRecord next)
    {
        return string.Equals(existing.SessionId, next.SessionId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(existing.PipeName, next.PipeName, StringComparison.OrdinalIgnoreCase) ||
               existing.ProcessId == next.ProcessId;
    }

    private static IEnumerable<SessionRecord> PruneStale(IEnumerable<SessionRecord> sessions)
    {
        foreach (var session in sessions)
        {
            if (IsProcessAlive(session.ProcessId))
            {
                yield return session;
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static SessionRegistryDocument ReadDocument()
    {
        if (!File.Exists(RegistryPath))
        {
            return new SessionRegistryDocument();
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<SessionRegistryDocument>(json, ProtocolJson.Options)
                   ?? new SessionRegistryDocument();
        }
        catch
        {
            return new SessionRegistryDocument();
        }
    }

    private static void WriteDocument(SessionRegistryDocument doc)
    {
        Directory.CreateDirectory(RegistryDirectory);
        var tempPath = $"{RegistryPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(doc, JsonOptions));

        if (File.Exists(RegistryPath))
        {
            File.Replace(tempPath, RegistryPath, null);
        }
        else
        {
            File.Move(tempPath, RegistryPath);
        }
    }

    private static void WithRegistryLock(Action action)
    {
        WithRegistryLock<object?>(() =>
        {
            action();
            return null;
        });
    }

    private static T WithRegistryLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, RegistryMutexName);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
            if (!acquired)
            {
                throw new IOException("Timed out waiting for GhCLI session registry lock.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private sealed class SessionRegistryDocument
    {
        public List<SessionRecord> Sessions { get; set; } = new();
    }
}
