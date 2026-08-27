namespace EntKube.Web.Services;

// The shape every log backend returns. Named after Loki because Loki was the first backend; the native
// segment engine adopted the same shape deliberately, so the log viewers stay backend-agnostic and a
// cluster can be switched between backends without touching the UI. Kept in the EntKube.Web.Services
// namespace for the same reason as OperationResult.cs — see the note there.

public class LokiLogStream
{
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<LokiLogEntry> Entries { get; set; } = [];
}

public class LokiLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Line { get; set; } = "";
    public LogLevel DetectedLevel { get; set; } = LogLevel.None;

    /// <summary>Trace id this line belongs to, if the app propagated trace context into its logs.</summary>
    public string? TraceId { get; set; }
}

public enum LogLevel { None, Debug, Info, Warn, Error, Fatal }
