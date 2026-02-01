using Microsoft.AspNetCore.Mvc;
using OtelDashboardApi.Models;
using OtelDashboardApi.Services;

namespace OtelDashboardApi.Controllers;

[ApiController]
[Route("api/v1/logs")]
public class LogsController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;
    private readonly ILogger<LogsController> _logger;

    public LogsController(
        InMemoryStore store,
        WebSocketStreamService wsService,
        ILogger<LogsController> logger)
    {
        _store = store;
        _wsService = wsService;
        _logger = logger;
    }

    /// <summary>
    /// Ingest log entries from OTEL collector.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> IngestLogs([FromBody] List<LogEntry> logs)
    {
        var count = 0;
        foreach (var log in logs)
        {
            _store.AddLog(log);
            count++;
            
            // Broadcast to WebSocket subscribers
            await _wsService.BroadcastAsync("logs", log);
        }

        _logger.LogInformation("Ingested {Count} logs", count);
        
        return Ok(new { success = true, receivedCount = count });
    }

    /// <summary>
    /// Query logs with optional filters.
    /// </summary>
    [HttpGet]
    public IActionResult QueryLogs(
        [FromQuery] string? serviceName = null,
        [FromQuery] string? level = null,
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] string? traceId = null,
        [FromQuery] int limit = 100)
    {
        Models.LogLevel? logLevel = null;
        if (!string.IsNullOrEmpty(level) && Enum.TryParse<Models.LogLevel>(level, true, out var parsed))
        {
            logLevel = parsed;
        }

        var logs = _store.QueryLogs(serviceName, logLevel, startTime, endTime, traceId, limit);
        return Ok(logs);
    }
}
