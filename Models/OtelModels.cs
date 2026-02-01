namespace OtelDashboardApi.Models;

/// <summary>
/// Represents a log entry from OpenTelemetry.
/// </summary>
public record LogEntry
{
    public long Id { get; init; }
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public required string ServiceName { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object> Attributes { get; init; } = new();
    public ResourceInfo? Resource { get; init; }
}

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>
/// Represents a metric entry from OpenTelemetry.
/// </summary>
public record MetricEntry
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Unit { get; init; }
    public required double Value { get; init; }
    public MetricType Type { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string ServiceName { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
}

public enum MetricType
{
    Counter,
    Gauge,
    Histogram,
    Sum
}

/// <summary>
/// Represents a trace span from OpenTelemetry.
/// </summary>
public record TraceSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Name { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public required double DurationMs { get; init; }
    public int StatusCode { get; init; }
    public string? StatusMessage { get; init; }
    public int Kind { get; init; }
    public required string ServiceName { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
    public List<SpanEvent> Events { get; init; } = new();
    public List<SpanLink> Links { get; init; } = new();
}

public enum SpanStatus
{
    Unset = 0,
    Ok = 1,
    Error = 2
}

public enum SpanKind
{
    Internal = 0,
    Server = 1,
    Client = 2,
    Producer = 3,
    Consumer = 4
}

public record SpanEvent
{
    public required string Name { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
}

public record SpanLink
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
}

public record ResourceInfo
{
    public required string ServiceName { get; init; }
    public string? ServiceVersion { get; init; }
    public string? DeploymentEnvironment { get; init; }
}

/// <summary>
/// WebSocket message wrapper for streaming.
/// </summary>
public record WebSocketMessage<T>
{
    public required string Type { get; init; }
    public required string Channel { get; init; }
    public required T Payload { get; init; }
    public required string Timestamp { get; init; }
}

/// <summary>
/// WebSocket subscription request.
/// </summary>
public record WebSocketSubscription
{
    public required string Type { get; init; }
    public required string Channel { get; init; }
    public SubscriptionFilters? Filters { get; init; }
}

public record SubscriptionFilters
{
    public string? ServiceName { get; init; }
    public string? Level { get; init; }
    public string? TraceId { get; init; }
    public long? MinDuration { get; init; }
}
