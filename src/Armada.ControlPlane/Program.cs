using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Armada.ControlPlane.Models;
using Armada.ControlPlane.Services;
using Armada.ControlPlane.Settings;
using Armada.Core;
using Armada.Core.Models;

ControlPlaneSettings settings = new ControlPlaneSettings();
DateTime controlPlaneStartUtc = DateTime.UtcNow;

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
app.UseDefaultFiles();
app.UseStaticFiles();

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
        startedUtc = controlPlaneStartUtc,
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

app.MapGet("/api/v1/instances/{instanceId}/summary", async (string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(registry, instanceId, "armada.instance.summary", null, token).ConfigureAwait(false);
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

app.MapGet("/api/v1/instances/{instanceId}/activity", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 20, 1, 100);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.activity.recent",
        new RemoteTunnelQueryRequest { Limit = limit },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/missions/recent", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 10, 1, 100);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.missions.recent",
        new RemoteTunnelQueryRequest { Limit = limit },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/voyages/recent", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 10, 1, 100);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.voyages.recent",
        new RemoteTunnelQueryRequest { Limit = limit },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/captains/recent", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 10, 1, 100);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.captains.recent",
        new RemoteTunnelQueryRequest { Limit = limit },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/missions/{missionId}", async (string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.detail",
        new RemoteTunnelQueryRequest { MissionId = missionId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/missions/{missionId}/log", async (HttpContext context, string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    int offset = ParsePositiveInt(context.Request.Query["offset"], 0, 0, Int32.MaxValue);
    int lines = ParsePositiveInt(context.Request.Query["lines"], 200, 1, 2000);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.log",
        new RemoteTunnelQueryRequest { MissionId = missionId, Offset = offset, Lines = lines },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/missions/{missionId}/diff", async (string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.diff",
        new RemoteTunnelQueryRequest { MissionId = missionId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/voyages/{voyageId}", async (string instanceId, string voyageId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.voyage.detail",
        new RemoteTunnelQueryRequest { VoyageId = voyageId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/captains/{captainId}", async (string instanceId, string captainId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.captain.detail",
        new RemoteTunnelQueryRequest { CaptainId = captainId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/captains/{captainId}/log", async (HttpContext context, string instanceId, string captainId, InstanceRegistry registry, CancellationToken token) =>
{
    int offset = ParsePositiveInt(context.Request.Query["offset"], 0, 0, Int32.MaxValue);
    int lines = ParsePositiveInt(context.Request.Query["lines"], 50, 1, 1000);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.captain.log",
        new RemoteTunnelQueryRequest { CaptainId = captainId, Offset = offset, Lines = lines },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/fleets", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 12, 1, 200);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.fleets.list",
        new RemoteTunnelQueryRequest { Limit = limit },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/fleets/{fleetId}", async (string instanceId, string fleetId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.fleet.detail",
        new RemoteTunnelQueryRequest { FleetId = fleetId },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/fleets", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement payload = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(registry, instanceId, "armada.fleet.create", payload, token).ConfigureAwait(false);
});

app.MapPut("/api/v1/instances/{instanceId}/fleets/{fleetId}", async (HttpContext context, string instanceId, string fleetId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement fleet = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.fleet.update",
        new
        {
            fleetId = fleetId,
            fleet = fleet
        },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/vessels", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 12, 1, 200);
    string? fleetId = context.Request.Query["fleetId"];
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.vessels.list",
        new RemoteTunnelQueryRequest { Limit = limit, FleetId = fleetId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/vessels/{vesselId}", async (string instanceId, string vesselId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.vessel.detail",
        new RemoteTunnelQueryRequest { VesselId = vesselId },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/vessels", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement payload = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(registry, instanceId, "armada.vessel.create", payload, token).ConfigureAwait(false);
});

app.MapPut("/api/v1/instances/{instanceId}/vessels/{vesselId}", async (HttpContext context, string instanceId, string vesselId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement vessel = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.vessel.update",
        new
        {
            vesselId = vesselId,
            vessel = vessel
        },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/voyages", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 12, 1, 200);
    string? status = context.Request.Query["status"];
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.voyages.list",
        new RemoteTunnelQueryRequest { Limit = limit, Status = status },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/voyages/dispatch", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement payload = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(registry, instanceId, "armada.voyage.dispatch", payload, token).ConfigureAwait(false);
});

app.MapDelete("/api/v1/instances/{instanceId}/voyages/{voyageId}", async (string instanceId, string voyageId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.voyage.cancel",
        new RemoteTunnelQueryRequest { VoyageId = voyageId },
        token).ConfigureAwait(false);
});

app.MapGet("/api/v1/instances/{instanceId}/missions", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    int limit = ParsePositiveInt(context.Request.Query["limit"], 16, 1, 200);
    string? status = context.Request.Query["status"];
    string? voyageId = context.Request.Query["voyageId"];
    string? vesselId = context.Request.Query["vesselId"];
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.missions.list",
        new RemoteTunnelQueryRequest { Limit = limit, Status = status, VoyageId = voyageId, VesselId = vesselId },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/missions", async (HttpContext context, string instanceId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement payload = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(registry, instanceId, "armada.mission.create", payload, token).ConfigureAwait(false);
});

app.MapPut("/api/v1/instances/{instanceId}/missions/{missionId}", async (HttpContext context, string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement mission = await ReadJsonBodyAsync(context).ConfigureAwait(false);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.update",
        new
        {
            missionId = missionId,
            mission = mission
        },
        token).ConfigureAwait(false);
});

app.MapDelete("/api/v1/instances/{instanceId}/missions/{missionId}", async (string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.cancel",
        new RemoteTunnelQueryRequest { MissionId = missionId },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/missions/{missionId}/restart", async (HttpContext context, string instanceId, string missionId, InstanceRegistry registry, CancellationToken token) =>
{
    JsonElement payload = await ReadJsonBodyAsync(context, allowEmpty: true).ConfigureAwait(false);
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.mission.restart",
        new
        {
            missionId = missionId,
            title = GetOptionalProperty(payload, "title"),
            description = GetOptionalProperty(payload, "description")
        },
        token).ConfigureAwait(false);
});

app.MapPost("/api/v1/instances/{instanceId}/captains/{captainId}/stop", async (string instanceId, string captainId, InstanceRegistry registry, CancellationToken token) =>
{
    return await ForwardPayloadAsync(
        registry,
        instanceId,
        "armada.captain.stop",
        new RemoteTunnelQueryRequest { CaptainId = captainId },
        token).ConfigureAwait(false);
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
                            "instances.shell.summary",
                            "instances.fleets.list",
                            "instances.fleet.detail",
                            "instances.fleet.create",
                            "instances.fleet.update",
                            "instances.vessels.list",
                            "instances.vessel.detail",
                            "instances.vessel.create",
                            "instances.vessel.update",
                            "instances.activity",
                            "instances.missions.list",
                            "instances.missions.recent",
                            "instances.voyages.list",
                            "instances.voyages.recent",
                            "instances.captains.recent",
                            "instances.mission.detail",
                            "instances.mission.log",
                            "instances.mission.diff",
                            "instances.mission.create",
                            "instances.mission.update",
                            "instances.mission.cancel",
                            "instances.mission.restart",
                            "instances.voyage.detail",
                            "instances.voyage.dispatch",
                            "instances.voyage.cancel",
                            "instances.captain.detail",
                            "instances.captain.log",
                            "instances.captain.stop",
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

static async Task<IResult> ForwardPayloadAsync(InstanceRegistry registry, string instanceId, string method, object? payload, CancellationToken token)
{
    try
    {
        RemoteTunnelEnvelope response = await registry.SendRequestAsync(instanceId, method, payload, token).ConfigureAwait(false);
        return BuildForwardedPayloadResult(response);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static IResult BuildForwardedPayloadResult(RemoteTunnelEnvelope response)
{
    object? payload = null;
    if (response.Payload.HasValue)
    {
        payload = JsonSerializer.Deserialize<object>(response.Payload.Value.GetRawText(), RemoteTunnelProtocol.JsonOptions);
    }

    int statusCode = response.StatusCode ?? (response.Success == false ? 502 : 200);
    if (statusCode >= 200 && statusCode < 300 && String.IsNullOrWhiteSpace(response.ErrorCode))
    {
        return Results.Json(payload ?? new { });
    }

    return Results.Json(new
    {
        error = response.Message ?? "Tunnel request failed.",
        errorCode = response.ErrorCode,
        correlationId = response.CorrelationId,
        payload = payload
    }, statusCode: statusCode);
}

static int ParsePositiveInt(string? rawValue, int defaultValue, int minimum, int maximum)
{
    if (!Int32.TryParse(rawValue, out int parsed))
    {
        parsed = defaultValue;
    }

    if (parsed < minimum) parsed = minimum;
    if (parsed > maximum) parsed = maximum;
    return parsed;
}

static async Task<JsonElement> ReadJsonBodyAsync(HttpContext context, bool allowEmpty = false)
{
    using StreamReader reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    string body = await reader.ReadToEndAsync().ConfigureAwait(false);
    if (String.IsNullOrWhiteSpace(body))
    {
        return JsonDocument.Parse("{}").RootElement.Clone();
    }

    return JsonDocument.Parse(body).RootElement.Clone();
}

static string? GetOptionalProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind != JsonValueKind.Object)
    {
        return null;
    }

    foreach (JsonProperty property in element.EnumerateObject())
    {
        if (String.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
        {
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
        }
    }

    return null;
}
