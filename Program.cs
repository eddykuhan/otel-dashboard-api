using System.Text.Json.Serialization;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OtelDashboardApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() 
    { 
        Title = "OTEL Dashboard API", 
        Version = "v1",
        Description = "High-performance OpenTelemetry dashboard API with gRPC and HTTP OTLP receivers, plus WebSocket streaming. Compatible with .NET Aspire Dashboard protocol."
    });
});

// Register custom services as singletons for shared state
builder.Services.AddSingleton<InMemoryStore>();
builder.Services.AddSingleton<WebSocketStreamService>();

// Configure gRPC for OTLP receiver (Traces, Metrics, Logs)
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 64 * 1024 * 1024; // 64MB for large batches
    options.MaxSendMessageSize = 16 * 1024 * 1024; // 16MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// Configure Kestrel to support both HTTP/1.1 (for REST/JSON) and HTTP/2 (for gRPC)
builder.WebHost.ConfigureKestrel(options =>
{
    // Primary endpoint: HTTP/1.1 + HTTP/2 on port 5003
    // Supports: REST API, OTLP HTTP/JSON, gRPC (without TLS via HTTP/2 prior knowledge)
    options.ListenLocalhost(5003, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    
    // Alternative: HTTP/2 only on port 4317 (standard OTLP gRPC port)
    options.ListenLocalhost(4317, o => o.Protocols = HttpProtocols.Http2);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:4200", "http://localhost:5200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");

// Enable gRPC services for OTLP receiver (Traces, Metrics, Logs)
app.MapGrpcService<OtlpTraceGrpcService>();
app.MapGrpcService<OtlpMetricsGrpcService>();
app.MapGrpcService<OtlpLogsGrpcService>();

// OTLP HTTP/JSON endpoint for traces
app.MapPost("/v1/traces", async (HttpContext context) =>
{
    try
    {
        var inMemoryStore = context.RequestServices.GetRequiredService<InMemoryStore>();
        var wsService = context.RequestServices.GetRequiredService<WebSocketStreamService>();
        
        // Read the request body as JSON
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        
        // Parse the JSON and add to in-memory store
        await inMemoryStore.AddTracesJsonAsync(json, context.RequestAborted);
        
        // Return standard OTLP response
        return Results.Ok(new { partialSuccess = (object?)null });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error processing OTLP traces");
        return Results.Json(new { partialSuccess = new { rejectedSpans = 1, errorMessage = ex.Message } }, statusCode: 200);
    }
});

// OTLP HTTP/JSON endpoint for metrics
app.MapPost("/v1/metrics", async (HttpContext context) =>
{
    try
    {
        var inMemoryStore = context.RequestServices.GetRequiredService<InMemoryStore>();
        var wsService = context.RequestServices.GetRequiredService<WebSocketStreamService>();
        
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        
        await inMemoryStore.AddMetricsJsonAsync(json, context.RequestAborted);
        
        return Results.Ok(new { partialSuccess = (object?)null });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error processing OTLP metrics");
        return Results.Json(new { partialSuccess = new { rejectedDataPoints = 1, errorMessage = ex.Message } }, statusCode: 200);
    }
});

// OTLP HTTP/JSON endpoint for logs
app.MapPost("/v1/logs", async (HttpContext context) =>
{
    try
    {
        var inMemoryStore = context.RequestServices.GetRequiredService<InMemoryStore>();
        var wsService = context.RequestServices.GetRequiredService<WebSocketStreamService>();
        
        using var reader = new StreamReader(context.Request.Body);
        var json = await reader.ReadToEndAsync();
        
        await inMemoryStore.AddLogsJsonAsync(json, context.RequestAborted);
        
        return Results.Ok(new { partialSuccess = (object?)null });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error processing OTLP logs");
        return Results.Json(new { partialSuccess = new { rejectedLogRecords = 1, errorMessage = ex.Message } }, statusCode: 200);
    }
});

// WebSocket middleware
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// WebSocket endpoint
app.Map("/ws/stream", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket requests only");
        return;
    }

    var wsService = context.RequestServices.GetRequiredService<WebSocketStreamService>();
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid().ToString();
    
    await wsService.HandleConnectionAsync(webSocket, connectionId, context.RequestAborted);
});

app.UseAuthorization();
app.MapControllers();

// Log startup info
app.Logger.LogInformation("=== OTEL Dashboard API Started ===");
app.Logger.LogInformation("");
app.Logger.LogInformation("OTLP Endpoints (configure your apps/collectors to send here):");
app.Logger.LogInformation("  gRPC: http://localhost:4317 (standard OTLP port)");
app.Logger.LogInformation("  gRPC: http://localhost:5003 (alternative)");
app.Logger.LogInformation("  HTTP: http://localhost:5003/v1/traces");
app.Logger.LogInformation("  HTTP: http://localhost:5003/v1/metrics");
app.Logger.LogInformation("  HTTP: http://localhost:5003/v1/logs");
app.Logger.LogInformation("");
app.Logger.LogInformation("Dashboard Endpoints:");
app.Logger.LogInformation("  REST API: http://localhost:5003/api/v1/*");
app.Logger.LogInformation("  WebSocket: ws://localhost:5003/ws/stream");
app.Logger.LogInformation("  Swagger: http://localhost:5003/swagger");
app.Logger.LogInformation("");

app.Run();
