# OTEL Dashboard API

A high-performance OpenTelemetry collector and visualization backend built with .NET 10. Receives telemetry data (traces, metrics, logs) via both HTTP/JSON and gRPC protocols, stores data in-memory with configurable limits, and provides real-time querying and WebSocket streaming capabilities.

## Features

- ✅ **Full OTLP Compatibility**: Receive telemetry via standard OpenTelemetry Protocol (OTLP)
- ✅ **Dual Protocol Support**: HTTP/JSON and gRPC endpoints for maximum flexibility
- ✅ **Real-time Streaming**: WebSocket-based updates for live telemetry monitoring
- ✅ **High-throughput Storage**: Concurrent, thread-safe in-memory storage with secondary indexes
- ✅ **Dashboard-ready API**: REST endpoints optimized for frontend visualization
- ✅ **Health Checks**: Built-in system health and telemetry stats endpoints

## Quick Start

### Prerequisites

- .NET 10 SDK
- Port 5003 (HTTP/REST) and 4317 (gRPC) available

### Installation

```bash
cd otel-dashboard-api
dotnet restore
dotnet build
```

### Running

```bash
# Development mode with hot reload
dotnet run

# Release mode
dotnet run --configuration Release
```

The API will be available at:
- **HTTP/REST**: http://localhost:5003
- **gRPC**: localhost:4317 (port 5003 also supports gRPC via HTTP/2)

## Architecture

### Port Configuration

- **5003**: HTTP/1.1 and HTTP/2
  - REST API endpoints for querying telemetry
  - gRPC endpoint for OTLP export (alternative to 4317)
  - WebSocket streaming for real-time updates

- **4317**: HTTP/2 only
  - Standard OTLP gRPC port per OpenTelemetry specification
  - Primary gRPC endpoint for telemetry ingestion

### Core Components

```
├── Controllers/          # REST API endpoints
│   ├── HealthController        # System health & stats
│   ├── TracesController        # Query traces
│   ├── MetricsController       # Query metrics
│   ├── LogsController          # Query logs
│   └── SseStreamController     # WebSocket streaming
│
├── Services/            # Business logic
│   ├── OtlpTraceGrpcService    # Trace ingestion
│   ├── OtlpMetricsGrpcService  # Metrics ingestion
│   ├── OtlpLogsGrpcService     # Logs ingestion
│   ├── InMemoryStore           # Data storage
│   ├── WebSocketStreamService  # Real-time streaming
│   └── OtlpHelpers             # Data conversion utilities
│
├── Models/              # Domain models
│   └── OtelModels.cs           # Trace, Metric, Log types
│
└── Protos/              # Protocol Buffer definitions
    ├── common.proto            # Shared types
    ├── trace.proto             # Trace service & types
    ├── metrics.proto           # Metrics service & types
    └── logs.proto              # Logs service & types
```

## API Reference

### Health & Stats

```bash
GET /api/v1/health
```

Returns system health status and telemetry counts:
```json
{
  "status": "healthy",
  "timestamp": "2026-02-01T01:37:28.52105Z",
  "stats": {
    "logs": 18,
    "metrics": 53,
    "traces": 7
  },
  "websocket": {
    "connections": 1,
    "subscriptions": {}
  }
}
```

### Query Traces

```bash
GET /api/v1/traces?serviceName=my-service&minDurationMs=100&limit=50
GET /api/v1/traces/{traceId}
```

### Query Metrics

```bash
GET /api/v1/metrics?serviceName=my-service&limit=50
```

### Query Logs

```bash
GET /api/v1/logs?serviceName=my-service&limit=50
```

### OTLP HTTP Endpoints

Send OTLP data via HTTP POST:

```bash
# Traces
curl -X POST http://localhost:5003/v1/traces \
  -H "Content-Type: application/json" \
  -d '{...}'

# Metrics
curl -X POST http://localhost:5003/v1/metrics \
  -H "Content-Type: application/json" \
  -d '{...}'

# Logs
curl -X POST http://localhost:5003/v1/logs \
  -H "Content-Type: application/json" \
  -d '{...}'
```

### WebSocket Streaming

Connect to real-time telemetry updates:

```bash
ws://localhost:5003/ws/stream
```

## Configuration

### Storage Limits

Configure in `Services/InMemoryStore.cs`:

```csharp
private const int MaxLogs = 100_000;
private const int MaxMetrics = 100_000;
private const int MaxTraces = 50_000;
```

Storage uses **FIFO eviction** when limits are exceeded.

### Application Settings

Edit `appsettings.json` or `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

## Testing

### Test HTTP Endpoints

```bash
# Send a trace
curl -X POST http://localhost:5003/v1/traces \
  -H "Content-Type: application/json" \
  -d @test-trace.json

# Query traces
curl http://localhost:5003/api/v1/traces?limit=10

# Get specific trace
curl http://localhost:5003/api/v1/traces/abcdef0123456789abcdef0123456789
```

### Test gRPC Endpoints

Use a gRPC client tool like [grpcurl](https://github.com/fullstorydev/grpcurl):

```bash
# List available services
grpcurl -plaintext localhost:4317 list

# Send traces via gRPC
grpcurl -plaintext -d @ localhost:4317 opentelemetry.proto.collector.trace.v1.TraceService/Export < trace.json
```

### Swagger/OpenAPI

API documentation available at: **http://localhost:5003/swagger**

## Integration with OpenTelemetry SDK

Configure your application to export telemetry to this dashboard:

### .NET Example

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add OpenTelemetry
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(opts => opts.Endpoint = new Uri("http://localhost:4317")))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(opts => opts.Endpoint = new Uri("http://localhost:4317")))
    .WithLogging(logging => logging
        .AddOtlpExporter(opts => opts.Endpoint = new Uri("http://localhost:4317")));
```

## Proto File Structure

The project uses separate proto files to match OpenTelemetry SDK expectations:

```
Protos/
├── common.proto      # Package: opentelemetry.proto.common.v1
├── trace.proto       # Package: opentelemetry.proto.collector.trace.v1
├── metrics.proto     # Package: opentelemetry.proto.collector.metrics.v1
└── logs.proto        # Package: opentelemetry.proto.collector.logs.v1
```

**Important**: Each service uses the correct package name:
- Traces: `opentelemetry.proto.collector.trace.v1.TraceService/Export`
- Metrics: `opentelemetry.proto.collector.metrics.v1.MetricsService/Export`
- Logs: `opentelemetry.proto.collector.logs.v1.LogsService/Export`

Proto files are compiled during build to `obj/Debug/net10.0/Protos/` and generate corresponding C# service classes.

## Storage Model

### Traces
- Stored by trace ID in a dictionary
- Spans organized hierarchically
- Oldest traces evicted first by root span start time
- Limit: 50,000 traces

### Metrics
- Stored by name in a dictionary
- Latest value retained per metric
- Attributes tracked for filtering
- Limit: 100,000 metric entries

### Logs
- Stored in a concurrent queue
- Linked to traces via trace/span IDs
- Severity and attributes indexed
- Limit: 100,000 log entries

## Performance Considerations

- **Concurrent Collections**: Uses `ConcurrentQueue`, `ConcurrentDictionary` for thread-safe operations
- **Memory**: In-memory storage requires ~50-100MB for full capacity
- **Throughput**: Designed for 1000+ telemetry items/second
- **Eviction**: FIFO based on entry time to maintain capacity limits
- **WebSocket**: Bounded channels prevent unbounded growth of subscriptions

## Areas Requiring Caution

1. **Thread Safety**: InMemoryStore uses concurrent collections; maintain lock discipline when iterating
2. **ID Format**: Always use HEX format for trace/span IDs (not Base64)
3. **Timestamp Precision**: Handle nanosecond-to-millisecond conversion carefully
4. **Memory Leaks**: Ensure WebSocket connections are properly closed
5. **Large Payloads**: Large attribute dictionaries may impact performance

## Development

### Build

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

### Format Code

```bash
dotnet format
```

### Regenerate Proto Files

Proto files are automatically regenerated during build. If needed manually:

```bash
dotnet clean
dotnet build
```

Generated files appear in: `obj/Debug/net10.0/Protos/`

## Deployment

### Docker

```bash
docker build -t otel-dashboard-api .
docker run -p 5003:5003 -p 4317:4317 otel-dashboard-api
```

### Kubernetes

```bash
kubectl apply -f k8s-deployment.yaml
```

Configure service to expose both ports (5003 and 4317).

## Related Documentation

- [OpenTelemetry Specification](https://opentelemetry.io/docs/specs/)
- [OTLP Protocol](https://opentelemetry.io/docs/specs/otel/protocol/)
- [ASP.NET Core gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/)
- [.NET Concurrency](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)

## License

See LICENSE file for details.

## Contributing

Contributions welcome! Please ensure:
- Code follows existing patterns and conventions
- All tests pass
- Changes are documented
- Proto files are properly formatted

## Support

For issues or questions, please refer to the [Copilot Instructions](./.github/copilot-instructions.md) for detailed technical documentation.
