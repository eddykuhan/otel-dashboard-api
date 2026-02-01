# OTEL Dashboard API - Copilot Instructions

## Project Overview

The OTEL Dashboard API is a high-performance OpenTelemetry collector and visualization backend. It receives telemetry data (traces, metrics, logs) from applications and collectors via both HTTP (REST/JSON) and gRPC, stores the data in-memory with configurable limits, and provides real-time querying and WebSocket streaming capabilities.

### Key Objectives
- **Full OTLP Compatibility**: Receive telemetry via standard OpenTelemetry Protocol (OTLP) formats
- **Dual Protocol Support**: HTTP/JSON and gRPC endpoints for maximum flexibility
- **Real-time Streaming**: WebSocket-based updates for live telemetry monitoring
- **High-throughput Storage**: Concurrent, thread-safe in-memory storage with secondary indexes
- **Dashboard-ready API**: REST endpoints optimized for frontend visualization

## Architecture

### Technology Stack
- **.NET 10**: Modern, high-performance runtime
- **ASP.NET Core**: REST API framework with Swagger/OpenAPI support
- **gRPC**: Protocol Buffers for efficient binary serialization
- **System.Threading**: Concurrent collections for thread-safe data storage
- **System.IO.Pipelines**: Efficient I/O for WebSocket streaming

### Port Configuration
- **5003**: HTTP/1.1 and HTTP/2 (REST API + alternative gRPC)
- **4317**: HTTP/2 only (standard OTLP gRPC port per OpenTelemetry spec)

### Core Components

#### 1. Controllers (REST API Endpoints)
```
Controllers/
├── HealthController.cs      # /api/v1/health - System health & stats
├── TracesController.cs      # /api/v1/traces - Query traces
├── MetricsController.cs     # /api/v1/metrics - Query metrics
├── LogsController.cs        # /api/v1/logs - Query logs
└── SseStreamController.cs   # /ws/stream - WebSocket streaming (SSE)
```

#### 2. OTLP Receivers (HTTP Endpoints)
- `POST /v1/traces` - Receive traces (JSON-encoded OTLP)
- `POST /v1/metrics` - Receive metrics (JSON-encoded OTLP)
- `POST /v1/logs` - Receive logs (JSON-encoded OTLP)

#### 3. gRPC Services
- **OtlpTraceGrpcService**: Implements `TraceService.TraceServiceBase` from proto
- **OtlpMetricsGrpcService**: Implements `MetricsService.MetricsServiceBase` from proto
- **OtlpLogsGrpcService**: Implements `LogsService.LogsServiceBase` from proto

All services:
- Validate incoming OTLP requests
- Parse resource/scope attributes
- Store telemetry in `InMemoryStore`
- Broadcast updates via `WebSocketStreamService`
- Return standardized `PartialSuccess` responses

#### 4. Data Storage (InMemoryStore)
Thread-safe concurrent storage with configurable capacity limits:

```csharp
private readonly ConcurrentQueue<OtelLog> _logs;                    // Max: 100,000
private readonly ConcurrentDictionary<string, MetricEntry> _metrics; // Max: 100,000
private readonly ConcurrentDictionary<string, List<TraceSpan>> _tracesByTraceId; // Max: 50,000
```

**Storage Limits & Eviction**:
- Exceeding limits triggers FIFO eviction
- Logs: Oldest log entries removed first
- Traces: Oldest traces (by root span start time) removed first
- Metrics: Oldest metrics removed first

#### 5. WebSocket Streaming (WebSocketStreamService)
- Manages active WebSocket connections
- Routes telemetry updates to subscribed clients
- Uses bounded channels with backpressure handling
- Supports topic-based subscriptions: "traces", "metrics", "logs"

#### 6. OTLP Helpers (OtlpHelpers.cs)
Utility functions for OTLP data conversion:
- `BytesToHex()` - Convert byte arrays to hex strings (for trace/span IDs)
- `NanosToDateTime()` - Convert nanosecond timestamps to DateTime
- `GetServiceName()` - Extract service.name from resource attributes
- `MapSpanKind()` - Convert proto SpanKind enum to domain model
- `MapSpanStatus()` - Convert proto StatusCode to domain model
- `ToStringAttributesDict()` - Convert OTLP key-value pairs to dictionary

## Key Patterns and Conventions

### Trace ID & Span ID Format
```csharp
// All IDs are stored and transmitted as HEX strings (standard OTLP format)
// NOT Base64 - this ensures query IDs match storage IDs
var traceIdHex = "abcdef0123456789abcdef0123456789"; // 32 hex chars = 16 bytes
var spanIdHex = "1234567890abcdef";                   // 16 hex chars = 8 bytes
```

### Timestamp Handling
```csharp
// OTLP allows nanosecond timestamps as EITHER number OR string
// The GetUInt64FromJsonElement() helper handles both formats automatically
private static ulong GetUInt64FromJsonElement(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Number)
        return element.GetUInt64();
    else if (element.ValueKind == JsonValueKind.String)
        return ulong.TryParse(element.GetString(), out var result) ? result : 0;
    return 0;
}
```

### gRPC Service Pattern
```csharp
public override async Task<ExportTraceServiceResponse> Export(
    ExportTraceServiceRequest request,
    ServerCallContext context)
{
    int accepted = 0, rejected = 0;
    
    foreach (var resourceSpans in request.ResourceSpans)
    {
        var serviceName = OtlpHelpers.GetServiceName(resourceSpans.Resource);
        foreach (var scopeSpans in resourceSpans.ScopeSpans)
        {
            foreach (var span in scopeSpans.Spans)
            {
                try
                {
                    var traceSpan = ConvertSpan(span, serviceName, ...);
                    _store.AddSpan(traceSpan);
                    await _wsService.BroadcastAsync("traces", traceSpan, context.CancellationToken);
                    accepted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process span");
                    rejected++;
                }
            }
        }
    }
    
    return new ExportTraceServiceResponse
    {
        PartialSuccess = rejected > 0 ? new PartialSuccess 
        { 
            RejectedSpans = rejected,
            ErrorMessage = "..." 
        } : null
    };
}
```

### REST Query Pattern
```csharp
[HttpGet]
public IActionResult GetTraces(
    [FromQuery] string? serviceName = null,
    [FromQuery] long? minDurationMs = null,
    [FromQuery] DateTime? startTime = null,
    [FromQuery] DateTime? endTime = null,
    [FromQuery] int limit = 50)
{
    var results = _store.QueryTraces(serviceName, minDurationMs, startTime, endTime, limit);
    return Ok(results);
}
```

### Attribute Handling
```csharp
// OTLP attributes can be various types; we convert to string dictionary
private static Dictionary<string, string> ToStringAttributesDict(IEnumerable<KeyValue> attributes)
{
    var dict = new Dictionary<string, string>();
    foreach (var attr in attributes)
    {
        dict[attr.Key] = attr.Value.StringValue ?? attr.Value.IntValue.ToString() ?? "";
    }
    return dict;
}
```

## Data Models

### Domain Models (Models/OtelModels.cs)
```csharp
public class TraceSpan
{
    public string TraceId { get; set; }        // Hex format
    public string SpanId { get; set; }         // Hex format
    public string? ParentSpanId { get; set; }  // Null for root spans
    public string Name { get; set; }
    public int Kind { get; set; }              // 0=Unspecified, 1=Internal, 2=Server, 3=Client, 4=Producer, 5=Consumer
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double DurationMs { get; set; }
    public int StatusCode { get; set; }        // 0=Unset, 1=Ok, 2=Error
    public string? StatusMessage { get; set; }
    public string ServiceName { get; set; }
    public Dictionary<string, string> Attributes { get; set; }
    public List<SpanEvent> Events { get; set; }
    public List<SpanLink> Links { get; set; }
}

public class OtelLog
{
    public string Id { get; set; }             // UUID
    public string TraceId { get; set; }        // Hex format
    public string SpanId { get; set; }         // Hex format
    public string ServiceName { get; set; }
    public DateTime Timestamp { get; set; }
    public int SeverityNumber { get; set; }    // 1-24 from OpenTelemetry spec
    public string SeverityText { get; set; }   // "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"
    public string Body { get; set; }           // Log message body
    public Dictionary<string, string> Attributes { get; set; }
}

public class MetricEntry
{
    public int Id { get; set; }
    public string Name { get; set; }           // Metric name
    public string Description { get; set; }
    public string Unit { get; set; }           // e.g., "ms", "1", "By"
    public double Value { get; set; }          // Latest value
    public string Type { get; set; }           // "Gauge", "Counter", "Histogram", "Summary"
    public DateTime Timestamp { get; set; }    // When metric was received
    public string ServiceName { get; set; }
    public Dictionary<string, string> Attributes { get; set; }
}
```

### Enums (Models/OtelModels.cs)
```csharp
public enum SpanKind
{
    Unspecified = 0,
    Internal = 1,
    Server = 2,
    Client = 3,
    Producer = 4,
    Consumer = 5
}

public enum SpanStatus
{
    Unset = 0,
    Ok = 1,
    Error = 2
}

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}
```

## OTLP Protocol Details

### OpenTelemetry Protocol Specification
The project implements the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otel/protocol/) specification.

### Message Hierarchy
```
ExportTraceServiceRequest
└── ResourceSpans[]
    ├── Resource (service.name, service.version, etc.)
    └── ScopeSpans[]
        ├── Scope (instrumentation library info)
        └── Spans[]
            ├── TraceId, SpanId, ParentSpanId (hex)
            ├── Name
            ├── Kind (SpanKind enum)
            ├── StartTimeUnixNano, EndTimeUnixNano
            ├── Status (code, message)
            ├── Attributes[]
            ├── Events[] (with timestamps and attributes)
            └── Links[] (reference to other spans/traces)
```

Similar hierarchy for metrics and logs.

## Development Workflows

### Setup
```bash
cd /Users/kuhan/Projects/agentic-ai-poc/k8s-agent/otel-dashboard-api
dotnet restore
dotnet build
```

### Running
```bash
# Run on default ports (5003 HTTP, 4317 gRPC)
dotnet run

# Or in release mode
dotnet run --configuration Release
```

### Testing Endpoints

#### HTTP Traces
```bash
# POST a trace
curl -X POST http://localhost:5003/v1/traces \
  -H "Content-Type: application/json" \
  -d '{
    "resourceSpans": [{
      "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "my-service"}}]},
      "scopeSpans": [{
        "scope": {"name": "my-scope"},
        "spans": [{
          "traceId": "abcdef0123456789abcdef0123456789",
          "spanId": "1234567890abcdef",
          "name": "test-span",
          "kind": 2,
          "startTimeUnixNano": "1706745600000000000",
          "endTimeUnixNano": "1706745600100000000",
          "status": {"code": 1}
        }]
      }]
    }]
  }'

# Query traces
curl http://localhost:5003/api/v1/traces?limit=10
curl http://localhost:5003/api/v1/traces/abcdef0123456789abcdef0123456789
```

#### HTTP Metrics
```bash
curl -X POST http://localhost:5003/v1/metrics \
  -H "Content-Type: application/json" \
  -d '{...}'
curl http://localhost:5003/api/v1/metrics?limit=10
```

#### HTTP Logs
```bash
curl -X POST http://localhost:5003/v1/logs \
  -H "Content-Type: application/json" \
  -d '{...}'
curl http://localhost:5003/api/v1/logs?limit=50
```

#### Health Check
```bash
curl http://localhost:5003/api/v1/health
```

### Proto Regeneration
When modifying `Protos/opentelemetry.proto`:
```bash
# The build process automatically regenerates C# files from .proto
# Generated files appear in obj/Debug/net10.0/Protos/
dotnet build
```

## Common Tasks

### Adding a New Query Filter
1. Add parameter to `QueryTraces/QueryMetrics/QueryLogs` in `InMemoryStore.cs`
2. Implement filter logic in the query method
3. Add `[FromQuery]` parameter to controller endpoint
4. Update Swagger documentation with `[Produces(...)]` attributes

### Extending OTLP Support
1. Update `Protos/opentelemetry.proto` with new message types
2. Regenerate proto files via `dotnet build`
3. Create new gRPC service or extend existing one
4. Register in `Program.cs` with `services.AddGrpc()`

### Adding WebSocket Subscriptions
1. Define new topic in `WebSocketStreamService`
2. Call `await _wsService.BroadcastAsync(topic, data, cancellationToken)` when data arrives
3. Update frontend to subscribe to topic

### Performance Tuning
- Adjust storage limits in `InMemoryStore` constructor (maxLogs, maxMetrics, maxTraces)
- Implement data compression for large attribute dictionaries
- Consider read-only replicas for high-throughput scenarios

## Storage Capacity Management

### Current Limits
- Logs: 100,000 entries
- Metrics: 100,000 entries
- Traces: 50,000 traces (unlimited spans per trace)

### Eviction Strategies
```csharp
// Logs: Removes oldest entries when limit exceeded
while (_logs.Count > _maxLogs)
{
    _logs.TryDequeue(out _);
}

// Traces: Removes oldest traces by root span start time
while (_tracesByTraceId.Count > _maxTraces)
{
    var oldest = _tracesByTraceId
        .MinBy(kvp => kvp.Value.Min(s => s.StartTime));
    _tracesByTraceId.TryRemove(oldest.Key, out _);
}
```

## Testing Approach

- **Manual API Testing**: Use curl or Postman for endpoint validation
- **Load Testing**: Send high-volume telemetry to test concurrent handling
- **Error Handling**: Test invalid OTLP payloads, missing required fields
- **Storage Limits**: Verify eviction works correctly when limits exceeded

## Error Handling

### Partial Success Pattern
```csharp
// If some spans fail, return partial success (not full failure)
return new ExportTraceServiceResponse
{
    PartialSuccess = new PartialSuccess
    {
        RejectedSpans = rejectedCount,
        ErrorMessage = "Invalid format in spans: [details]"
    }
};
```

### JSON Parsing Robustness
```csharp
// Always use GetPropertyOrDefault() to avoid exceptions
var value = element.GetPropertyOrDefault("key", defaultElement);

// Always wrap parsing in try-catch
try
{
    // Parsing logic
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Parse error");
    // Continue with next item (partial success)
}
```

## Areas Requiring Caution

1. **Thread Safety**: InMemoryStore uses concurrent collections; maintain lock discipline when iterating
2. **ID Format Consistency**: Always use HEX format for trace/span IDs (not Base64)
3. **Timestamp Precision**: Nanosecond precision can overflow; handle carefully with millisecond conversion
4. **Memory Leaks**: WebSocket connections must be properly closed; bounded channels prevent unbounded growth
5. **Large Payloads**: Attribute dictionaries can grow large; consider compression for very large telemetry
6. **Concurrent Modifications**: When iterating dictionaries, use snapshots or locks to prevent exceptions

## Proto File Structure

The proto files are split to match OpenTelemetry SDK expectations:

```
Protos/
├── common.proto   # Shared types (Resource, KeyValue, AnyValue, InstrumentationScope)
│                  # Package: opentelemetry.proto.common.v1
│                  # C# Namespace: OpenTelemetry.Proto.Common
│
├── trace.proto    # TraceService, Span, SpanEvent, SpanLink types
│                  # Package: opentelemetry.proto.collector.trace.v1
│                  # C# Namespace: OpenTelemetry.Proto.Collector.Trace
│
├── metrics.proto  # MetricsService, Metric, Gauge, Sum, Histogram types
│                  # Package: opentelemetry.proto.collector.metrics.v1
│                  # C# Namespace: OpenTelemetry.Proto.Collector.Metrics
│
└── logs.proto     # LogsService, LogRecord, SeverityNumber types
                   # Package: opentelemetry.proto.collector.logs.v1
                   # C# Namespace: OpenTelemetry.Proto.Collector.Logs
```

**IMPORTANT**: The package names MUST match the standard OpenTelemetry OTLP specification:
- Traces: `opentelemetry.proto.collector.trace.v1.TraceService/Export`
- Metrics: `opentelemetry.proto.collector.metrics.v1.MetricsService/Export`
- Logs: `opentelemetry.proto.collector.logs.v1.LogsService/Export`

Using a single package name for all services will cause the OpenTelemetry SDK to fail connecting for metrics and logs (traces might work by coincidence if the package name matches).

## Integration with Frontend

The Angular frontend (`/otel-dashboard-frontend`) connects via:
- **REST API**: `http://localhost:5003/api/v1/*` for querying telemetry
- **WebSocket**: `ws://localhost:5003/ws/stream` for real-time updates
- **Swagger/OpenAPI**: `http://localhost:5003/swagger` for API documentation

Frontend environment variables configured in `environment.ts`:
```typescript
export const environment = {
  apiUrl: 'http://localhost:5003',
  wsUrl: 'ws://localhost:5003'
};
```

## Deployment Considerations

- **Memory**: In-memory storage requires RAM for 100K logs + 100K metrics + 50K traces
- **CPU**: Heavy concurrent requests benefit from multi-core systems
- **Networking**: Both HTTP/2 and gRPC ports should be accessible from telemetry sources
- **Scalability**: Current architecture is single-instance; use multiple replicas + load balancer for HA
- **Persistence**: Data is lost on restart; consider adding persistent storage layer if needed

## Related Documentation

- [OpenTelemetry Specification](https://opentelemetry.io/docs/specs/)
- [OTLP Protocol](https://opentelemetry.io/docs/specs/otel/protocol/)
- [ASP.NET Core gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/)
- [.NET Concurrency](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)
