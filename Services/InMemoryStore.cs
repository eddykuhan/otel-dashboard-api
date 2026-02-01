using System.Collections.Concurrent;
using System.Text.Json;
using OtelDashboardApi.Extensions;
using OtelDashboardApi.Models;

namespace OtelDashboardApi.Services;

/// <summary>
/// Thread-safe in-memory storage for OpenTelemetry data.
/// Uses concurrent collections for lock-free access and maintains multiple indexes for fast queries.
/// </summary>
public class InMemoryStore
{
    private readonly ILogger<InMemoryStore> _logger;
    
    // Configuration
    private readonly int _maxLogs = 100_000;
    private readonly int _maxMetrics = 100_000;
    private readonly int _maxTraces = 50_000;
    
    // Thread-safe counters for ID generation
    private long _logIdCounter = 0;
    private long _metricIdCounter = 0;
    
    // Primary storage with concurrent access
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly ConcurrentQueue<MetricEntry> _metrics = new();
    private readonly ConcurrentDictionary<string, List<TraceSpan>> _tracesByTraceId = new();
    
    // Secondary indexes for fast queries
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LogEntry>> _logsByService = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LogEntry>> _logsByTraceId = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MetricEntry>> _metricsByName = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MetricEntry>> _metricsByService = new();

    public InMemoryStore(ILogger<InMemoryStore> logger)
    {
        _logger = logger;
    }

    #region Log Operations

    /// <summary>
    /// Adds a log entry with automatic ID assignment.
    /// Returns the assigned ID.
    /// </summary>
    public long AddLog(LogEntry log)
    {
        var id = Interlocked.Increment(ref _logIdCounter);
        var logWithId = log with { Id = id };
        
        _logs.Enqueue(logWithId);
        
        // Update indexes
        GetOrAddQueue(_logsByService, log.ServiceName).Enqueue(logWithId);
        if (!string.IsNullOrEmpty(log.TraceId))
        {
            GetOrAddQueue(_logsByTraceId, log.TraceId).Enqueue(logWithId);
        }
        
        // Trim if over capacity
        TrimLogsIfNeeded();
        
        return id;
    }

    /// <summary>
    /// Gets logs since a specific ID (for SSE streaming).
    /// </summary>
    public List<LogEntry> GetLogsSince(long sinceId, int limit = 100)
    {
        return _logs
            .Where(l => l.Id > sinceId)
            .OrderBy(l => l.Id)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Queries logs with optional filters.
    /// </summary>
    public List<LogEntry> QueryLogs(
        string? serviceName = null,
        Models.LogLevel? level = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? traceId = null,
        int limit = 100)
    {
        IEnumerable<LogEntry> query;
        
        // Use index if possible
        if (!string.IsNullOrEmpty(traceId) && _logsByTraceId.TryGetValue(traceId, out var traceQueue))
        {
            query = traceQueue;
        }
        else if (!string.IsNullOrEmpty(serviceName) && _logsByService.TryGetValue(serviceName, out var serviceQueue))
        {
            query = serviceQueue;
        }
        else
        {
            query = _logs;
        }
        
        // Apply remaining filters
        if (level.HasValue)
            query = query.Where(l => l.Level == level.Value);
        if (startTime.HasValue)
            query = query.Where(l => l.Timestamp >= startTime.Value);
        if (endTime.HasValue)
            query = query.Where(l => l.Timestamp <= endTime.Value);
        
        return query
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .ToList();
    }

    private void TrimLogsIfNeeded()
    {
        while (_logs.Count > _maxLogs && _logs.TryDequeue(out _))
        {
            // Just dequeue, indexes will have stale references but that's acceptable
            // for performance - queries will filter naturally
        }
    }

    #endregion

    #region Metric Operations

    public long AddMetric(MetricEntry metric)
    {
        var id = Interlocked.Increment(ref _metricIdCounter);
        var metricWithId = metric with { Id = id };
        
        _metrics.Enqueue(metricWithId);
        
        // Update indexes
        GetOrAddQueue(_metricsByName, metric.Name).Enqueue(metricWithId);
        GetOrAddQueue(_metricsByService, metric.ServiceName).Enqueue(metricWithId);
        
        TrimMetricsIfNeeded();
        
        return id;
    }

    public List<MetricEntry> QueryMetrics(
        string? name = null,
        string? serviceName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int limit = 100)
    {
        IEnumerable<MetricEntry> query;
        
        if (!string.IsNullOrEmpty(name) && _metricsByName.TryGetValue(name, out var nameQueue))
        {
            query = nameQueue;
        }
        else if (!string.IsNullOrEmpty(serviceName) && _metricsByService.TryGetValue(serviceName, out var serviceQueue))
        {
            query = serviceQueue;
        }
        else
        {
            query = _metrics;
        }
        
        if (startTime.HasValue)
            query = query.Where(m => m.Timestamp >= startTime.Value);
        if (endTime.HasValue)
            query = query.Where(m => m.Timestamp <= endTime.Value);
        
        return query
            .OrderByDescending(m => m.Timestamp)
            .Take(limit)
            .ToList();
    }

    private void TrimMetricsIfNeeded()
    {
        while (_metrics.Count > _maxMetrics && _metrics.TryDequeue(out _)) { }
    }

    #endregion

    #region Trace Operations

    public void AddSpan(TraceSpan span)
    {
        var spans = _tracesByTraceId.GetOrAdd(span.TraceId, _ => new List<TraceSpan>());
        lock (spans)
        {
            spans.Add(span);
        }
        
        TrimTracesIfNeeded();
    }

    public List<TraceSpan>? GetTrace(string traceId)
    {
        if (_tracesByTraceId.TryGetValue(traceId, out var spans))
        {
            lock (spans)
            {
                return spans.OrderBy(s => s.StartTime).ToList();
            }
        }
        return null;
    }

    public List<(string TraceId, TraceSpan RootSpan)> QueryTraces(
        string? serviceName = null,
        long? minDurationMs = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int limit = 50)
    {
        var results = new List<(string TraceId, TraceSpan RootSpan)>();
        
        foreach (var (traceId, spans) in _tracesByTraceId)
        {
            TraceSpan? rootSpan;
            lock (spans)
            {
                rootSpan = spans.FirstOrDefault(s => s.ParentSpanId == null);
                if (rootSpan == null && spans.Count > 0)
                {
                    rootSpan = spans.OrderBy(s => s.StartTime).First();
                }
            }
            
            if (rootSpan == null) continue;
            
            // Apply filters
            if (!string.IsNullOrEmpty(serviceName) && rootSpan.ServiceName != serviceName)
                continue;
            if (minDurationMs.HasValue && rootSpan.DurationMs < minDurationMs.Value)
                continue;
            if (startTime.HasValue && rootSpan.StartTime < startTime.Value)
                continue;
            if (endTime.HasValue && rootSpan.EndTime > endTime.Value)
                continue;
            
            results.Add((traceId, rootSpan));
            
            if (results.Count >= limit)
                break;
        }
        
        return results
            .OrderByDescending(r => r.RootSpan.StartTime)
            .ToList();
    }

    private void TrimTracesIfNeeded()
    {
        // Remove oldest traces when over capacity
        while (_tracesByTraceId.Count > _maxTraces)
        {
            var oldest = _tracesByTraceId
                .SelectMany(kvp => 
                {
                    lock (kvp.Value)
                    {
                        return kvp.Value.Select(s => (kvp.Key, s.StartTime));
                    }
                })
                .OrderBy(x => x.StartTime)
                .FirstOrDefault();
            
            if (oldest != default)
            {
                _tracesByTraceId.TryRemove(oldest.Key, out _);
            }
            else
            {
                break;
            }
        }
    }

    #endregion

    #region Statistics

    public (int LogCount, int MetricCount, int TraceCount) GetStats()
    {
        return (_logs.Count, _metrics.Count, _tracesByTraceId.Count);
    }

    #endregion

    #region JSON OTLP Import

    /// <summary>
    /// Adds traces from JSON OTLP format (HTTP/JSON exporter)
    /// </summary>
    public async Task AddTracesJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            
            // Handle both wrapped and unwrapped formats
            var resourceSpans = root.TryGetProperty("resourceSpans", out var rs) ? rs : root;
            
            foreach (var resourceSpan in resourceSpans.EnumerateArray())
            {
                var resource = resourceSpan.GetPropertyOrDefault("resource", JsonDocument.Parse("{\"attributes\":[]}").RootElement);
                var scopeSpans = resourceSpan.GetPropertyOrDefault("scopeSpans", JsonDocument.Parse("[{\"spans\":[]}]").RootElement);
                
                var serviceName = GetAttributeValue(resource, "service.name") ?? "unknown";
                
                foreach (var scopeSpan in scopeSpans.EnumerateArray())
                {
                    var spans = scopeSpan.GetPropertyOrDefault("spans", JsonDocument.Parse("[]").RootElement);
                    
                    foreach (var span in spans.EnumerateArray())
                    {
                        var traceSpan = ParseTraceSpan(span, serviceName);
                        if (traceSpan != null)
                        {
                            AddSpan(traceSpan);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing OTLP traces JSON");
            throw;
        }
    }

    /// <summary>
    /// Adds metrics from JSON OTLP format (HTTP/JSON exporter)
    /// </summary>
    public async Task AddMetricsJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            
            var resourceMetrics = root.TryGetProperty("resourceMetrics", out var rm) ? rm : root;
            
            foreach (var resourceMetric in resourceMetrics.EnumerateArray())
            {
                var resource = resourceMetric.GetPropertyOrDefault("resource", JsonDocument.Parse("{\"attributes\":[]}").RootElement);
                var scopeMetrics = resourceMetric.GetPropertyOrDefault("scopeMetrics", JsonDocument.Parse("[{\"metrics\":[]}]").RootElement);
                
                var serviceName = GetAttributeValue(resource, "service.name") ?? "unknown";
                
                foreach (var scopeMetric in scopeMetrics.EnumerateArray())
                {
                    var metrics = scopeMetric.GetPropertyOrDefault("metrics", JsonDocument.Parse("[]").RootElement);
                    
                    foreach (var metric in metrics.EnumerateArray())
                    {
                        var metricEntry = ParseMetricEntry(metric, serviceName);
                        if (metricEntry != null)
                        {
                            AddMetric(metricEntry);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing OTLP metrics JSON");
            throw;
        }
    }

    /// <summary>
    /// Adds logs from JSON OTLP format (HTTP/JSON exporter)
    /// </summary>
    public async Task AddLogsJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            
            var resourceLogs = root.TryGetProperty("resourceLogs", out var rl) ? rl : root;
            
            foreach (var resourceLog in resourceLogs.EnumerateArray())
            {
                var resource = resourceLog.GetPropertyOrDefault("resource", JsonDocument.Parse("{\"attributes\":[]}").RootElement);
                var scopeLogs = resourceLog.GetPropertyOrDefault("scopeLogs", JsonDocument.Parse("[{\"logRecords\":[]}]").RootElement);
                
                var serviceName = GetAttributeValue(resource, "service.name") ?? "unknown";
                var serviceVersion = GetAttributeValue(resource, "service.version");
                var deploymentEnv = GetAttributeValue(resource, "deployment.environment");
                
                foreach (var scopeLog in scopeLogs.EnumerateArray())
                {
                    var logRecords = scopeLog.GetPropertyOrDefault("logRecords", JsonDocument.Parse("[]").RootElement);
                    
                    foreach (var logRecord in logRecords.EnumerateArray())
                    {
                        var logEntry = ParseLogEntry(logRecord, serviceName, serviceVersion, deploymentEnv);
                        if (logEntry != null)
                        {
                            AddLog(logEntry);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing OTLP logs JSON");
            throw;
        }
    }

    private LogEntry? ParseLogEntry(JsonElement logRecord, string serviceName, string? serviceVersion, string? deploymentEnv)
    {
        try
        {
            var traceIdHex = logRecord.GetPropertyOrDefault("traceId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            var spanIdHex = logRecord.GetPropertyOrDefault("spanId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            
            // Get timestamp (prefer timeUnixNano, fallback to observedTimeUnixNano)
            var timeUnixNano = logRecord.GetPropertyOrDefault("timeUnixNano", JsonDocument.Parse("\"0\"").RootElement);
            var observedTimeUnixNano = logRecord.GetPropertyOrDefault("observedTimeUnixNano", JsonDocument.Parse("\"0\"").RootElement);
            
            ulong timestampNanos = 0;
            if (timeUnixNano.ValueKind == JsonValueKind.String && ulong.TryParse(timeUnixNano.GetString(), out var ts1))
                timestampNanos = ts1;
            else if (timeUnixNano.ValueKind == JsonValueKind.Number)
                timestampNanos = timeUnixNano.GetUInt64();
            
            if (timestampNanos == 0)
            {
                if (observedTimeUnixNano.ValueKind == JsonValueKind.String && ulong.TryParse(observedTimeUnixNano.GetString(), out var ts2))
                    timestampNanos = ts2;
                else if (observedTimeUnixNano.ValueKind == JsonValueKind.Number)
                    timestampNanos = observedTimeUnixNano.GetUInt64();
            }
            
            var timestamp = timestampNanos > 0 
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(timestampNanos / 1_000_000)).UtcDateTime 
                : DateTime.UtcNow;
            
            // Get severity
            var severityNumber = 0;
            if (logRecord.TryGetProperty("severityNumber", out var sevNum))
            {
                if (sevNum.ValueKind == JsonValueKind.Number)
                    severityNumber = sevNum.GetInt32();
                else if (sevNum.ValueKind == JsonValueKind.String && int.TryParse(sevNum.GetString(), out var sn))
                    severityNumber = sn;
            }
            
            var severityText = logRecord.GetPropertyOrDefault("severityText", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            
            // Get message from body
            var body = logRecord.GetPropertyOrDefault("body", JsonDocument.Parse("{}").RootElement);
            var message = GetBodyMessage(body);
            
            // Get attributes
            var attributes = logRecord.GetPropertyOrDefault("attributes", JsonDocument.Parse("[]").RootElement);
            var attrDict = GetAttributesAsObjects(attributes);
            
            if (!string.IsNullOrEmpty(severityText))
            {
                attrDict["severity_text"] = severityText;
            }
            
            return new LogEntry
            {
                TraceId = traceIdHex,
                SpanId = spanIdHex,
                Timestamp = timestamp,
                Level = MapSeverityNumber(severityNumber),
                ServiceName = serviceName,
                Message = message,
                Attributes = attrDict,
                Resource = new ResourceInfo
                {
                    ServiceName = serviceName,
                    ServiceVersion = serviceVersion,
                    DeploymentEnvironment = deploymentEnv
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing log entry");
            return null;
        }
    }

    private static string GetBodyMessage(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.String)
            return body.GetString() ?? "";
        
        if (body.TryGetProperty("stringValue", out var sv))
            return sv.GetString() ?? "";
        
        if (body.TryGetProperty("kvlistValue", out var kvlist))
        {
            var parts = new List<string>();
            if (kvlist.TryGetProperty("values", out var values))
            {
                foreach (var kv in values.EnumerateArray())
                {
                    var key = kv.GetPropertyOrDefault("key", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
                    var val = kv.GetPropertyOrDefault("value", JsonDocument.Parse("{}").RootElement);
                    parts.Add($"{key}={GetAnyValueString(val)}");
                }
            }
            return string.Join(", ", parts);
        }
        
        if (body.TryGetProperty("arrayValue", out var arrayVal))
        {
            var parts = new List<string>();
            if (arrayVal.TryGetProperty("values", out var values))
            {
                foreach (var val in values.EnumerateArray())
                {
                    parts.Add(GetAnyValueString(val));
                }
            }
            return string.Join(" ", parts);
        }
        
        return body.ToString();
    }

    private static string GetAnyValueString(JsonElement val)
    {
        if (val.TryGetProperty("stringValue", out var sv))
            return sv.GetString() ?? "";
        if (val.TryGetProperty("intValue", out var iv))
            return iv.ValueKind == JsonValueKind.String ? iv.GetString() ?? "" : iv.GetInt64().ToString();
        if (val.TryGetProperty("doubleValue", out var dv))
            return dv.GetDouble().ToString();
        if (val.TryGetProperty("boolValue", out var bv))
            return bv.GetBoolean().ToString();
        return val.ToString();
    }

    private static Dictionary<string, object> GetAttributesAsObjects(JsonElement attributes)
    {
        var dict = new Dictionary<string, object>();
        foreach (var attr in attributes.EnumerateArray())
        {
            if (attr.TryGetProperty("key", out var keyEl))
            {
                var key = keyEl.GetString() ?? "";
                if (attr.TryGetProperty("value", out var val))
                {
                    object value = GetAnyValueString(val);
                    if (val.TryGetProperty("intValue", out var iv))
                    {
                        value = iv.ValueKind == JsonValueKind.String 
                            ? long.TryParse(iv.GetString(), out var l) ? l : iv.GetString()! 
                            : iv.GetInt64();
                    }
                    else if (val.TryGetProperty("doubleValue", out var dv))
                    {
                        value = dv.GetDouble();
                    }
                    else if (val.TryGetProperty("boolValue", out var bv))
                    {
                        value = bv.GetBoolean();
                    }
                    
                    dict[key] = value;
                }
            }
        }
        return dict;
    }

    private static Models.LogLevel MapSeverityNumber(int severityNumber)
    {
        return severityNumber switch
        {
            >= 21 => Models.LogLevel.Fatal,    // FATAL, FATAL2, FATAL3, FATAL4
            >= 17 => Models.LogLevel.Error,    // ERROR, ERROR2, ERROR3, ERROR4
            >= 13 => Models.LogLevel.Warn,     // WARN, WARN2, WARN3, WARN4
            >= 9 => Models.LogLevel.Info,      // INFO, INFO2, INFO3, INFO4
            >= 5 => Models.LogLevel.Debug,     // DEBUG, DEBUG2, DEBUG3, DEBUG4
            >= 1 => Models.LogLevel.Debug,     // TRACE, TRACE2, TRACE3, TRACE4
            _ => Models.LogLevel.Info
        };
    }

    private TraceSpan? ParseTraceSpan(JsonElement span, string serviceName)
    {
        try
        {
            var traceIdHex = span.GetPropertyOrDefault("traceId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            var spanIdHex = span.GetPropertyOrDefault("spanId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            var parentSpanIdHex = span.GetPropertyOrDefault("parentSpanId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            var name = span.GetPropertyOrDefault("name", JsonDocument.Parse("\"unknown\"").RootElement).GetString() ?? "unknown";
            var kind = span.GetPropertyOrDefault("kind", JsonDocument.Parse("0").RootElement).GetInt32();
            var startTimeUnixNano = GetUInt64FromJsonElement(span.GetPropertyOrDefault("startTimeUnixNano", JsonDocument.Parse("0").RootElement));
            var endTimeUnixNano = GetUInt64FromJsonElement(span.GetPropertyOrDefault("endTimeUnixNano", JsonDocument.Parse("0").RootElement));
            
            var startTime = startTimeUnixNano > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(startTimeUnixNano / 1_000_000)).UtcDateTime : DateTime.UtcNow;
            var endTime = endTimeUnixNano > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(endTimeUnixNano / 1_000_000)).UtcDateTime : DateTime.UtcNow;
            
            var durationMs = (endTime - startTime).TotalMilliseconds;
            
            var attributes = span.GetPropertyOrDefault("attributes", JsonDocument.Parse("[]").RootElement);
            var status = span.GetPropertyOrDefault("status", JsonDocument.Parse("{\"code\":0}").RootElement);
            var statusCode = status.GetPropertyOrDefault("code", JsonDocument.Parse("0").RootElement).GetInt32();
            var statusMessage = status.GetPropertyOrDefault("message", JsonDocument.Parse("\"\"").RootElement).GetString();
            
            return new TraceSpan
            {
                TraceId = traceIdHex,  // Keep hex format for consistency with OTLP standard
                SpanId = spanIdHex,
                ParentSpanId = string.IsNullOrEmpty(parentSpanIdHex) ? null : parentSpanIdHex,
                Name = name,
                Kind = kind,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = durationMs,
                ServiceName = serviceName,
                StatusCode = statusCode,
                StatusMessage = statusMessage,
                Attributes = GetAttributesDict(attributes),
                Events = span.GetPropertyOrDefault("events", JsonDocument.Parse("[]").RootElement)
                    .EnumerateArray()
                    .Select(e => {
                        var tsNano = e.TryGetProperty("timeUnixNano", out var ts) ? GetUInt64FromJsonElement(ts) : 0;
                        return new SpanEvent
                        {
                            Name = e.GetPropertyOrDefault("name", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "",
                            Timestamp = tsNano > 0 
                                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(tsNano / 1_000_000)).UtcDateTime 
                                : startTime,
                            Attributes = GetAttributesDict(e.GetPropertyOrDefault("attributes", JsonDocument.Parse("[]").RootElement))
                        };
                    })
                    .ToList(),
                Links = span.GetPropertyOrDefault("links", JsonDocument.Parse("[]").RootElement)
                    .EnumerateArray()
                    .Select(l => new SpanLink
                    {
                        TraceId = l.GetPropertyOrDefault("traceId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "",
                        SpanId = l.GetPropertyOrDefault("spanId", JsonDocument.Parse("\"\"").RootElement).GetString() ?? ""
                    })
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing span");
            return null;
        }
    }

    private MetricEntry? ParseMetricEntry(JsonElement metric, string serviceName)
    {
        try
        {
            var name = metric.GetPropertyOrDefault("name", JsonDocument.Parse("\"unknown\"").RootElement).GetString() ?? "unknown";
            var description = metric.GetPropertyOrDefault("description", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            var unit = metric.GetPropertyOrDefault("unit", JsonDocument.Parse("\"\"").RootElement).GetString() ?? "";
            
            var dataPoints = metric.GetPropertyOrDefault("dataPoints", JsonDocument.Parse("[]").RootElement);
            
            // Get the first data point's value
            foreach (var dp in dataPoints.EnumerateArray())
            {
                var value = GetDataPointValue(dp);
                var startTimeUnixMs = dp.GetPropertyOrDefault("startTimeUnixNano", JsonDocument.Parse("0").RootElement).GetUInt64();
                var timeUnixMs = dp.GetPropertyOrDefault("timeUnixNano", JsonDocument.Parse("0").RootElement).GetUInt64();
                
                return new MetricEntry
                {
                    Name = name,
                    Description = description,
                    Unit = unit,
                    Value = value,
                    Timestamp = timeUnixMs > 0 
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(timeUnixMs / 1_000_000)).UtcDateTime
                        : startTimeUnixMs > 0 
                            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(startTimeUnixMs / 1_000_000)).UtcDateTime
                            : DateTime.UtcNow,
                    ServiceName = serviceName
                };
            }
            
            return new MetricEntry
            {
                Name = name,
                Description = description,
                Unit = unit,
                Value = 0,
                Timestamp = DateTime.UtcNow,
                ServiceName = serviceName
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing metric");
            return null;
        }
    }

    private double GetDataPointValue(JsonElement dp)
    {
        // Try asNumber first (OTLP 1.12+)
        if (dp.TryGetProperty("asNumber", out var asNumber))
        {
            return asNumber.GetDouble();
        }
        
        // Try value for sum/gauge
        if (dp.TryGetProperty("value", out var value))
        {
            return value.GetDouble();
        }
        
        // Try explicit value for histogram
        if (dp.TryGetProperty("explicitBucket", out var bucket))
        {
            if (bucket.TryGetProperty("values", out var values) && values.GetArrayLength() > 0)
            {
                // Return sum of histogram values
                double sum = 0;
                foreach (var v in values.EnumerateArray())
                {
                    sum += v.GetDouble();
                }
                return sum;
            }
        }
        
        return 0;
    }

    private static string? GetAttributeValue(JsonElement resource, string key)
    {
        if (resource.TryGetProperty("attributes", out var attrs))
        {
            foreach (var attr in attrs.EnumerateArray())
            {
                if (attr.TryGetProperty("key", out var keyEl) && keyEl.GetString() == key)
                {
                    if (attr.TryGetProperty("value", out var val))
                    {
                        return val.GetPropertyOrDefault("stringValue", 
                            val.GetPropertyOrDefault("intValue", 
                            val.GetPropertyOrDefault("doubleValue", 
                            val.GetPropertyOrDefault("boolValue", JsonDocument.Parse("\"\"").RootElement)))).GetString();
                    }
                }
            }
        }
        return null;
    }

    private static Dictionary<string, string> GetAttributesDict(JsonElement attributes)
    {
        var dict = new Dictionary<string, string>();
        foreach (var attr in attributes.EnumerateArray())
        {
            if (attr.TryGetProperty("key", out var keyEl))
            {
                var key = keyEl.GetString() ?? "";
                if (attr.TryGetProperty("value", out var val))
                {
                    string? value = null;
                    if (val.TryGetProperty("stringValue", out var sv)) value = sv.GetString();
                    else if (val.TryGetProperty("intValue", out var iv)) value = iv.GetInt64().ToString();
                    else if (val.TryGetProperty("doubleValue", out var dv)) value = dv.GetDouble().ToString();
                    else if (val.TryGetProperty("boolValue", out var bv)) value = bv.GetBoolean().ToString();
                    
                    if (value != null)
                    {
                        dict[key] = value;
                    }
                }
            }
        }
        return dict;
    }

    /// <summary>
    /// Helper to get UInt64 from JsonElement that might be either a number or string.
    /// OTLP JSON allows both formats for nanosecond timestamps.
    /// </summary>
    private static ulong GetUInt64FromJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetUInt64();
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (ulong.TryParse(str, out var result))
            {
                return result;
            }
        }
        return 0;
    }

    private static string ConvertHexToBase64(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "";
        try
        {
            var bytes = Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return hex;
        }
    }

    #endregion

    private static ConcurrentQueue<T> GetOrAddQueue<T>(
        ConcurrentDictionary<string, ConcurrentQueue<T>> dict, 
        string key)
    {
        return dict.GetOrAdd(key, _ => new ConcurrentQueue<T>());
    }
}
