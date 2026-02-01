using Google.Protobuf;
using OpenTelemetry.Proto.Common;
using OpenTelemetry.Proto.Collector.Trace;
using OpenTelemetry.Proto.Collector.Logs;
using ProtoSpanKind = OpenTelemetry.Proto.Collector.Trace.SpanKind;
using ProtoStatusCode = OpenTelemetry.Proto.Collector.Trace.StatusCode;

namespace OtelDashboardApi.Services;

/// <summary>
/// Helper utilities for OTLP data conversion.
/// </summary>
public static class OtlpHelpers
{
    /// <summary>
    /// Converts bytes to hex string (standard OTLP format for trace/span IDs).
    /// </summary>
    public static string BytesToHex(ByteString? bytes)
    {
        if (bytes == null || bytes.IsEmpty)
            return string.Empty;
        
        return Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the service name from OTLP Resource attributes.
    /// </summary>
    public static string GetServiceName(Resource? resource)
    {
        if (resource == null)
            return "unknown";

        return GetResourceAttribute(resource, "service.name") ?? "unknown";
    }

    /// <summary>
    /// Gets a resource attribute value by key.
    /// </summary>
    public static string? GetResourceAttribute(Resource resource, string key)
    {
        foreach (var attr in resource.Attributes)
        {
            if (attr.Key == key)
            {
                return GetAnyValueAsString(attr.Value);
            }
        }
        return null;
    }

    /// <summary>
    /// Converts AnyValue to string representation.
    /// </summary>
    public static string? GetAnyValueAsString(AnyValue? value)
    {
        if (value == null) return null;

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(),
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(),
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue.ToString(),
            AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
            AnyValue.ValueOneofCase.ArrayValue => string.Join(", ", value.ArrayValue.Values.Select(GetAnyValueAsString)),
            AnyValue.ValueOneofCase.KvlistValue => string.Join(", ", value.KvlistValue.Values.Select(kv => $"{kv.Key}={GetAnyValueAsString(kv.Value)}")),
            _ => null
        };
    }

    /// <summary>
    /// Converts AnyValue to object for attributes dictionary.
    /// </summary>
    public static object GetAnyValueAsObject(AnyValue? value)
    {
        if (value == null) return string.Empty;

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
            AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(GetAnyValueAsObject).ToList(),
            AnyValue.ValueOneofCase.KvlistValue => value.KvlistValue.Values.ToDictionary(kv => kv.Key, kv => GetAnyValueAsObject(kv.Value)),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Converts OTLP KeyValue list to dictionary.
    /// </summary>
    public static Dictionary<string, object> ToAttributesDict(IEnumerable<KeyValue> attributes)
    {
        return attributes.ToDictionary(
            a => a.Key,
            a => GetAnyValueAsObject(a.Value)
        );
    }

    /// <summary>
    /// Converts OTLP KeyValue list to string dictionary.
    /// </summary>
    public static Dictionary<string, string> ToStringAttributesDict(IEnumerable<KeyValue> attributes)
    {
        return attributes.ToDictionary(
            a => a.Key,
            a => GetAnyValueAsString(a.Value) ?? string.Empty
        );
    }

    /// <summary>
    /// Converts Unix nanoseconds to DateTime.
    /// </summary>
    public static DateTime NanosToDateTime(ulong nanos)
    {
        if (nanos == 0) return DateTime.UtcNow;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(nanos / 1_000_000)).UtcDateTime;
    }

    /// <summary>
    /// Maps OTLP severity number to our LogLevel.
    /// </summary>
    public static Models.LogLevel MapSeverity(SeverityNumber severity)
    {
        // SeverityNumber values: TRACE=1-4, DEBUG=5-8, INFO=9-12, WARN=13-16, ERROR=17-20, FATAL=21-24
        var severityInt = (int)severity;
        return severityInt switch
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

    /// <summary>
    /// Maps OTLP SpanKind to our SpanKind.
    /// </summary>
    public static Models.SpanKind MapSpanKind(ProtoSpanKind kind)
    {
        return kind switch
        {
            ProtoSpanKind.Server => Models.SpanKind.Server,
            ProtoSpanKind.Client => Models.SpanKind.Client,
            ProtoSpanKind.Producer => Models.SpanKind.Producer,
            ProtoSpanKind.Consumer => Models.SpanKind.Consumer,
            ProtoSpanKind.Internal => Models.SpanKind.Internal,
            _ => Models.SpanKind.Internal
        };
    }

    /// <summary>
    /// Maps OTLP StatusCode to our SpanStatus.
    /// </summary>
    public static Models.SpanStatus MapSpanStatus(ProtoStatusCode code)
    {
        return code switch
        {
            ProtoStatusCode.Ok => Models.SpanStatus.Ok,
            ProtoStatusCode.Error => Models.SpanStatus.Error,
            _ => Models.SpanStatus.Unset
        };
    }

    /// <summary>
    /// Gets the message body from AnyValue (for logs).
    /// </summary>
    public static string GetLogMessage(AnyValue? body)
    {
        if (body == null) return string.Empty;

        return body.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => body.StringValue,
            AnyValue.ValueOneofCase.ArrayValue => string.Join(" ", body.ArrayValue.Values.Select(GetAnyValueAsString)),
            AnyValue.ValueOneofCase.KvlistValue => string.Join(", ", body.KvlistValue.Values.Select(kv => $"{kv.Key}={GetAnyValueAsString(kv.Value)}")),
            _ => GetAnyValueAsString(body) ?? string.Empty
        };
    }
}
