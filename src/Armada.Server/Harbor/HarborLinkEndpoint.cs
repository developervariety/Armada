namespace Armada.Server.Harbor
{
    using System;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.WebSockets;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Server end of the Harbor link. The upgrade is authenticated by the application's authentication service
    /// from its standard credential headers only; tenant, user and access-key headers carry no authority. The
    /// first frame must be a handshake for a runner enrolled to that verified principal. Every later frame is
    /// checked against the session lease and, for jobs, against the job's bound connection generation.
    /// </summary>
    public sealed class HarborLinkEndpoint
    {
        #region Private-Members

        private readonly string _Header = "[HarborLink] ";
        private readonly HarborSettings _Settings;
        private readonly IAuthenticationService _Authentication;
        private readonly HarborRunnerSessionRegistry _Registry;
        private readonly HarborJobCoordinator _Coordinator;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate the Harbor link endpoint.</summary>
        /// <param name="settings">Harbor settings.</param>
        /// <param name="authentication">Application authentication service.</param>
        /// <param name="registry">Enabled session registry backed by durable enrollment.</param>
        /// <param name="coordinator">Job coordinator.</param>
        /// <param name="logging">Logging module.</param>
        public HarborLinkEndpoint(
            HarborSettings settings,
            IAuthenticationService authentication,
            HarborRunnerSessionRegistry registry,
            HarborJobCoordinator coordinator,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            _Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>Watson WebSocket handler for one Harbor link.</summary>
        /// <param name="ctx">HTTP context of the upgrade.</param>
        /// <param name="session">WebSocket session.</param>
        public async Task HandleWebSocketAsync(HttpContextBase ctx, WebSocketSession session)
        {
            SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
            Func<HarborMessage, CancellationToken, Task> send = async (message, token) =>
            {
                await sendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await session.SendTextAsync(HarborProtocol.Serialize(message), token).ConfigureAwait(false);
                }
                finally
                {
                    sendLock.Release();
                }
            };
            HarborRunnerConnection connection = new HarborRunnerConnection(_Registry);

            try
            {
                AuthContext auth = await _Authentication.AuthenticateAsync(
                    ctx.Request.Headers.Get("Authorization"),
                    ctx.Request.Headers.Get("X-Token"),
                    ctx.Request.Headers.Get("X-Api-Key"),
                    ctx.Token).ConfigureAwait(false);
                if (!auth.IsAuthenticated)
                {
                    await RejectAsync(session, send, null, "harbor_authentication_failed", false).ConfigureAwait(false);
                    return;
                }

                using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(_Settings.HandshakeTimeoutSeconds));
                    try
                    {
                        await foreach (WebSocketMessage frame in session.ReadMessagesAsync(idle.Token))
                        {
                            if (!await HandleFrameAsync(frame, auth, connection, session, send, idle, ctx.Token).ConfigureAwait(false)) return;
                        }
                    }
                    catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested)
                    {
                        string reason = connection.IsHandshaken ? "harbor_link_idle" : "harbor_handshake_timeout";
                        if (connection.Session != null) await _Coordinator.MarkSessionEndedAsync(connection.Session, reason).ConfigureAwait(false);
                        await RejectAsync(session, send, null, reason, connection.IsHandshaken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _Logging.Debug(_Header + "link canceled by server shutdown");
            }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "link error: " + exception.Message);
            }
            finally
            {
                if (connection.Session != null) _Coordinator.Detach(connection.Session);
                connection.Disconnect();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<bool> HandleFrameAsync(
            WebSocketMessage frame,
            AuthContext auth,
            HarborRunnerConnection connection,
            WebSocketSession session,
            Func<HarborMessage, CancellationToken, Task> send,
            CancellationTokenSource idle,
            CancellationToken token)
        {
            if (frame.MessageType != WebSocketMessageType.Text || String.IsNullOrEmpty(frame.Text))
            {
                await RejectAsync(session, send, null, "harbor_frame_invalid", connection.IsHandshaken).ConfigureAwait(false);
                return false;
            }

            HarborMessage parsed;
            try
            {
                parsed = HarborProtocol.Deserialize(frame.Text);
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                _Logging.Warn(_Header + "malformed Harbor frame: " + exception.Message);
                await RejectAsync(session, send, null, "harbor_message_invalid", connection.IsHandshaken).ConfigureAwait(false);
                return false;
            }

            if (!connection.IsHandshaken)
            {
                if (parsed is not HarborHandshake handshake)
                {
                    await RejectAsync(session, send, parsed.CorrelationId, "harbor_handshake_required", false).ConfigureAwait(false);
                    return false;
                }
                if (!connection.TryAcceptHandshake(handshake, auth, out string handshakeReason) || connection.Session == null)
                {
                    await RejectAsync(session, send, handshake.CorrelationId, handshakeReason, false).ConfigureAwait(false);
                    return false;
                }
                _Coordinator.Attach(connection.Session, handshake.MaxConcurrentJobs, send);
                await send(new HarborHandshakeAck { CorrelationId = handshake.CorrelationId, Accepted = true }, token).ConfigureAwait(false);
                idle.CancelAfter(TimeSpan.FromSeconds(_Settings.IdleTimeoutSeconds));
                _Logging.Info(_Header + "runner " + connection.Session.Identity.RunnerId + " connected at generation " + connection.Session.Generation);
                return true;
            }

            idle.CancelAfter(TimeSpan.FromSeconds(_Settings.IdleTimeoutSeconds));
            HarborRunnerSession current = connection.Session!;

            if (parsed is HarborHandshake)
            {
                await send(new HarborError { CorrelationId = parsed.CorrelationId, Message = "harbor_handshake_duplicate" }, token).ConfigureAwait(false);
                return true;
            }

            if (parsed is HarborHeartbeat heartbeat)
            {
                if (!connection.TryAcceptHeartbeat(heartbeat, out string heartbeatReason))
                {
                    await _Coordinator.MarkSessionEndedAsync(current, heartbeatReason).ConfigureAwait(false);
                    await RejectAsync(session, send, heartbeat.CorrelationId, heartbeatReason, true).ConfigureAwait(false);
                    return false;
                }
                HarborHeartbeatResult applied = await _Coordinator.ApplyHeartbeatAsync(current, heartbeat.LiveJobIds).ConfigureAwait(false);
                foreach (System.Collections.Generic.KeyValuePair<string, string> refused in applied.Rejected)
                    await send(new HarborError { CorrelationId = heartbeat.CorrelationId, JobId = refused.Key, Message = refused.Value }, token).ConfigureAwait(false);
                return true;
            }

            HarborCommandResult result = await _Coordinator.HandleRunnerEventAsync(current, parsed).ConfigureAwait(false);
            if (result.Accepted) return true;
            if (result.EndsSession)
            {
                await RejectAsync(session, send, parsed.CorrelationId, result.Reason, true).ConfigureAwait(false);
                return false;
            }
            await send(new HarborError { CorrelationId = parsed.CorrelationId, JobId = JobIdOf(parsed), Message = result.Reason }, token).ConfigureAwait(false);
            return true;
        }

        private async Task RejectAsync(
            WebSocketSession session,
            Func<HarborMessage, CancellationToken, Task> send,
            string? correlationId,
            string reason,
            bool handshaken)
        {
            _Logging.Warn(_Header + "closing link from " + session.RemoteIp + ": " + reason);
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    HarborMessage refusal = handshaken
                        ? new HarborError { CorrelationId = correlationId, Message = reason }
                        : new HarborHandshakeAck { CorrelationId = correlationId, Accepted = false, Reason = reason };
                    await send(refusal, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _Logging.Debug(_Header + "refusal frame not delivered: " + exception.Message);
                }
                try
                {
                    await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _Logging.Debug(_Header + "close frame not delivered: " + exception.Message);
                }
            }
        }

        private static string? JobIdOf(HarborMessage message)
        {
            return message switch
            {
                HarborStarted started => started.JobId,
                HarborOutput output => output.JobId,
                HarborExited exited => exited.JobId,
                HarborError error => error.JobId,
                _ => null
            };
        }

        #endregion
    }
}
