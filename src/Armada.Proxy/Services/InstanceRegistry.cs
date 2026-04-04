namespace Armada.Proxy.Services
{
    using System.Collections.Concurrent;
    using Armada.Proxy.Models;
    using Armada.Proxy.Settings;
    using Armada.Core.Models;

    /// <summary>
    /// Tracks connected Armada instances and routes live tunnel requests.
    /// </summary>
    public class InstanceRegistry
    {
        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public InstanceRegistry(ProxySettings settings, Func<DateTime>? utcNow = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _UtcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a handshake payload against proxy settings.
        /// </summary>
        public bool TryValidateHandshake(RemoteTunnelHandshakePayload? payload, out string? error)
        {
            error = null;

            if (payload == null)
            {
                error = "Handshake payload is required.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(payload.InstanceId))
            {
                error = "Handshake payload is missing instanceId.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(payload.ProtocolVersion))
            {
                error = "Handshake payload is missing protocolVersion.";
                return false;
            }

            HashSet<string> tokens = _Settings.GetEnrollmentTokenSet();
            bool tokenRequired = _Settings.RequireEnrollmentToken || tokens.Count > 0;
            string? submittedToken = payload.EnrollmentToken?.Trim();

            if (tokenRequired && String.IsNullOrWhiteSpace(submittedToken))
            {
                error = "Handshake requires a valid enrollment token.";
                return false;
            }

            if (tokens.Count > 0 && !tokens.Contains(submittedToken ?? String.Empty))
            {
                error = "Handshake enrollment token is invalid.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Register or refresh an instance after handshake acceptance.
        /// </summary>
        public RemoteInstanceRecord RegisterHandshake(RemoteTunnelHandshakePayload payload, string? remoteAddress, RemoteInstanceSession session)
        {
            DateTime nowUtc = _UtcNow();
            string instanceId = payload.InstanceId!.Trim();
            RemoteInstanceRecord record = _Records.GetOrAdd(instanceId, _ => new RemoteInstanceRecord
            {
                InstanceId = instanceId,
                FirstSeenUtc = nowUtc
            });

            RemoteInstanceSession? previous = record.AttachSession(session, payload, remoteAddress, nowUtc);
            if (previous != null && !ReferenceEquals(previous, session))
            {
                previous.FailAll(new IOException("Instance session was replaced by a newer connection."));
            }

            return record;
        }

        /// <summary>
        /// Mark an instance disconnected.
        /// </summary>
        public void MarkDisconnected(string instanceId, string? error = null)
        {
            if (_Records.TryGetValue(instanceId, out RemoteInstanceRecord? record))
            {
                RemoteInstanceSession? session = record.Session;
                record.MarkDisconnected(_UtcNow(), error);
                session?.FailAll(new IOException(error ?? "Instance disconnected."));
            }
        }

        /// <summary>
        /// Mark generic inbound activity for an instance.
        /// </summary>
        public void MarkSeen(string instanceId)
        {
            if (_Records.TryGetValue(instanceId, out RemoteInstanceRecord? record))
            {
                record.MarkSeen(_UtcNow());
            }
        }

        /// <summary>
        /// Record an inbound event envelope for an instance.
        /// </summary>
        public void RecordEvent(string instanceId, RemoteTunnelEnvelope envelope)
        {
            if (_Records.TryGetValue(instanceId, out RemoteInstanceRecord? record))
            {
                record.RecordEvent(envelope, _UtcNow(), _Settings.MaxRecentEvents);
            }
        }

        /// <summary>
        /// Attempt to complete a pending request with an inbound response envelope.
        /// </summary>
        public bool TryCompleteResponse(string instanceId, RemoteTunnelEnvelope envelope)
        {
            if (_Records.TryGetValue(instanceId, out RemoteInstanceRecord? record) && record.Session != null)
            {
                record.MarkSeen(_UtcNow());
                return record.Session.TryCompleteResponse(envelope);
            }

            return false;
        }

        /// <summary>
        /// Get summaries for all known instances.
        /// </summary>
        public List<RemoteInstanceSummary> ListSummaries()
        {
            DateTime nowUtc = _UtcNow();
            return _Records.Values
                .Select(record => record.ToSummary(nowUtc, _Settings.StaleAfterSeconds))
                .OrderBy(summary => summary.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Retrieve a tracked record by instance identifier.
        /// </summary>
        public RemoteInstanceRecord? GetRecord(string instanceId)
        {
            _Records.TryGetValue(instanceId, out RemoteInstanceRecord? record);
            return record;
        }

        /// <summary>
        /// Send a live request to a connected instance.
        /// </summary>
        public async Task<RemoteTunnelEnvelope> SendRequestAsync(string instanceId, string method, object? payload, CancellationToken token)
        {
            if (!_Records.TryGetValue(instanceId, out RemoteInstanceRecord? record) || record.Session == null)
            {
                throw new InvalidOperationException("Instance " + instanceId + " is not connected.");
            }

            return await record.Session.SendRequestAsync(
                method,
                payload,
                TimeSpan.FromSeconds(_Settings.RequestTimeoutSeconds),
                token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Members

        private readonly ProxySettings _Settings;
        private readonly Func<DateTime> _UtcNow;
        private readonly ConcurrentDictionary<string, RemoteInstanceRecord> _Records = new ConcurrentDictionary<string, RemoteInstanceRecord>(StringComparer.Ordinal);

        #endregion
    }
}
