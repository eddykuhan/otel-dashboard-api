using Microsoft.AspNetCore.Mvc;
using OtelDashboardApi.Models;
using OtelDashboardApi.Services;

namespace OtelDashboardApi.Controllers;

[ApiController]
[Route("api/v1/traces")]
public class TracesController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<TracesController> _logger;

    public TracesController(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<TracesController> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    /// <summary>
    /// Ingest trace spans from OTEL collector.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> IngestSpans([FromBody] List<TraceSpan> spans)
    {
        var count = 0;
        foreach (var span in spans)
        {
            _store.AddSpan(span);
            count++;
            
            await _wsService.BroadcastAsync("traces", span);
        }

        _logger.LogInformation("Ingested {Count} spans", count);
        
        return Ok(new { success = true, receivedCount = count });
    }

    /// <summary>
    /// Query traces with optional filters.
    /// </summary>
    [HttpGet]
    public IActionResult QueryTraces(
        [FromQuery] string? serviceName = null,
        [FromQuery] long? minDuration = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] int limit = 50)
    {
        var traces = _store.QueryTraces(serviceName, minDuration, startTime, endTime, limit);
        
        var result = traces.Select(t => new
        {
            t.TraceId,
            RootSpan = t.RootSpan
        });
        
        return Ok(result);
    }

    /// <summary>
    /// Get full trace details by trace ID.
    /// </summary>
    [HttpGet("{traceId}")]
    public IActionResult GetTrace(string traceId)
    {
        var spans = _store.GetTrace(traceId);
        
        if (spans == null || spans.Count == 0)
        {
            return NotFound(new { error = "Trace not found" });
        }
        
        return Ok(new
        {
            traceId,
            spans,
            spanCount = spans.Count,
            duration = spans.Max(s => s.EndTime) - spans.Min(s => s.StartTime)
        });
    }
}
