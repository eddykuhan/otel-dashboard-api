using Microsoft.AspNetCore.Mvc;
using OtelDashboardApi.Services;

namespace OtelDashboardApi.Controllers;

[ApiController]
[Route("api/v1/health")]
public class HealthController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly WebSocketStreamService _wsService;

    public HealthController(InMemoryStore store, WebSocketStreamService wsService)
    {
        _store = store;
        _wsService = wsService;
    }

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    [HttpGet]
    public IActionResult GetHealth()
    {
        var (logCount, metricCount, traceCount) = _store.GetStats();
        
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            stats = new
            {
                logs = logCount,
                metrics = metricCount,
                traces = traceCount
            },
            websocket = new
            {
                connections = _wsService.GetConnectionCount(),
                subscriptions = _wsService.GetSubscriptionStats()
            }
        });
    }
}
