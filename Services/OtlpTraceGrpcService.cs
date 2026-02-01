using Grpc.Core;
using OpenTelemetry.Proto.Collector.Trace;
using OpenTelemetry.Proto.Common;
using OtelDashboardApi.Models;
using ProtoStatusCode = OpenTelemetry.Proto.Collector.Trace.StatusCode;

namespace OtelDashboardApi.Services;

/// <summary>
/// gRPC service for receiving OTLP trace data.
/// Implements the OpenTelemetry Collector TraceService protocol.
/// </summary>
public class OtlpTraceGrpcService : TraceService.TraceServiceBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<OtlpTraceGrpcService> _logger;

    public OtlpTraceGrpcService(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<OtlpTraceGrpcService> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    public override async Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request, 
        ServerCallContext context)
    {
        var spanCount = 0;
        var rejectedSpans = 0;

        try
        {
            foreach (var resourceSpans in request.ResourceSpans)
            {
                var serviceName = OtlpHelpers.GetServiceName(resourceSpans.Resource);

                foreach (var scopeSpans in resourceSpans.ScopeSpans)
                {
                    var scopeName = scopeSpans.Scope?.Name ?? "";
                    var scopeVersion = scopeSpans.Scope?.Version ?? "";

                    foreach (var span in scopeSpans.Spans)
                    {
                        try
                        {
                            var traceSpan = ConvertSpan(span, serviceName, scopeName, scopeVersion);
                            _store.AddSpan(traceSpan);
                            
                            // Broadcast to WebSocket clients
                            await _wsService.BroadcastAsync("traces", traceSpan, context.CancellationToken);
                            spanCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to process span {SpanId}", OtlpHelpers.BytesToHex(span.SpanId));
                            rejectedSpans++;
                        }
                    }
                }
            }

            _logger.LogDebug("Received {Count} spans via OTLP gRPC (rejected: {Rejected})", spanCount, rejectedSpans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OTLP trace export request");
            return new ExportTraceServiceResponse
            {
                PartialSuccess = new PartialSuccess
                {
                    RejectedSpans = spanCount + rejectedSpans,
                    ErrorMessage = ex.Message
                }
            };
        }

        return new ExportTraceServiceResponse
        {
            PartialSuccess = rejectedSpans > 0 ? new PartialSuccess
            {
                RejectedSpans = rejectedSpans,
                ErrorMessage = $"{rejectedSpans} spans rejected due to processing errors"
            } : null
        };
    }

    private static TraceSpan ConvertSpan(Span otlpSpan, string serviceName, string scopeName, string scopeVersion)
    {
        var startTime = OtlpHelpers.NanosToDateTime(otlpSpan.StartTimeUnixNano);
        var endTime = OtlpHelpers.NanosToDateTime(otlpSpan.EndTimeUnixNano);
        var durationMs = (endTime - startTime).TotalMilliseconds;
        
        // Build attributes including scope info
        var attributes = OtlpHelpers.ToStringAttributesDict(otlpSpan.Attributes);
        if (!string.IsNullOrEmpty(scopeName))
        {
            attributes["otel.scope.name"] = scopeName;
        }
        if (!string.IsNullOrEmpty(scopeVersion))
        {
            attributes["otel.scope.version"] = scopeVersion;
        }

        return new TraceSpan
        {
            TraceId = OtlpHelpers.BytesToHex(otlpSpan.TraceId),
            SpanId = OtlpHelpers.BytesToHex(otlpSpan.SpanId),
            ParentSpanId = otlpSpan.ParentSpanId?.IsEmpty != false ? null : OtlpHelpers.BytesToHex(otlpSpan.ParentSpanId),
            Name = otlpSpan.Name ?? "unknown",
            Kind = (int)OtlpHelpers.MapSpanKind(otlpSpan.Kind),
            StartTime = startTime,
            EndTime = endTime,
            DurationMs = durationMs,
            ServiceName = serviceName,
            StatusCode = (int)(otlpSpan.Status?.Code ?? ProtoStatusCode.Unset),
            StatusMessage = otlpSpan.Status?.Message,
            Attributes = attributes,
            Events = otlpSpan.Events.Select(e => new Models.SpanEvent
            {
                Name = e.Name ?? "",
                Timestamp = OtlpHelpers.NanosToDateTime(e.TimeUnixNano),
                Attributes = OtlpHelpers.ToStringAttributesDict(e.Attributes)
            }).ToList(),
            Links = otlpSpan.Links.Select(l => new Models.SpanLink
            {
                TraceId = OtlpHelpers.BytesToHex(l.TraceId),
                SpanId = OtlpHelpers.BytesToHex(l.SpanId)
            }).ToList()
        };
    }
}
