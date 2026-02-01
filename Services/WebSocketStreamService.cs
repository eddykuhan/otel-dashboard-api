using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OtelDashboardApi.Models;

namespace OtelDashboardApi.Services;

/// <summary>
/// High-performance WebSocket streaming service with:
/// - Parallel broadcasting using Task.WhenAll
/// - Backpressure handling with bounded channels
/// - Per-connection message queues
/// </summary>
public class WebSocketStreamService
{
    private readonly ILogger<WebSocketStreamService> _logger;
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
    
    private class ConnectionState
    {
        public required WebSocket Socket { get; init; }
        public required Channel<object> MessageQueue { get; init; }
        public HashSet<string> Subscriptions { get; } = new();
        public CancellationTokenSource CancellationTokenSource { get; } = new();
    }

    public WebSocketStreamService(ILogger<WebSocketStreamService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Handles a new WebSocket connection with backpressure support.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket socket, string connectionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("WebSocket connected: {ConnectionId}", connectionId);
        
        // Create bounded channel for backpressure - drops oldest if full
        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var state = new ConnectionState
        {
            Socket = socket,
            MessageQueue = channel
        };

        _connections[connectionId] = state;

        try
        {
            // Start sender and receiver tasks
            var senderTask = ProcessOutgoingMessagesAsync(connectionId, state, cancellationToken);
            var receiverTask = ProcessIncomingMessagesAsync(connectionId, state, cancellationToken);

            // Wait for either to complete (disconnect or error)
            await Task.WhenAny(senderTask, receiverTask);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for {ConnectionId}", connectionId);
        }
        finally
        {
            state.CancellationTokenSource.Cancel();
            _connections.TryRemove(connectionId, out _);
            
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            
            _logger.LogInformation("WebSocket disconnected: {ConnectionId}", connectionId);
        }
    }

    /// <summary>
    /// Broadcasts a message to all subscribed connections in parallel.
    /// </summary>
    public async Task BroadcastAsync<T>(string channel, T data, CancellationToken cancellationToken = default)
    {
        var message = new WebSocketMessage<T>
        {
            Type = "data",
            Channel = channel,
            Payload = data,
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        // Get all subscribed connections
        var subscribedConnections = _connections
            .Where(kvp => kvp.Value.Subscriptions.Contains(channel))
            .Where(kvp => kvp.Value.Socket.State == WebSocketState.Open)
            .ToList();

        if (subscribedConnections.Count == 0)
            return;

        // Parallel send using Task.WhenAll for performance
        var sendTasks = subscribedConnections.Select(async kvp =>
        {
            try
            {
                // Queue the message (non-blocking with backpressure)
                if (!kvp.Value.MessageQueue.Writer.TryWrite(message))
                {
                    _logger.LogDebug("Message dropped for {ConnectionId} due to backpressure", kvp.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to queue message for {ConnectionId}", kvp.Key);
            }
        });

        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// Processes outgoing messages from the queue to the WebSocket.
    /// </summary>
    private async Task ProcessOutgoingMessagesAsync(string connectionId, ConnectionState state, CancellationToken cancellationToken)
    {
        var reader = state.MessageQueue.Reader;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, 
            state.CancellationTokenSource.Token);

        try
        {
            await foreach (var message in reader.ReadAllAsync(linkedCts.Token))
            {
                if (state.Socket.State != WebSocketState.Open)
                    break;

                var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                var bytes = Encoding.UTF8.GetBytes(json);

                await state.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect
        }
    }

    /// <summary>
    /// Processes incoming messages (subscriptions) from the WebSocket.
    /// </summary>
    private async Task ProcessIncomingMessagesAsync(string connectionId, ConnectionState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, 
            state.CancellationTokenSource.Token);

        try
        {
            while (state.Socket.State == WebSocketState.Open)
            {
                var result = await state.Socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), 
                    linkedCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await state.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, 
                        null, 
                        CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleSubscriptionMessage(connectionId, state, json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect
        }
    }

    private Task HandleSubscriptionMessage(string connectionId, ConnectionState state, string json)
    {
        try
        {
            var subscription = JsonSerializer.Deserialize<WebSocketSubscription>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (subscription == null) return Task.CompletedTask;

            switch (subscription.Type.ToLower())
            {
                case "subscribe":
                    state.Subscriptions.Add(subscription.Channel);
                    _logger.LogInformation("{ConnectionId} subscribed to {Channel}", connectionId, subscription.Channel);
                    break;
                case "unsubscribe":
                    state.Subscriptions.Remove(subscription.Channel);
                    _logger.LogInformation("{ConnectionId} unsubscribed from {Channel}", connectionId, subscription.Channel);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid subscription message from {ConnectionId}", connectionId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the count of active connections.
    /// </summary>
    public int GetConnectionCount() => _connections.Count;

    /// <summary>
    /// Gets subscription stats.
    /// </summary>
    public Dictionary<string, int> GetSubscriptionStats()
    {
        var stats = new Dictionary<string, int>();
        foreach (var conn in _connections.Values)
        {
            foreach (var sub in conn.Subscriptions)
            {
                stats.TryGetValue(sub, out var count);
                stats[sub] = count + 1;
            }
        }
        return stats;
    }
}
