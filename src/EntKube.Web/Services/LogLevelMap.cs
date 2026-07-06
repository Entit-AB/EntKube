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
}
