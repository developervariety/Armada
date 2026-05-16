namespace Armada.Proxy
{
    using System.Net.WebSockets;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Proxy.Models;
    using Armada.Proxy.Services;
    using Armada.Proxy.Settings;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using WatsonWebserver.Core.WebSockets;

    /// <summary>
    /// Watson-based remote proxy host for Armada remote management.
    /// </summary>
    public class ArmadaProxyServer : IDisposable
    {
        /// <summary>
        /// Callback invoked when the proxy host is stopping.
        /// </summary>
        public Action? OnStopping { get; set; }

        private readonly string _Header = "[ArmadaProxyServer] ";
        private readonly LoggingModule _Logging;
        private readonly ProxySettings _Settings;
        private readonly bool _Quiet;
        private readonly InstanceRegistry _Registry;
        private readonly ProxyAuthService _Auth;
        private readonly string _WwwrootDirectory;
        private readonly DateTime _StartUtc = DateTime.UtcNow;

        private Webserver _Server = null!;
        private bool _Started = false;
        private bool _Disposed = false;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ArmadaProxyServer(LoggingModule logging, ProxySettings settings, bool quiet = false)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Quiet = quiet;
            _Registry = new InstanceRegistry(_Settings);
            _Auth = new ProxyAuthService(_Settings);
            _WwwrootDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        /// <summary>
        /// Start the proxy host.
        /// </summary>
        public async Task StartAsync(CancellationToken token = default)
        {
            if (_Started)
            {
                return;
            }

            WebserverSettings webserverSettings = new WebserverSettings(_Settings.Hostname, _Settings.Port, false);
            webserverSettings.IO.EnableKeepAlive = true;
            webserverSettings.IO.ReadTimeoutMs = 30000;
            webserverSettings.Protocols.IdleTimeoutMs = 30000;
            webserverSettings.Timeout.DefaultTimeout = TimeSpan.FromSeconds(Math.Max(10, _Settings.RequestTimeoutSeconds));
            webserverSettings.WebSockets.Enable = true;
            webserverSettings.WebSockets.AllowClientSuppliedGuid = true;

            _Server = new Webserver(webserverSettings, DefaultRouteAsync);
            ConfigureServer(_Server);
            await _Server.StartAsync(token).ConfigureAwait(false);
            _Started = true;

            if (!_Quiet)
            {
                _Logging.Info(_Header + "proxy started on " + _Server.Settings.Prefix);
            }
        }

        /// <summary>
        /// Stop the proxy host.
        /// </summary>
        public void Stop()
        {
            if (!_Started)
            {
                return;
            }

            _Logging.Info(_Header + "stopping");
            _Server.Stop();
            _Started = false;
            OnStopping?.Invoke();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_Disposed)
            {
                return;
            }

            _Disposed = true;
            Stop();
            _Server?.Dispose();
        }

        private void ConfigureServer(Webserver server)
        {
            server.Events.Logger = message => _Logging.Debug(_Header + message);
            server.Events.ExceptionEncountered += (sender, args) =>
            {
                if (args?.Exception != null)
                {
                    _Logging.Warn(_Header + "watson exception: " + args.Exception);
                }
            };
            server.Events.ServerStarted += (sender, args) => _Logging.Info(_Header + "server listening");
            server.Events.ServerStopped += (sender, args) => _Logging.Info(_Header + "server stopped");
            server.Events.WebSocketSessionStarted += (sender, args) =>
            {
                if (args?.Session != null)
                {
                    _Logging.Info(_Header + "websocket connected " + args.Session.RemoteIp + ":" + args.Session.RemotePort + " " + args.Session.Request.Path);
                }
            };
            server.Events.WebSocketSessionEnded += (sender, args) =>
            {
                if (args?.Session != null)
                {
                    _Logging.Info(_Header + "websocket disconnected " + args.Session.RemoteIp + ":" + args.Session.RemotePort + " " + args.Session.Request.Path);
                }
            };

            server.Middleware.Add(async (ctx, next, token) =>
            {
                DateTime startUtc = DateTime.UtcNow;
                await next().ConfigureAwait(false);
                double elapsedMs = (DateTime.UtcNow - startUtc).TotalMilliseconds;
                _Logging.Debug(_Header + ctx.Request.Method + " " + ctx.Request.Url.RawWithQuery + " " + ctx.Response.StatusCode + " (" + elapsedMs.ToString("F2") + "ms)");
            });

            server.Middleware.Add(async (ctx, next, token) =>
            {
                string path = ctx.Request.Url.RawWithoutQuery ?? String.Empty;
                if (!IsProtectedApiPath(path))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                string? sessionToken = ctx.Request.Headers.Get(Constants.ProxySessionTokenHeader);
                if (_Auth.TryValidateSession(sessionToken, out DateTime? _))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                await SendJsonErrorAsync(ctx, 401, "Proxy authentication required. Sign in again.").ConfigureAwait(false);
            });

            server.UseOpenApi(api =>
            {
                api.Info.Title = Constants.ProductName + " Proxy API";
                api.Info.Version = Constants.ProductVersion;
                api.Info.Description = "Remote proxy service for Armada instance discovery, tunnel routing, and bounded management actions.";
                api.Tags.Add(new OpenApiTag { Name = "Auth", Description = "Proxy browser authentication routes" });
                api.Tags.Add(new OpenApiTag { Name = "Status", Description = "Proxy health and status routes" });
                api.Tags.Add(new OpenApiTag { Name = "Instances", Description = "Remote instance inspection and tunnel forwarding" });
            });

            RegisterApiRoutes(server);
            RegisterWebSocketRoutes(server);
            // Register static content last so the catch-all asset mount at "/"
            // does not shadow API or websocket routes.
            RegisterStaticContent(server);
        }

        private void RegisterStaticContent(Webserver server)
        {
            server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/", ServeIndexAsync);

            if (Directory.Exists(_WwwrootDirectory))
            {
                server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/app.css", ctx => ServeStaticFileAsync(ctx, "app.css", "text/css"));
                server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/app.js", ctx => ServeStaticFileAsync(ctx, "app.js", "application/javascript"));
                server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/img/logo-dark-grey.png", ctx => ServeStaticFileAsync(ctx, Path.Combine("img", "logo-dark-grey.png"), "image/png"));
                server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/img/logo-light-grey.png", ctx => ServeStaticFileAsync(ctx, Path.Combine("img", "logo-light-grey.png"), "image/png"));
                server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/img/logo.ico", ctx => ServeStaticFileAsync(ctx, Path.Combine("img", "logo.ico"), "image/x-icon"));
            }
            else
            {
                _Logging.Warn(_Header + "wwwroot directory not found at " + _WwwrootDirectory);
            }
        }

        private void RegisterApiRoutes(Webserver server)
        {
            MapJsonGet(server, "/api/v1/auth/challenge", async (req) =>
            {
                ProxyAuthService.ProxyAuthChallenge challenge = _Auth.CreateChallenge();
                return new
                {
                    nonce = challenge.Nonce,
                    expiresUtc = challenge.ExpiresUtc
                };
            });

            MapJsonPost(server, "/api/v1/auth/login", async (req) =>
            {
                JsonElement payload = ReadJsonBody(req);
                string? nonce = GetOptionalProperty(payload, "nonce");
                string? proofSha256 = GetOptionalProperty(payload, "proofSha256");

                if (!_Auth.TryLogin(nonce, proofSha256, out string? sessionToken, out DateTime? expiresUtc, out string? error))
                {
                    req.Http.Response.StatusCode = 401;
                    return new { error = error ?? "Proxy authentication failed." };
                }

                _Logging.Info(_Header + "browser login accepted");
                return new
                {
                    token = sessionToken,
                    expiresUtc = expiresUtc
                };
            });

            MapJsonPost(server, "/api/v1/auth/logout", async (req) =>
            {
                string? sessionToken = req.Http.Request.Headers.Get(Constants.ProxySessionTokenHeader);
                _Auth.Logout(sessionToken);
                return new { success = true };
            });

            MapJsonGet(server, "/api/v1/status/health", async (req) => BuildHealthPayload());

            MapJsonGet(server, "/api/v1/instances", async (req) =>
            {
                List<RemoteInstanceSummary> instances = _Registry.ListSummaries();
                return new
                {
                    count = instances.Count,
                    instances = instances
                };
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                RemoteInstanceRecord? record = _Registry.GetRecord(instanceId);
                if (record == null)
                {
                    throw new WebserverException(ApiResultEnum.NotFound, "Instance not found.");
                }

                return new
                {
                    summary = record.ToSummary(DateTime.UtcNow, _Settings.StaleAfterSeconds),
                    recentEvents = record.GetRecentEvents()
                };
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/summary", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.instance.summary", null).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/status/snapshot", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardTunnelResponseAsync(req, instanceId, "armada.status.snapshot", null).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/health", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardTunnelResponseAsync(req, instanceId, "armada.status.health", null).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/activity", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 20, 1, 100);
                return await ForwardPayloadAsync(req, instanceId, "armada.activity.recent", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/missions/recent", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 10, 1, 100);
                return await ForwardPayloadAsync(req, instanceId, "armada.missions.recent", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/voyages/recent", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 10, 1, 100);
                return await ForwardPayloadAsync(req, instanceId, "armada.voyages.recent", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/captains/recent", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 10, 1, 100);
                return await ForwardPayloadAsync(req, instanceId, "armada.captains.recent", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/missions/{missionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.detail", new RemoteTunnelQueryRequest { MissionId = missionId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/missions/{missionId}/log", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                int offset = ParsePositiveInt(req.Query["offset"], 0, 0, Int32.MaxValue);
                int lines = ParsePositiveInt(req.Query["lines"], 200, 1, 2000);
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.log", new RemoteTunnelQueryRequest
                {
                    MissionId = missionId,
                    Offset = offset,
                    Lines = lines
                }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/missions/{missionId}/diff", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.diff", new RemoteTunnelQueryRequest { MissionId = missionId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/voyages/{voyageId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string voyageId = RequireParameter(req, "voyageId");
                return await ForwardPayloadAsync(req, instanceId, "armada.voyage.detail", new RemoteTunnelQueryRequest { VoyageId = voyageId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/captains/{captainId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string captainId = RequireParameter(req, "captainId");
                return await ForwardPayloadAsync(req, instanceId, "armada.captain.detail", new RemoteTunnelQueryRequest { CaptainId = captainId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/captains/{captainId}/log", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string captainId = RequireParameter(req, "captainId");
                int offset = ParsePositiveInt(req.Query["offset"], 0, 0, Int32.MaxValue);
                int lines = ParsePositiveInt(req.Query["lines"], 50, 1, 1000);
                return await ForwardPayloadAsync(req, instanceId, "armada.captain.log", new RemoteTunnelQueryRequest
                {
                    CaptainId = captainId,
                    Offset = offset,
                    Lines = lines
                }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/fleets", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 12, 1, 200);
                return await ForwardPayloadAsync(req, instanceId, "armada.fleets.list", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/fleets/{fleetId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string fleetId = RequireParameter(req, "fleetId");
                return await ForwardPayloadAsync(req, instanceId, "armada.fleet.detail", new RemoteTunnelQueryRequest { FleetId = fleetId }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/fleets", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                JsonElement payload = ReadJsonBody(req);
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.fleet.create", payload).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/fleets/{fleetId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string fleetId = RequireParameter(req, "fleetId");
                JsonElement fleet = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.fleet.update", new { fleetId, fleet }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/vessels", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 12, 1, 200);
                string? fleetId = GetOptionalValue(req.Query["fleetId"]);
                return await ForwardPayloadAsync(req, instanceId, "armada.vessels.list", new RemoteTunnelQueryRequest { Limit = limit, FleetId = fleetId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/vessels/{vesselId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                return await ForwardPayloadAsync(req, instanceId, "armada.vessel.detail", new RemoteTunnelQueryRequest { VesselId = vesselId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/pipelines", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 24, 1, 200);
                return await ForwardPayloadAsync(req, instanceId, "armada.pipelines.list", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/playbooks", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 24, 1, 200);
                return await ForwardPayloadAsync(req, instanceId, "armada.playbooks.list", new RemoteTunnelQueryRequest { Limit = limit }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/playbooks/{playbookId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string playbookId = RequireParameter(req, "playbookId");
                return await ForwardPayloadAsync(req, instanceId, "armada.playbook.detail", new RemoteTunnelQueryRequest { PlaybookId = playbookId }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/playbooks", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                JsonElement payload = ReadJsonBody(req);
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.playbook.create", payload).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/playbooks/{playbookId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string playbookId = RequireParameter(req, "playbookId");
                JsonElement playbook = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.playbook.update", new { playbookId, playbook }).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/playbooks/{playbookId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string playbookId = RequireParameter(req, "playbookId");
                return await ForwardPayloadAsync(req, instanceId, "armada.playbook.delete", new RemoteTunnelQueryRequest { PlaybookId = playbookId }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/vessels", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                JsonElement payload = ReadJsonBody(req);
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.vessel.create", payload).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/vessels/{vesselId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                JsonElement vessel = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.vessel.update", new { vesselId, vessel }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/voyages", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 12, 1, 200);
                string? status = GetOptionalValue(req.Query["status"]);
                return await ForwardPayloadAsync(req, instanceId, "armada.voyages.list", new RemoteTunnelQueryRequest { Limit = limit, Status = status }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/voyages/dispatch", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                JsonElement payload = ReadJsonBody(req);
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.voyage.dispatch", payload).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/voyages/{voyageId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string voyageId = RequireParameter(req, "voyageId");
                return await ForwardPayloadAsync(req, instanceId, "armada.voyage.cancel", new RemoteTunnelQueryRequest { VoyageId = voyageId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/missions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                int limit = ParsePositiveInt(req.Query["limit"], 16, 1, 200);
                return await ForwardPayloadAsync(req, instanceId, "armada.missions.list", new RemoteTunnelQueryRequest
                {
                    Limit = limit,
                    Status = GetOptionalValue(req.Query["status"]),
                    VoyageId = GetOptionalValue(req.Query["voyageId"]),
                    VesselId = GetOptionalValue(req.Query["vesselId"])
                }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/missions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                JsonElement payload = ReadJsonBody(req);
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.create", payload).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/missions/{missionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                JsonElement mission = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.update", new { missionId, mission }).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/missions/{missionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.cancel", new RemoteTunnelQueryRequest { MissionId = missionId }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/missions/{missionId}/restart", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string missionId = RequireParameter(req, "missionId");
                JsonElement payload = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.mission.restart", new
                {
                    missionId,
                    title = GetOptionalProperty(payload, "title"),
                    description = GetOptionalProperty(payload, "description")
                }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/captains/{captainId}/stop", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string captainId = RequireParameter(req, "captainId");
                return await ForwardPayloadAsync(req, instanceId, "armada.captain.stop", new RemoteTunnelQueryRequest { CaptainId = captainId }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/objectives", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objectives.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objectives/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objectives.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/objectives/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective.detail", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objectives", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.objective.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/objectives/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective.update", BuildIdBodyPayload(objectiveId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/objectives/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective.delete", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/backlog", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/backlog/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/backlog/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.detail", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/backlog", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/backlog/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.update", BuildIdBodyPayload(objectiveId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/backlog/{objectiveId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.backlog.delete", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/objectives/{objectiveId}/refinement-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-sessions.list", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objectives/{objectiveId}/refinement-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-sessions.create", BuildIdBodyPayload(objectiveId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/backlog/{objectiveId}/refinement-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-sessions.list", BuildIdPayload(objectiveId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/backlog/{objectiveId}/refinement-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string objectiveId = RequireParameter(req, "objectiveId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-sessions.create", BuildIdBodyPayload(objectiveId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.detail", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/messages", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.message", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/summarize", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.summarize", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/apply", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.apply", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}/stop", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.stop", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/objective-refinement-sessions/{sessionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.objective-refinement-session.delete", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/planning-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-sessions.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-sessions.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.detail", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}/messages", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.message", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}/summarize", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.summarize", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}/dispatch", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.dispatch", BuildIdBodyPayload(sessionId, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}/stop", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.stop", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/planning-sessions/{sessionId}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string sessionId = RequireParameter(req, "sessionId");
                return await ForwardPayloadAsync(req, instanceId, "armada.planning-session.delete", BuildIdPayload(sessionId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workflow-profiles", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profiles.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/workflow-profiles/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profiles.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/workflow-profiles", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profile.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workflow-profiles/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profile.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/workflow-profiles/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profile.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/workflow-profiles/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.workflow-profile.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/check-runs", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.check-runs.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/check-runs/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.check-runs.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/check-runs", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.check-run.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/check-runs/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.check-run.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/check-runs/{id}/retry", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.check-run.retry", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/check-runs/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.check-run.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/environments", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.environments.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/environments/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.environments.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/environments", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.environment.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/environments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.environment.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/environments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.environment.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/environments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.environment.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/releases", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.releases.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/releases/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.releases.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/releases", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.release.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/releases/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.release.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/releases/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.release.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/releases/{id}/refresh", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.release.refresh", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/releases/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.release.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/deployments", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployments.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployments.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/deployments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/deployments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments/{id}/approve", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                JsonElement body = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.approve", new { id, comment = GetOptionalProperty(body, "comment") }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments/{id}/deny", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                JsonElement body = ReadJsonBody(req);
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.deny", new { id, comment = GetOptionalProperty(body, "comment") }).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments/{id}/verify", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.verify", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/deployments/{id}/rollback", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.rollback", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/deployments/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.deployment.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/incidents", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.incidents.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/incidents/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.incidents.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/incidents", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.incident.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/incidents/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.incident.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/incidents/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.incident.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/incidents/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.incident.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/runbooks", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbooks.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/runbooks/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbooks.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/runbooks", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook.create", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/runbooks/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/runbooks/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/runbooks/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/runbook-executions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-executions.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/runbook-executions/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-executions.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/runbook-executions/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-execution.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/runbooks/{id}/executions", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                req.Http.Response.StatusCode = 201;
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-execution.create", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonPut(server, "/api/v1/instances/{instanceId}/runbook-executions/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-execution.update", BuildIdBodyPayload(id, ReadJsonBody(req))).ConfigureAwait(false);
            });

            MapJsonDelete(server, "/api/v1/instances/{instanceId}/runbook-executions/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.runbook-execution.delete", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/captains/{captainId}/tools", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string captainId = RequireParameter(req, "captainId");
                return await ForwardPayloadAsync(req, instanceId, "armada.captain.tools", BuildIdPayload(captainId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/request-history", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.request-history.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/request-history/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.request-history.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/request-history/summary", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.request-history.summary", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/request-history/summary", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.request-history.summary", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/request-history/{id}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string id = RequireParameter(req, "id");
                return await ForwardPayloadAsync(req, instanceId, "armada.request-history.detail", BuildIdPayload(id)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/status", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workspace.status", BuildIdPayload(vesselId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/tree", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workspace.tree", new { id = vesselId, path = GetOptionalValue(req.Query["path"]) }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/file", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workspace.file", new { id = vesselId, path = GetOptionalValue(req.Query["path"]) }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/search", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                int maxResults = ParsePositiveInt(req.Query["maxResults"], 200, 1, 1000);
                return await ForwardPayloadAsync(req, instanceId, "armada.workspace.search", new
                {
                    id = vesselId,
                    query = GetOptionalValue(req.Query["query"]) ?? GetOptionalValue(req.Query["q"]),
                    maxResults
                }).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/workspace/vessels/{vesselId}/changes", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string vesselId = RequireParameter(req, "vesselId");
                return await ForwardPayloadAsync(req, instanceId, "armada.workspace.changes", BuildIdPayload(vesselId)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/pipelines/{name}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string name = RequireParameter(req, "name");
                return await ForwardPayloadAsync(req, instanceId, "armada.pipeline.detail", BuildIdPayload(name)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/personas", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.personas.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/personas/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.personas.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/personas/{name}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string name = RequireParameter(req, "name");
                return await ForwardPayloadAsync(req, instanceId, "armada.persona.detail", BuildIdPayload(name)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/prompt-templates", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.prompt-templates.list", null).ConfigureAwait(false);
            });

            MapJsonPost(server, "/api/v1/instances/{instanceId}/prompt-templates/enumerate", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                return await ForwardPayloadAsync(req, instanceId, "armada.prompt-templates.list", ReadJsonBody(req)).ConfigureAwait(false);
            });

            MapJsonGet(server, "/api/v1/instances/{instanceId}/prompt-templates/{name}", async (req) =>
            {
                string instanceId = RequireParameter(req, "instanceId");
                string name = RequireParameter(req, "name");
                return await ForwardPayloadAsync(req, instanceId, "armada.prompt-template.detail", BuildIdPayload(name)).ConfigureAwait(false);
            });
        }

        private void MapJsonGet(Webserver server, string path, Func<ApiRequest, Task<object>> handler)
        {
            server.Get(path, async (req) => await PrepareJsonResponseAsync(req, handler).ConfigureAwait(false));
        }

        private void MapJsonPost(Webserver server, string path, Func<ApiRequest, Task<object>> handler)
        {
            server.Post(path, async (req) => await PrepareJsonResponseAsync(req, handler).ConfigureAwait(false));
        }

        private void MapJsonPut(Webserver server, string path, Func<ApiRequest, Task<object>> handler)
        {
            server.Put(path, async (req) => await PrepareJsonResponseAsync(req, handler).ConfigureAwait(false));
        }

        private void MapJsonDelete(Webserver server, string path, Func<ApiRequest, Task<object>> handler)
        {
            server.Delete(path, async (req) => await PrepareJsonResponseAsync(req, handler).ConfigureAwait(false));
        }

        private static object BuildIdPayload(string id)
        {
            return new { id };
        }

        private static object BuildIdBodyPayload(string id, JsonElement body)
        {
            return new { id, body };
        }

        private static async Task<object> PrepareJsonResponseAsync(ApiRequest req, Func<ApiRequest, Task<object>> handler)
        {
            object payload = await handler(req).ConfigureAwait(false);
            if (req.Http.Response.StatusCode <= 0)
            {
                req.Http.Response.StatusCode = 200;
            }

            if (String.IsNullOrWhiteSpace(req.Http.Response.ContentType))
            {
                req.Http.Response.ContentType = "application/json";
            }

            return payload;
        }

        private void RegisterWebSocketRoutes(Webserver server)
        {
            server.WebSocket("/tunnel", HandleTunnelAsync);
        }

        private object BuildHealthPayload()
        {
            List<RemoteInstanceSummary> instances = _Registry.ListSummaries();
            return new
            {
                healthy = true,
                product = "Armada.Proxy",
                version = Constants.ProductVersion,
                protocolVersion = Constants.RemoteTunnelProtocolVersion,
                port = _Settings.Port,
                startedUtc = _StartUtc,
                instances = new
                {
                    total = instances.Count,
                    connected = instances.Count(instance => String.Equals(instance.State, "connected", StringComparison.OrdinalIgnoreCase)),
                    stale = instances.Count(instance => String.Equals(instance.State, "stale", StringComparison.OrdinalIgnoreCase)),
                    offline = instances.Count(instance => String.Equals(instance.State, "offline", StringComparison.OrdinalIgnoreCase))
                }
            };
        }

        private async Task<object> ForwardPayloadAsync(ApiRequest req, string instanceId, string method, object? payload)
        {
            try
            {
                string? requesterIp = GetRequesterIp(req);
                RemoteTunnelEnvelope response = await _Registry.SendRequestAsync(instanceId, method, payload, req.CancellationToken, requesterIp).ConfigureAwait(false);
                return BuildForwardedPayloadResult(req, response);
            }
            catch (Exception ex)
            {
                req.Http.Response.StatusCode = 400;
                return new { error = ex.Message };
            }
        }

        private async Task<object> ForwardTunnelResponseAsync(ApiRequest req, string instanceId, string method, object? payload)
        {
            try
            {
                string? requesterIp = GetRequesterIp(req);
                RemoteTunnelEnvelope response = await _Registry.SendRequestAsync(instanceId, method, payload, req.CancellationToken, requesterIp).ConfigureAwait(false);
                return BuildTunnelProxyResponse(response);
            }
            catch (Exception ex)
            {
                req.Http.Response.StatusCode = 400;
                return new { error = ex.Message };
            }
        }

        private object BuildForwardedPayloadResult(ApiRequest req, RemoteTunnelEnvelope response)
        {
            object? payload = DeserializePayload(response.Payload);
            int statusCode = response.StatusCode ?? (response.Success == false ? 502 : 200);

            if (statusCode >= 200 && statusCode < 300 && String.IsNullOrWhiteSpace(response.ErrorCode))
            {
                req.Http.Response.StatusCode = statusCode;
                return payload ?? new { };
            }

            req.Http.Response.StatusCode = statusCode;
            return new
            {
                error = response.Message ?? "Tunnel request failed.",
                errorCode = response.ErrorCode,
                correlationId = response.CorrelationId,
                payload = payload
            };
        }

        private object BuildTunnelProxyResponse(RemoteTunnelEnvelope response)
        {
            return new
            {
                correlationId = response.CorrelationId,
                success = response.Success,
                statusCode = response.StatusCode,
                errorCode = response.ErrorCode,
                message = response.Message,
                payload = DeserializePayload(response.Payload)
            };
        }

        private static string? GetRequesterIp(ApiRequest req)
        {
            if (req == null) return null;

            string? forwardedFor = req.Http?.Request?.Headers?.Get("X-Forwarded-For");
            string? forwarded = req.Http?.Request?.Headers?.Get("Forwarded");

            string? forwardedForIp = NormalizeForwardedValue(forwardedFor?.Split(',').FirstOrDefault());
            if (!String.IsNullOrWhiteSpace(forwardedForIp))
            {
                return forwardedForIp;
            }

            if (!String.IsNullOrWhiteSpace(forwarded))
            {
                string? fromForwardedHeader = ExtractForwardedForIp(forwarded);
                if (!String.IsNullOrWhiteSpace(fromForwardedHeader))
                {
                    return fromForwardedHeader;
                }
            }

            string? sourceIp = req.Http?.Request?.Source?.IpAddress;
            return String.IsNullOrWhiteSpace(sourceIp) ? null : sourceIp;
        }

        private static string? ExtractForwardedForIp(string? forwardedHeader)
        {
            if (String.IsNullOrWhiteSpace(forwardedHeader))
            {
                return null;
            }

            string firstForwarded = forwardedHeader.Split(',').FirstOrDefault()?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(firstForwarded))
            {
                return null;
            }

            foreach (string part in firstForwarded.Split(';'))
            {
                string candidate = part.Trim();
                if (!candidate.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return NormalizeForwardedValue(candidate.Substring(4));
            }

            return null;
        }

        private static string? NormalizeForwardedValue(string? value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string candidate = value.Trim().Trim('"');
            if (String.IsNullOrWhiteSpace(candidate) || String.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (candidate.StartsWith("[", StringComparison.Ordinal) && candidate.Contains(']'))
            {
                int endBracket = candidate.IndexOf(']');
                if (endBracket > 1)
                {
                    return candidate.Substring(1, endBracket - 1);
                }
            }

            if (candidate.Count(ch => ch == ':') == 1 && candidate.Contains('.'))
            {
                int lastColon = candidate.LastIndexOf(':');
                if (lastColon > 0)
                {
                    return candidate.Substring(0, lastColon);
                }
            }

            return candidate;
        }

        private Task HandleTunnelAsync(HttpContextBase ctx, WebSocketSession session)
        {
            return HandleTunnelInternalAsync(ctx, session);
        }

        private async Task HandleTunnelInternalAsync(HttpContextBase ctx, WebSocketSession session)
        {
            string? instanceId = null;
            string remoteAddress = String.IsNullOrWhiteSpace(session.RemoteIp) ? "unknown" : session.RemoteIp + ":" + session.RemotePort;

            try
            {
                using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(_Settings.HandshakeTimeoutSeconds));
                RemoteTunnelEnvelope firstEnvelope = await ReceiveEnvelopeAsync(session, handshakeTimeout.Token).ConfigureAwait(false);

                if (!String.Equals(firstEnvelope.Type, "request", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(firstEnvelope.Method, "armada.tunnel.handshake", StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Warn(_Header + "invalid handshake from " + remoteAddress + ": first message was " + (firstEnvelope.Method ?? firstEnvelope.Type ?? "unknown"));
                    await SendEnvelopeAsync(session, RemoteTunnelProtocol.CreateError(firstEnvelope.CorrelationId, "invalid_handshake", "First tunnel message must be armada.tunnel.handshake.", 400), ctx.Token).ConfigureAwait(false);
                    await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Handshake required", ctx.Token).ConfigureAwait(false);
                    return;
                }

                RemoteTunnelHandshakePayload? handshake = firstEnvelope.Payload?.Deserialize<RemoteTunnelHandshakePayload>(RemoteTunnelProtocol.JsonOptions);
                if (!_Registry.TryValidateHandshake(handshake, out string? handshakeError))
                {
                    _Logging.Warn(_Header + "handshake rejected from " + remoteAddress + ": " + (handshakeError ?? "unknown reason"));
                    await SendEnvelopeAsync(
                        session,
                        RemoteTunnelProtocol.CreateResponse(
                            firstEnvelope.CorrelationId,
                            new RemoteTunnelRequestResult
                            {
                                StatusCode = 401,
                                ErrorCode = "handshake_rejected",
                                Message = handshakeError
                            }),
                        ctx.Token).ConfigureAwait(false);
                    await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, handshakeError ?? "Handshake rejected", ctx.Token).ConfigureAwait(false);
                    return;
                }

                RemoteInstanceSession proxySession = new RemoteInstanceSession((envelope, token) => SendEnvelopeAsync(session, envelope, token));
                instanceId = handshake!.InstanceId!.Trim();
                _Registry.RegisterHandshake(handshake, remoteAddress, proxySession);
                _Logging.Info(_Header + "handshake accepted for " + instanceId + " from " + remoteAddress);

                await SendEnvelopeAsync(
                    session,
                    RemoteTunnelProtocol.CreateResponse(
                        firstEnvelope.CorrelationId,
                        new RemoteTunnelRequestResult
                        {
                            StatusCode = 200,
                            Payload = new RemoteTunnelHandshakeResponse
                            {
                                Accepted = true,
                                ProxyVersion = Constants.ProductVersion,
                                ProtocolVersion = Constants.RemoteTunnelProtocolVersion,
                                InstanceId = instanceId,
                                Message = "Handshake accepted.",
                                Capabilities = GetCapabilities()
                            },
                            Message = "Handshake accepted."
                        }),
                    ctx.Token).ConfigureAwait(false);

                await foreach (WebSocketMessage message in session.ReadMessagesAsync(ctx.Token).ConfigureAwait(false))
                {
                    if (message.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    RemoteTunnelEnvelope envelope = DeserializeEnvelope(message.Text);
                    _Registry.MarkSeen(instanceId);

                    if (String.Equals(envelope.Type, "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendEnvelopeAsync(session, RemoteTunnelProtocol.CreatePong(envelope.CorrelationId), ctx.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (String.Equals(envelope.Type, "response", StringComparison.OrdinalIgnoreCase))
                    {
                        _Registry.TryCompleteResponse(instanceId, envelope);
                        continue;
                    }

                    if (String.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(envelope.Type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        _Registry.RecordEvent(instanceId, envelope);
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
                if (session.IsConnected)
                {
                    try
                    {
                        await SendEnvelopeAsync(session, RemoteTunnelProtocol.CreateError(null, "invalid_json", ex.Message, 400), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(instanceId))
                {
                    _Registry.MarkDisconnected(instanceId);
                }

                if (session.IsConnected)
                {
                    try
                    {
                        await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tunnel closed", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task<RemoteTunnelEnvelope> ReceiveEnvelopeAsync(WebSocketSession session, CancellationToken token)
        {
            WebSocketMessage message = await session.ReceiveAsync(token).ConfigureAwait(false);
            if (message.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Only text websocket messages are supported.");
            }

            return DeserializeEnvelope(message.Text);
        }

        private RemoteTunnelEnvelope DeserializeEnvelope(string? json)
        {
            return JsonSerializer.Deserialize<RemoteTunnelEnvelope>(json ?? String.Empty, RemoteTunnelProtocol.JsonOptions)
                ?? throw new JsonException("Tunnel envelope could not be deserialized.");
        }

        private async Task SendEnvelopeAsync(WebSocketSession session, RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(envelope, RemoteTunnelProtocol.JsonOptions);
            await session.SendTextAsync(json, token).ConfigureAwait(false);
        }

        private async Task ServeIndexAsync(HttpContextBase ctx)
        {
            await ServeStaticFileAsync(ctx, "index.html", "text/html").ConfigureAwait(false);
        }

        private Task DefaultRouteAsync(HttpContextBase ctx)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.Send("{\"error\":\"Not found\"}", ctx.Token);
        }

        private static object? DeserializePayload(JsonElement? payload)
        {
            if (!payload.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<object>(payload.Value.GetRawText(), RemoteTunnelProtocol.JsonOptions);
        }

        private async Task ServeStaticFileAsync(HttpContextBase ctx, string relativePath, string contentType)
        {
            string fullPath = Path.Combine(_WwwrootDirectory, relativePath);
            if (!File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.Send("Armada.Proxy asset is missing.", ctx.Token).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            byte[] bytes = await File.ReadAllBytesAsync(fullPath, ctx.Token).ConfigureAwait(false);
            await ctx.Response.Send(bytes, ctx.Token).ConfigureAwait(false);
        }

        private static string RequireParameter(ApiRequest req, string name)
        {
            string value = req.Parameters[name];
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new WebserverException(ApiResultEnum.BadRequest, "Missing parameter: " + name);
            }

            return value.Trim();
        }

        private static string? GetOptionalValue(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int ParsePositiveInt(string? rawValue, int defaultValue, int minimum, int maximum)
        {
            if (!Int32.TryParse(rawValue, out int parsed))
            {
                parsed = defaultValue;
            }

            if (parsed < minimum) parsed = minimum;
            if (parsed > maximum) parsed = maximum;
            return parsed;
        }

        private static JsonElement ReadJsonBody(ApiRequest req)
        {
            string body = req.Http.Request.DataAsString ?? String.Empty;
            if (String.IsNullOrWhiteSpace(body))
            {
                return JsonDocument.Parse("{}").RootElement.Clone();
            }

            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private static string? GetOptionalProperty(JsonElement element, string propertyName)
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

        private static bool IsProtectedApiPath(string? path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (String.Equals(path, "/api/v1/status/health", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static Task SendJsonErrorAsync(HttpContextBase ctx, int statusCode, string message)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            string json = JsonSerializer.Serialize(new { error = message }, RemoteTunnelProtocol.JsonOptions);
            return ctx.Response.Send(json, ctx.Token);
        }

        private static List<string> GetCapabilities()
        {
            return new List<string>
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
                "instances.pipelines.list",
                "instances.pipeline.detail",
                "instances.playbooks.list",
                "instances.playbook.detail",
                "instances.playbook.create",
                "instances.playbook.update",
                "instances.playbook.delete",
                "instances.backlog.list",
                "instances.backlog.detail",
                "instances.backlog.create",
                "instances.backlog.update",
                "instances.backlog.delete",
                "instances.objectives.list",
                "instances.objective.detail",
                "instances.objective.create",
                "instances.objective.update",
                "instances.objective.delete",
                "instances.refinement.list",
                "instances.refinement.detail",
                "instances.refinement.create",
                "instances.refinement.message",
                "instances.refinement.summarize",
                "instances.refinement.apply",
                "instances.refinement.stop",
                "instances.refinement.delete",
                "instances.planning.list",
                "instances.planning.detail",
                "instances.planning.create",
                "instances.planning.message",
                "instances.planning.summarize",
                "instances.planning.dispatch",
                "instances.planning.stop",
                "instances.planning.delete",
                "instances.workflowprofiles.list",
                "instances.workflowprofile.detail",
                "instances.workflowprofile.create",
                "instances.workflowprofile.update",
                "instances.workflowprofile.delete",
                "instances.checkruns.list",
                "instances.checkrun.detail",
                "instances.checkrun.create",
                "instances.checkrun.retry",
                "instances.checkrun.delete",
                "instances.environments.list",
                "instances.environment.detail",
                "instances.environment.create",
                "instances.environment.update",
                "instances.environment.delete",
                "instances.releases.list",
                "instances.release.detail",
                "instances.release.create",
                "instances.release.update",
                "instances.release.refresh",
                "instances.release.delete",
                "instances.deployments.list",
                "instances.deployment.detail",
                "instances.deployment.create",
                "instances.deployment.update",
                "instances.deployment.approve",
                "instances.deployment.deny",
                "instances.deployment.verify",
                "instances.deployment.rollback",
                "instances.deployment.delete",
                "instances.incidents.list",
                "instances.incident.detail",
                "instances.incident.create",
                "instances.incident.update",
                "instances.incident.delete",
                "instances.runbooks.list",
                "instances.runbook.detail",
                "instances.runbook.create",
                "instances.runbook.update",
                "instances.runbook.delete",
                "instances.runbookexecutions.list",
                "instances.runbookexecution.detail",
                "instances.runbookexecution.create",
                "instances.runbookexecution.update",
                "instances.runbookexecution.delete",
                "instances.captain.tools",
                "instances.requesthistory.list",
                "instances.requesthistory.detail",
                "instances.requesthistory.summary",
                "instances.workspace.status",
                "instances.workspace.tree",
                "instances.workspace.file",
                "instances.workspace.search",
                "instances.workspace.changes",
                "instances.personas.list",
                "instances.persona.detail",
                "instances.prompttemplates.list",
                "instances.prompttemplate.detail",
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
            };
        }
    }
}
