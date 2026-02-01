using Microsoft.AspNetCore.Mvc;
using OtelDashboardApi.Models;
using OtelDashboardApi.Services;

namespace OtelDashboardApi.Controllers;

[ApiController]
[Route("api/v1/metrics")]
public class MetricsController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<MetricsController> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    /// <summary>
    /// Ingest metric entries from OTEL collector.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> IngestMetrics([FromBody] List<MetricEntry> metrics)
    {
        var count = 0;
        foreach (var metric in metrics)
        {
            _store.AddMetric(metric);
            count++;
            
            await _wsService.BroadcastAsync("metrics", metric);
        }

        _logger.LogInformation("Ingested {Count} metrics", count);
        
        return Ok(new { success = true, receivedCount = count });
    }

    /// <summary>
    /// Query metrics with optional filters.
    /// </summary>
    [HttpGet]
    public IActionResult QueryMetrics(
        [FromQuery] string? name = null,
        [FromQuery] string? serviceName = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] int limit = 100)
    {
        var metrics = _store.QueryMetrics(name, serviceName, startTime, endTime, limit);
        return Ok(metrics);
    }
}
