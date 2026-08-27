namespace EntKube.Web.Services;

// Shared by the engine's RUM series output and by every Prometheus-backed chart. In the
// EntKube.Web.Services namespace for the same reason as OperationResult.cs — see the note there.

/// <summary>
/// A single data point in a time series — a timestamp/value pair.
/// </summary>
public class TimeSeriesDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}
