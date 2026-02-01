using Grpc.Core;
using OpenTelemetry.Proto.Collector.Metrics;
using OpenTelemetry.Proto.Common;
using OtelDashboardApi.Models;

namespace OtelDashboardApi.Services;

/// <summary>
/// gRPC service for receiving OTLP metrics data.
/// Implements the OpenTelemetry Collector MetricsService protocol.
/// </summary>
public class OtlpMetricsGrpcService : MetricsService.MetricsServiceBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<OtlpMetricsGrpcService> _logger;

    public OtlpMetricsGrpcService(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<OtlpMetricsGrpcService> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    public override async Task<ExportMetricsServiceResponse> Export(
        ExportMetricsServiceRequest request, 
        ServerCallContext context)
    {
        var metricCount = 0;
        var rejectedDataPoints = 0;

        try
        {
            foreach (var resourceMetrics in request.ResourceMetrics)
            {
                var serviceName = OtlpHelpers.GetServiceName(resourceMetrics.Resource);

                foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
                {
                    var scopeName = scopeMetrics.Scope?.Name ?? "";
                    var scopeVersion = scopeMetrics.Scope?.Version ?? "";

                    foreach (var metric in scopeMetrics.Metrics)
                    {
                        try
                        {
                            var entries = ConvertMetric(metric, serviceName, scopeName, scopeVersion);
                            foreach (var entry in entries)
                            {
                                _store.AddMetric(entry);
                                
                                // Broadcast to WebSocket clients
                                await _wsService.BroadcastAsync("metrics", entry, context.CancellationToken);
                                metricCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to process metric {MetricName}", metric.Name);
                            rejectedDataPoints++;
                        }
                    }
                }
            }

            _logger.LogDebug("Received {Count} metric data points via OTLP gRPC (rejected: {Rejected})", metricCount, rejectedDataPoints);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OTLP metrics export request");
            return new ExportMetricsServiceResponse
            {
                PartialSuccess = new PartialSuccess
                {
                    RejectedDataPoints = metricCount + rejectedDataPoints,
                    ErrorMessage = ex.Message
                }
            };
        }

        return new ExportMetricsServiceResponse
        {
            PartialSuccess = rejectedDataPoints > 0 ? new PartialSuccess
            {
                RejectedDataPoints = rejectedDataPoints,
                ErrorMessage = $"{rejectedDataPoints} data points rejected due to processing errors"
            } : null
        };
    }

    private static IEnumerable<MetricEntry> ConvertMetric(
        Metric metric, 
        string serviceName,
        string scopeName,
        string scopeVersion)
    {
        var metricType = GetMetricType(metric);
        var description = metric.Description;
        var unit = metric.Unit;
        var name = metric.Name;

        // Extract data points based on metric type
        var dataPoints = metric.DataCase switch
        {
            Metric.DataOneofCase.Gauge => ConvertNumberDataPoints(metric.Gauge.DataPoints),
            Metric.DataOneofCase.Sum => ConvertNumberDataPoints(metric.Sum.DataPoints),
            Metric.DataOneofCase.Histogram => ConvertHistogramDataPoints(metric.Histogram.DataPoints),
            Metric.DataOneofCase.ExponentialHistogram => ConvertExpHistogramDataPoints(metric.ExponentialHistogram.DataPoints),
            Metric.DataOneofCase.Summary => ConvertSummaryDataPoints(metric.Summary.DataPoints),
            _ => Enumerable.Empty<(double Value, DateTime Timestamp, Dictionary<string, string> Attributes)>()
        };

        foreach (var (value, timestamp, attributes) in dataPoints)
        {
            // Add scope info to attributes
            if (!string.IsNullOrEmpty(scopeName))
            {
                attributes["otel.scope.name"] = scopeName;
            }
            if (!string.IsNullOrEmpty(scopeVersion))
            {
                attributes["otel.scope.version"] = scopeVersion;
            }

            yield return new MetricEntry
            {
                Name = name,
                Description = description,
                Unit = unit,
                Value = value,
                Type = metricType,
                Timestamp = timestamp,
                ServiceName = serviceName,
                Attributes = attributes
            };
        }
    }

    private static MetricType GetMetricType(Metric metric)
    {
        return metric.DataCase switch
        {
            Metric.DataOneofCase.Gauge => MetricType.Gauge,
            Metric.DataOneofCase.Sum => metric.Sum.IsMonotonic ? MetricType.Counter : MetricType.Sum,
            Metric.DataOneofCase.Histogram => MetricType.Histogram,
            Metric.DataOneofCase.ExponentialHistogram => MetricType.Histogram,
            Metric.DataOneofCase.Summary => MetricType.Gauge,
            _ => MetricType.Gauge
        };
    }

    private static IEnumerable<(double Value, DateTime Timestamp, Dictionary<string, string> Attributes)> 
        ConvertNumberDataPoints(IEnumerable<NumberDataPoint> dataPoints)
    {
        foreach (var dp in dataPoints)
        {
            var value = dp.ValueCase switch
            {
                NumberDataPoint.ValueOneofCase.AsDouble => dp.AsDouble,
                NumberDataPoint.ValueOneofCase.AsInt => dp.AsInt,
                _ => 0.0
            };

            var timestamp = dp.TimeUnixNano > 0 
                ? OtlpHelpers.NanosToDateTime(dp.TimeUnixNano)
                : dp.StartTimeUnixNano > 0 
                    ? OtlpHelpers.NanosToDateTime(dp.StartTimeUnixNano)
                    : DateTime.UtcNow;

            yield return (value, timestamp, OtlpHelpers.ToStringAttributesDict(dp.Attributes));
        }
    }

    private static IEnumerable<(double Value, DateTime Timestamp, Dictionary<string, string> Attributes)> 
        ConvertHistogramDataPoints(IEnumerable<HistogramDataPoint> dataPoints)
    {
        foreach (var dp in dataPoints)
        {
            // For histograms, we'll emit the count and optionally sum as separate metrics
            var timestamp = dp.TimeUnixNano > 0 
                ? OtlpHelpers.NanosToDateTime(dp.TimeUnixNano)
                : DateTime.UtcNow;

            var attributes = OtlpHelpers.ToStringAttributesDict(dp.Attributes);
            
            // Emit count
            attributes["aggregate"] = "count";
            yield return ((double)dp.Count, timestamp, new Dictionary<string, string>(attributes));

            // Emit sum if present
            if (dp.HasSum)
            {
                attributes["aggregate"] = "sum";
                yield return (dp.Sum, timestamp, new Dictionary<string, string>(attributes));
            }

            // Emit min/max if present
            if (dp.HasMin)
            {
                attributes["aggregate"] = "min";
                yield return (dp.Min, timestamp, new Dictionary<string, string>(attributes));
            }
            if (dp.HasMax)
            {
                attributes["aggregate"] = "max";
                yield return (dp.Max, timestamp, new Dictionary<string, string>(attributes));
            }
        }
    }

    private static IEnumerable<(double Value, DateTime Timestamp, Dictionary<string, string> Attributes)> 
        ConvertExpHistogramDataPoints(IEnumerable<ExponentialHistogramDataPoint> dataPoints)
    {
        foreach (var dp in dataPoints)
        {
            var timestamp = dp.TimeUnixNano > 0 
                ? OtlpHelpers.NanosToDateTime(dp.TimeUnixNano)
                : DateTime.UtcNow;

            var attributes = OtlpHelpers.ToStringAttributesDict(dp.Attributes);
            
            attributes["aggregate"] = "count";
            yield return ((double)dp.Count, timestamp, new Dictionary<string, string>(attributes));

            if (dp.HasSum)
            {
                attributes["aggregate"] = "sum";
                yield return (dp.Sum, timestamp, new Dictionary<string, string>(attributes));
            }
        }
    }

    private static IEnumerable<(double Value, DateTime Timestamp, Dictionary<string, string> Attributes)> 
        ConvertSummaryDataPoints(IEnumerable<SummaryDataPoint> dataPoints)
    {
        foreach (var dp in dataPoints)
        {
            var timestamp = dp.TimeUnixNano > 0 
                ? OtlpHelpers.NanosToDateTime(dp.TimeUnixNano)
                : DateTime.UtcNow;

            var attributes = OtlpHelpers.ToStringAttributesDict(dp.Attributes);

            // Emit count
            attributes["aggregate"] = "count";
            yield return ((double)dp.Count, timestamp, new Dictionary<string, string>(attributes));

            // Emit sum
            attributes["aggregate"] = "sum";
            yield return (dp.Sum, timestamp, new Dictionary<string, string>(attributes));

            // Emit quantiles
            foreach (var q in dp.QuantileValues)
            {
                var qAttributes = new Dictionary<string, string>(attributes)
                {
                    ["aggregate"] = "quantile",
                    ["quantile"] = q.Quantile.ToString("F2")
                };
                yield return (q.Value, timestamp, qAttributes);
            }
        }
    }
}
