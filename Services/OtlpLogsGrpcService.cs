using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs;
using OpenTelemetry.Proto.Common;
using OtelDashboardApi.Models;

namespace OtelDashboardApi.Services;

/// <summary>
/// gRPC service for receiving OTLP log data.
/// Implements the OpenTelemetry Collector LogsService protocol.
/// </summary>
public class OtlpLogsGrpcService : LogsService.LogsServiceBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<OtlpLogsGrpcService> _logger;

    public OtlpLogsGrpcService(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<OtlpLogsGrpcService> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    public override async Task<ExportLogsServiceResponse> Export(
        ExportLogsServiceRequest request, 
        ServerCallContext context)
    {
        var logCount = 0;
        var rejectedLogs = 0;

        try
        {
            foreach (var resourceLogs in request.ResourceLogs)
            {
                var serviceName = OtlpHelpers.GetServiceName(resourceLogs.Resource);
                var serviceVersion = OtlpHelpers.GetResourceAttribute(resourceLogs.Resource, "service.version");
                var deploymentEnv = OtlpHelpers.GetResourceAttribute(resourceLogs.Resource, "deployment.environment");

                foreach (var scopeLogs in resourceLogs.ScopeLogs)
                {
                    var scopeName = scopeLogs.Scope?.Name ?? "";
                    var scopeVersion = scopeLogs.Scope?.Version ?? "";

                    foreach (var logRecord in scopeLogs.LogRecords)
                    {
                        try
                        {
                            var logEntry = ConvertLogRecord(logRecord, serviceName, serviceVersion, deploymentEnv, scopeName, scopeVersion);
                            _store.AddLog(logEntry);
                            
                            // Broadcast to WebSocket clients
                            await _wsService.BroadcastAsync("logs", logEntry, context.CancellationToken);
                            logCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to process log record");
                            rejectedLogs++;
                        }
                    }
                }
            }

            _logger.LogDebug("Received {Count} logs via OTLP gRPC (rejected: {Rejected})", logCount, rejectedLogs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OTLP logs export request");
            return new ExportLogsServiceResponse
            {
                PartialSuccess = new PartialSuccess
                {
                    RejectedLogRecords = logCount + rejectedLogs,
                    ErrorMessage = ex.Message
                }
            };
        }

        return new ExportLogsServiceResponse
        {
            PartialSuccess = rejectedLogs > 0 ? new PartialSuccess
            {
                RejectedLogRecords = rejectedLogs,
                ErrorMessage = $"{rejectedLogs} log records rejected due to processing errors"
            } : null
        };
    }

    private static LogEntry ConvertLogRecord(
        LogRecord logRecord, 
        string serviceName, 
        string? serviceVersion, 
        string? deploymentEnv,
        string scopeName,
        string scopeVersion)
    {
        var timestamp = logRecord.TimeUnixNano > 0 
            ? OtlpHelpers.NanosToDateTime(logRecord.TimeUnixNano)
            : logRecord.ObservedTimeUnixNano > 0 
                ? OtlpHelpers.NanosToDateTime(logRecord.ObservedTimeUnixNano)
                : DateTime.UtcNow;

        // Build attributes including scope info
        var attributes = OtlpHelpers.ToAttributesDict(logRecord.Attributes);
        if (!string.IsNullOrEmpty(scopeName))
        {
            attributes["otel.scope.name"] = scopeName;
        }
        if (!string.IsNullOrEmpty(scopeVersion))
        {
            attributes["otel.scope.version"] = scopeVersion;
        }
        if (!string.IsNullOrEmpty(logRecord.SeverityText))
        {
            attributes["severity_text"] = logRecord.SeverityText;
        }

        return new LogEntry
        {
            TraceId = OtlpHelpers.BytesToHex(logRecord.TraceId),
            SpanId = OtlpHelpers.BytesToHex(logRecord.SpanId),
            Timestamp = timestamp,
            Level = OtlpHelpers.MapSeverity(logRecord.SeverityNumber),
            ServiceName = serviceName,
            Message = OtlpHelpers.GetLogMessage(logRecord.Body),
            Attributes = attributes,
            Resource = new ResourceInfo
            {
                ServiceName = serviceName,
                ServiceVersion = serviceVersion,
                DeploymentEnvironment = deploymentEnv
            }
        };
    }
}
