using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OtelDashboardApi.Services;

namespace OtelDashboardApi.Controllers;

/// <summary>
/// Server-Sent Events controller for fallback one-way streaming.
/// </summary>
[ApiController]
[Route("api/v1/stream")]
public class SseStreamController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly ILogger<SseStreamController> _logger;

    public SseStreamController(InMemoryStore store, ILogger<SseStreamController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// SSE endpoint for log streaming.
    /// </summary>
    [HttpGet("logs")]
    public async Task StreamLogs(
        [FromQuery] string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no"); // Disable nginx buffering

        _logger.LogInformation("SSE log stream started");

        var lastId = 0L;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var newLogs = _store.GetLogsSince(lastId);

                foreach (var log in newLogs)
                {
                    if (!string.IsNullOrEmpty(serviceName) && log.ServiceName != serviceName)
                        continue;

                    var data = JsonSerializer.Serialize(log, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    
                    await Response.WriteAsync($"id: {log.Id}\n", cancellationToken);
                    await Response.WriteAsync($"event: log\n", cancellationToken);
                    await Response.WriteAsync($"data: {data}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                    
                    lastId = log.Id;
                }

                // Heartbeat if no new logs
                if (newLogs.Count == 0)
                {
                    await Response.WriteAsync($": heartbeat\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }

                await Task.Delay(100, cancellationToken); // Poll interval
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE log stream ended");
        }
    }
}
