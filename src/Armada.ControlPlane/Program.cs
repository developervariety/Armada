using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Armada.ControlPlane.Models;
using Armada.ControlPlane.Services;
using Armada.ControlPlane.Settings;
using Armada.Core;
using Armada.Core.Models;

ControlPlaneSettings settings = new ControlPlaneSettings();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.GetSection("ArmadaControlPlane").Bind(settings);
builder.WebHost.UseUrls("http://" + settings.Hostname + ":" + settings.Port);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<InstanceRegistry>();

WebApplication app = builder.Build();
app.UseWebSockets();

app.MapGet("/api/v1/status/health", (InstanceRegistry registry) =>
{
    List<RemoteInstanceSummary> instances = registry.ListSummaries();
    return Results.Ok(new
    {
        healthy = true,
        product = "Armada.ControlPlane",
        version = Constants.ProductVersion,
        protocolVersion = Constants.RemoteTunnelProtocolVersion,
        port = settings.Port,
        startedUtc = DateTime.UtcNow,
        instances = new
        {
            total = instances.Count,
            connected = instances.Count(instance => String.Equals(instance.State, "connected", StringComparison.OrdinalIgnoreCase)),
            stale = instances.Count(instance => String.Equals(instance.State, "stale", StringComparison.OrdinalIgnoreCase)),
            offline = instances.Count(instance => String.Equals(instance.State, "offline", StringComparison.OrdinalIgnoreCase))
        }
    });
});

app.MapGet("/api/v1/instances", (InstanceRegistry registry) =>
{
    List<RemoteInstanceSummary> instances = registry.ListSummaries();
    return Results.Ok(new
    {
        count = instances.Count,
        instances = instances
    });
});

app.MapGet("/api/v1/instances/{instanceId}", (string instanceId, InstanceRegistry registry) =>
{
    RemoteInstanceRecord? record = registry.GetRecord(instanceId);
    if (record == null)
    {
        return Results.NotFound(new { error = "Instance not found." });
    }

    return Results.Ok(new
    {
        summary = record.ToSummary(DateTime.UtcNow, settings.StaleAfterSeconds),
        recentEvents = record.GetRecentEvents()
    });
});

app.MapGet("/api/v1/instances/{instanceId}/status/snapshot", async (string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    try
    {
        RemoteTunnelEnvelope response = await registry.SendRequestAsync(instanceId, "armada.status.snapshot", null, token).ConfigureAwait(false);
        return Results.Ok(BuildTunnelProxyResponse(response));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/v1/instances/{instanceId}/health", async (string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    try
    {
        RemoteTunnelEnvelope response = await registry.SendRequestAsync(instanceId, "armada.status.health", null, token).ConfigureAwait(false);
        return Results.Ok(BuildTunnelProxyResponse(response));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Map("/tunnel", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Expected a websocket upgrade request." }).ConfigureAwait(false);
        return;
    }

    InstanceRegistry registry = context.RequestServices.GetRequiredService<InstanceRegistry>();
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    string? instanceId = null;
    SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    try
    {
        using CancellationTokenSource handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.HandshakeTimeoutSeconds));
        RemoteTunnelEnvelope firstEnvelope = await ReceiveEnvelopeAsync(socket, handshakeTimeout.Token).ConfigureAwait(false);

        if (!String.Equals(firstEnvelope.Type, "request", StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(firstEnvelope.Method, "armada.tunnel.handshake", StringComparison.OrdinalIgnoreCase))
        {
            await SendEnvelopeAsync(
                socket,
                RemoteTunnelProtocol.CreateError(firstEnvelope.CorrelationId, "invalid_handshake", "First tunnel message must be armada.tunnel.handshake.", 400),
                CancellationToken.None,
                sendLock).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Handshake required", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        RemoteTunnelHandshakePayload? handshake = firstEnvelope.Payload?.Deserialize<RemoteTunnelHandshakePayload>(RemoteTunnelProtocol.JsonOptions);
        if (!registry.TryValidateHandshake(handshake, out string? handshakeError))
        {
            await SendEnvelopeAsync(
                socket,
                RemoteTunnelProtocol.CreateResponse(
                    firstEnvelope.CorrelationId,
                    new RemoteTunnelRequestResult
                    {
                        StatusCode = 401,
                        ErrorCode = "handshake_rejected",
                        Message = handshakeError
                    }),
                CancellationToken.None,
                sendLock).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, handshakeError ?? "Handshake rejected", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        RemoteInstanceSession session = new RemoteInstanceSession((envelope, token) => SendEnvelopeAsync(socket, envelope, token, sendLock));
        instanceId = handshake!.InstanceId!.Trim();
        registry.RegisterHandshake(handshake, context.Connection.RemoteIpAddress?.ToString(), session);

        await SendEnvelopeAsync(
            socket,
            RemoteTunnelProtocol.CreateResponse(
                firstEnvelope.CorrelationId,
                new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new RemoteTunnelHandshakeResponse
                    {
                        Accepted = true,
                        ControlPlaneVersion = Constants.ProductVersion,
                        ProtocolVersion = Constants.RemoteTunnelProtocolVersion,
                        InstanceId = instanceId,
                        Message = "Handshake accepted.",
                        Capabilities = new List<string>
                        {
                            "instances.summary",
                            "instances.detail",
                            "armada.status.snapshot",
                            "armada.status.health"
                        }
                    },
                    Message = "Handshake accepted."
                }),
            CancellationToken.None,
            sendLock).ConfigureAwait(false);

        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            RemoteTunnelEnvelope envelope = await ReceiveEnvelopeAsync(socket, context.RequestAborted).ConfigureAwait(false);
            registry.MarkSeen(instanceId);

            if (String.Equals(envelope.Type, "ping", StringComparison.OrdinalIgnoreCase))
            {
                await SendEnvelopeAsync(socket, RemoteTunnelProtocol.CreatePong(envelope.CorrelationId), context.RequestAborted, sendLock).ConfigureAwait(false);
                continue;
            }

            if (String.Equals(envelope.Type, "response", StringComparison.OrdinalIgnoreCase))
            {
                registry.TryCompleteResponse(instanceId, envelope);
                continue;
            }

            if (String.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(envelope.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                registry.RecordEvent(instanceId, envelope);
                continue;
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException)
    {
    }
    catch (JsonException ex)
    {
        if (socket.State == WebSocketState.Open)
        {
            await SendEnvelopeAsync(socket, RemoteTunnelProtocol.CreateError(null, "invalid_json", ex.Message, 400), CancellationToken.None, sendLock).ConfigureAwait(false);
        }
    }
    finally
    {
        if (!String.IsNullOrWhiteSpace(instanceId))
        {
            registry.MarkDisconnected(instanceId);
        }

        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tunnel closed", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
});

await app.RunAsync().ConfigureAwait(false);

static async Task<RemoteTunnelEnvelope> ReceiveEnvelopeAsync(WebSocket socket, CancellationToken token)
{
    byte[] buffer = new byte[8192];
    using MemoryStream ms = new MemoryStream();
    WebSocketReceiveResult result;

    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException("Tunnel closed by remote peer.");
        }

        if (result.Count > 0)
        {
            ms.Write(buffer, 0, result.Count);
        }
    }
    while (!result.EndOfMessage);

    if (result.MessageType != WebSocketMessageType.Text)
    {
        throw new InvalidOperationException("Only text websocket messages are supported.");
    }

    string json = Encoding.UTF8.GetString(ms.ToArray());
    return JsonSerializer.Deserialize<RemoteTunnelEnvelope>(json, RemoteTunnelProtocol.JsonOptions)
        ?? throw new JsonException("Tunnel envelope could not be deserialized.");
}

static async Task SendEnvelopeAsync(WebSocket socket, RemoteTunnelEnvelope envelope, CancellationToken token, SemaphoreSlim? sendLock = null)
{
    string json = JsonSerializer.Serialize(envelope, RemoteTunnelProtocol.JsonOptions);
    byte[] data = Encoding.UTF8.GetBytes(json);

    if (sendLock != null)
    {
        await sendLock.WaitAsync(token).ConfigureAwait(false);
    }

    try
    {
        await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }
    finally
    {
        sendLock?.Release();
    }
}

static object BuildTunnelProxyResponse(RemoteTunnelEnvelope response)
{
    object? payload = null;
    if (response.Payload.HasValue)
    {
        payload = JsonSerializer.Deserialize<object>(response.Payload.Value.GetRawText(), RemoteTunnelProtocol.JsonOptions);
    }

    return new
    {
        correlationId = response.CorrelationId,
        success = response.Success,
        statusCode = response.StatusCode,
        errorCode = response.ErrorCode,
        message = response.Message,
        payload = payload
    };
}
