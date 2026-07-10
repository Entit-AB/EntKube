namespace EntKube.Web.Services;

/// <summary>
/// Single source of truth for mapping a textual severity/level word (from Loki's
/// <c>detected_level</c> label or an OTLP <c>severityText</c>) to <see cref="LogLevel"/>.
/// Shared by LokiService and the native OTLP parser so the two backends classify the same
/// severity identically. Returns null for an unrecognized/absent value so callers can fall
/// back to their own heuristic.
/// </summary>
public static class LogLevelMap
{
    public static LogLevel? FromText(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "FATAL" or "CRITICAL" or "PANIC" => LogLevel.Fatal,
        "ERROR" or "ERR" => LogLevel.Error,
        "WARN" or "WARNING" => LogLevel.Warn,
        "INFO" or "INFORMATION" => LogLevel.Info,
        "DEBUG" or "TRACE" => LogLevel.Debug,
        _ => null
    };

    /// <summary>
    /// Heuristic severity of a raw log line, used when no structured <c>detected_level</c> /
    /// <c>severityText</c> is available (e.g. live <c>kubectl logs</c> output). Shared so that
    /// live pod logs and ingested logs are coloured/filtered by the same rules.
    /// </summary>
    public static LogLevel FromLine(string line)
    {
        if (line.Contains("FATAL",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)) return LogLevel.Fatal;
        if (line.Contains("ERROR",    StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" ERR ",    StringComparison.OrdinalIgnoreCase)) return LogLevel.Error;
        if (line.Contains("WARN",     StringComparison.OrdinalIgnoreCase)) return LogLevel.Warn;
        if (line.Contains("DEBUG",    StringComparison.OrdinalIgnoreCase)) return LogLevel.Debug;
        if (line.Contains("INFO",     StringComparison.OrdinalIgnoreCase)) return LogLevel.Info;
        return LogLevel.None;
    }
}
