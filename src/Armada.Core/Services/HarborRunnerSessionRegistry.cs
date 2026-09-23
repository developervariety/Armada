namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Fail-closed registry for authenticated Harbor runner sessions and their typed pending responses.
    /// This core has no network or process-launch behavior; transport adapters must present the session
    /// lease returned here for every request, response, and disconnect. Enabled use also requires an
    /// authoritative owner resolver backed by durable runner enrollment. Owner resolution is awaited before the
    /// registry lock is taken, so a slow durable lookup never blocks a thread or the lock.
    /// </summary>
    public sealed class HarborRunnerSessionRegistry
    {
        private const int ReplayCacheLimit = 4096;
        private readonly object _Gate = new object();
        private readonly IHarborRunnerOwnerResolver? _OwnerResolver;
        private readonly Dictionary<string, HarborRunnerSession> _Sessions = new Dictionary<string, HarborRunnerSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _LatestEnrollmentGenerations = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, IPendingRequest> _Pending = new Dictionary<string, IPendingRequest>(StringComparer.Ordinal);
        private readonly HashSet<string> _ReplayIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _ReplayOrder = new Queue<string>();
        private long _NextGeneration;
        private long _NextRequestSequence;

        /// <summary>Whether new Harbor runner registrations are enabled.</summary>
        public bool Enabled { get; }

        /// <summary>Instantiate the registry. New registrations are disabled by default.</summary>
        /// <param name="enabled">Enable registration explicitly.</param>
        /// <param name="ownerResolver">Authoritative durable runner enrollment resolver.</param>
        public HarborRunnerSessionRegistry(bool enabled = false, IHarborRunnerOwnerResolver? ownerResolver = null)
        {
            Enabled = enabled;
            _OwnerResolver = ownerResolver;
            if (_OwnerResolver is IHarborRunnerOwnerChangeNotifier notifier)
                notifier.OwnerChanged += HandleOwnerChanged;
        }

        /// <summary>
        /// Register a runner using a verified authentication context. A reconnect replaces the prior
        /// session only when its principal is the same verified identity.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="auth">Verified authentication context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The new session lease, or the stable denial reason.</returns>
        public async Task<HarborRunnerRegistration> RegisterAsync(
            string runnerId,
            AuthContext auth,
            CancellationToken token = default)
        {
            if (!Enabled) return HarborRunnerRegistration.Refuse("runner_registration_disabled");
            if (auth == null) return HarborRunnerRegistration.Refuse("runner_identity_unverified");

            HarborRunnerIdentity identity;
            try
            {
                identity = HarborRunnerIdentity.FromVerifiedAuthContext(runnerId, auth);
            }
            catch (UnauthorizedAccessException)
            {
                return HarborRunnerRegistration.Refuse("runner_identity_unverified");
            }
            catch (ArgumentException)
            {
                return HarborRunnerRegistration.Refuse("runner_id_invalid");
            }

            if (_OwnerResolver == null) return HarborRunnerRegistration.Refuse("runner_owner_unconfigured");

            HarborRunnerOwnerResolution resolution = await ResolveOwnerAsync(identity.RunnerId, "runner_owner_unknown", token).ConfigureAwait(false);
            if (!resolution.Resolved || resolution.Owner == null) return HarborRunnerRegistration.Refuse(resolution.FailureReason);
            if (!identity.Matches(resolution.Owner)) return HarborRunnerRegistration.Refuse("runner_owner_mismatch");
            long enrollmentGeneration = resolution.Generation;

            lock (_Gate)
            {
                if (IsEnrollmentGenerationStale(identity.RunnerId, enrollmentGeneration))
                    return HarborRunnerRegistration.Refuse("runner_enrollment_generation_stale");
                _Sessions.TryGetValue(identity.RunnerId, out HarborRunnerSession? existing);
                if (existing != null && existing.EnrollmentGeneration < enrollmentGeneration)
                {
                    // The durable enrollment changed after the live session was accepted, possibly on another
                    // instance. The old session can no longer authorize anything and must not block the owner
                    // of the current generation.
                    InvalidatePendingForSession(existing);
                    _Sessions.Remove(identity.RunnerId);
                    existing = null;
                }
                if (existing != null
                    && (!String.Equals(existing.Identity.TenantId, identity.TenantId, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.UserId, identity.UserId, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.AuthMethod, identity.AuthMethod, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.CredentialId, identity.CredentialId, StringComparison.Ordinal)))
                {
                    return HarborRunnerRegistration.Refuse("runner_identity_conflict");
                }

                if (existing != null) InvalidatePendingForSession(existing);
                HarborRunnerSession replacement = new HarborRunnerSession(identity, ++_NextGeneration, enrollmentGeneration);
                _Sessions[identity.RunnerId] = replacement;
                return HarborRunnerRegistration.Accept(replacement);
            }
        }

        /// <summary>Get the current session lease for a runner.</summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="session">Current session when connected.</param>
        /// <returns>True when a current session exists.</returns>
        public bool TryGetCurrent(string runnerId, out HarborRunnerSession? session)
        {
            session = null;
            if (String.IsNullOrWhiteSpace(runnerId)) return false;
            lock (_Gate) return _Sessions.TryGetValue(runnerId, out session);
        }

        /// <summary>Check whether a session lease is still current.</summary>
        /// <param name="session">Session lease.</param>
        /// <returns>True only for the active generation.</returns>
        public bool IsCurrent(HarborRunnerSession session)
        {
            if (session == null) return false;
            lock (_Gate)
            {
                return _Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    && Object.ReferenceEquals(current, session);
            }
        }

        /// <summary>
        /// Revalidate a live session against the durable owner and enrollment generation. Durable resolution
        /// is awaited before the registry lock is taken. A session that fails revalidation is removed when it is
        /// still current, and its pending work is canceled, so revocation reaches connected runners even
        /// when it happened on another instance.
        /// </summary>
        /// <param name="session">Session lease to revalidate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Accepted only when the session remains authorized and current; otherwise the stable denial reason.</returns>
        public async Task<HarborRunnerCheck> RevalidateAsync(HarborRunnerSession session, CancellationToken token = default)
        {
            if (session == null) return HarborRunnerCheck.Refuse("runner_session_missing");
            HarborRunnerCheck owner = await CheckSessionOwnerAsync(session, token).ConfigureAwait(false);
            string failureReason = owner.FailureReason;
            bool ownerCurrent = owner.Accepted;
            lock (_Gate)
            {
                bool isCurrent = _Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    && Object.ReferenceEquals(current, session);
                if (!isCurrent)
                    return HarborRunnerCheck.Refuse(String.IsNullOrEmpty(failureReason) ? "runner_session_stale" : failureReason);
                if (ownerCurrent && IsEnrollmentGenerationStale(session.Identity.RunnerId, session.EnrollmentGeneration))
                {
                    ownerCurrent = false;
                    failureReason = "runner_enrollment_generation_stale";
                }
                if (ownerCurrent) return HarborRunnerCheck.Pass;
                _Sessions.Remove(session.Identity.RunnerId);
                InvalidatePendingForSession(session);
                return HarborRunnerCheck.Refuse(failureReason);
            }
        }

        /// <summary>
        /// Disconnect a session only when its generation is still current. A stale link cannot disconnect
        /// a replacement connection.
        /// </summary>
        /// <param name="session">Session lease requesting disconnect.</param>
        /// <returns>True when this session was disconnected.</returns>
        public bool TryDisconnect(HarborRunnerSession session)
        {
            if (session == null) return false;
            lock (_Gate)
            {
                if (!_Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    || !Object.ReferenceEquals(current, session)) return false;
                _Sessions.Remove(session.Identity.RunnerId);
                InvalidatePendingForSession(session);
                return true;
            }
        }

        /// <summary>
        /// Register a typed request owned by the current session generation. The registry creates the
        /// correlation identifier from the generation and a monotonic sequence; callers cannot choose or
        /// reuse an identifier.
        /// </summary>
        /// <param name="session">Current session lease.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The accepted pending request, or the stable denial reason.</returns>
        public async Task<HarborPendingRegistration<T>> RegisterPendingAsync<T>(
            HarborRunnerSession session,
            CancellationToken token = default)
        {
            if (session == null) return HarborPendingRegistration<T>.Refuse("runner_session_missing");

            HarborRunnerCheck owner = await CheckSessionOwnerAsync(session, token).ConfigureAwait(false);
            if (!owner.Accepted) return HarborPendingRegistration<T>.Refuse(owner.FailureReason);

            lock (_Gate)
            {
                if (!_Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    || !Object.ReferenceEquals(current, session))
                    return HarborPendingRegistration<T>.Refuse("runner_session_stale");
                if (IsEnrollmentGenerationStale(session.Identity.RunnerId, session.EnrollmentGeneration))
                    return HarborPendingRegistration<T>.Refuse("runner_enrollment_generation_stale");
                string requestId = "g" + session.Generation.ToString(CultureInfo.InvariantCulture)
                    + "-r" + (++_NextRequestSequence).ToString(CultureInfo.InvariantCulture);
                if (_Pending.ContainsKey(requestId)) return HarborPendingRegistration<T>.Refuse("request_id_duplicate");
                if (_ReplayIds.Contains(requestId)) return HarborPendingRegistration<T>.Refuse("request_id_replayed");

                HarborPendingRequest<T> accepted = new HarborPendingRequest<T>(requestId, session.Identity.RunnerId, session.Generation);
                _Pending[requestId] = new PendingRequest<T>(accepted, session.Identity.RunnerId, session.Generation);
                return HarborPendingRegistration<T>.Accept(accepted);
            }
        }

        /// <summary>
        /// Complete a pending response only when both its runner and connection generation match. A second
        /// response for the same request is rejected as a replay.
        /// </summary>
        /// <param name="session">Session that delivered the response.</param>
        /// <param name="requestId">Correlation identifier.</param>
        /// <param name="response">Typed response.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the response completed its owner.</returns>
        public async Task<bool> CompletePendingAsync<T>(HarborRunnerSession session, string requestId, T response, CancellationToken token = default)
        {
            if (session == null || String.IsNullOrWhiteSpace(requestId)) return false;
            HarborRunnerCheck owner = await CheckSessionOwnerAsync(session, token).ConfigureAwait(false);
            if (!owner.Accepted)
            {
                lock (_Gate)
                {
                    if (_Pending.TryGetValue(requestId, out IPendingRequest? stale)
                        && String.Equals(stale.RunnerId, session?.Identity.RunnerId, StringComparison.Ordinal)
                        && stale.Generation == session?.Generation)
                    {
                        stale.Cancel();
                        _Pending.Remove(requestId);
                        RememberReplay(requestId);
                    }
                }
                return false;
            }
            IPendingRequest? pending;
            lock (_Gate)
            {
                if (!_Pending.TryGetValue(requestId, out pending)) return false;
                if (IsEnrollmentGenerationStale(session.Identity.RunnerId, session.EnrollmentGeneration)) return false;
                if (!String.Equals(pending.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                    || pending.Generation != session.Generation
                    || !_Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    || !Object.ReferenceEquals(current, session)) return false;
                if (!pending.TryComplete(response!)) return false;
                _Pending.Remove(requestId);
                RememberReplay(requestId);
            }

            return true;
        }

        /// <summary>Number of current runner sessions.</summary>
        public int SessionCount
        {
            get { lock (_Gate) return _Sessions.Count; }
        }

        /// <summary>Number of current pending requests.</summary>
        public int PendingCount
        {
            get { lock (_Gate) return _Pending.Count; }
        }

        private void InvalidatePendingForSession(HarborRunnerSession session)
        {
            List<string> invalidated = new List<string>();
            foreach (KeyValuePair<string, IPendingRequest> entry in _Pending)
            {
                if (String.Equals(entry.Value.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                    && entry.Value.Generation == session.Generation)
                    invalidated.Add(entry.Key);
            }
            foreach (string requestId in invalidated)
            {
                if (_Pending.TryGetValue(requestId, out IPendingRequest? pending)) pending.Cancel();
                _Pending.Remove(requestId);
                RememberReplay(requestId);
            }
        }

        private void RememberReplay(string requestId)
        {
            if (_ReplayIds.Add(requestId)) _ReplayOrder.Enqueue(requestId);
            while (_ReplayOrder.Count > ReplayCacheLimit)
            {
                string removed = _ReplayOrder.Dequeue();
                _ReplayIds.Remove(removed);
            }
        }

        private async Task<HarborRunnerOwnerResolution> ResolveOwnerAsync(string runnerId, string unnamedFailure, CancellationToken token)
        {
            HarborRunnerOwnerResolution resolution;
            try
            {
                resolution = await _OwnerResolver!.ResolveOwnerAsync(runnerId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return HarborRunnerOwnerResolution.Failure("runner_owner_unavailable");
            }
            if (resolution == null) return HarborRunnerOwnerResolution.Failure("runner_owner_unavailable");
            if (!resolution.Resolved || resolution.Owner == null)
                return HarborRunnerOwnerResolution.Failure(String.IsNullOrWhiteSpace(resolution.FailureReason) ? unnamedFailure : resolution.FailureReason);
            return resolution;
        }

        private async Task<HarborRunnerCheck> CheckSessionOwnerAsync(HarborRunnerSession session, CancellationToken token)
        {
            if (_OwnerResolver == null) return HarborRunnerCheck.Refuse("runner_owner_unconfigured");
            // The durable enrollment names why the owner no longer resolves, for example a revocation.
            HarborRunnerOwnerResolution resolution = await ResolveOwnerAsync(session.Identity.RunnerId, "runner_owner_unavailable", token).ConfigureAwait(false);
            if (!resolution.Resolved || resolution.Owner == null) return HarborRunnerCheck.Refuse(resolution.FailureReason);
            if (!session.Identity.Matches(resolution.Owner)) return HarborRunnerCheck.Refuse("runner_owner_mismatch");
            if (resolution.Generation != session.EnrollmentGeneration) return HarborRunnerCheck.Refuse("runner_enrollment_generation_stale");
            return HarborRunnerCheck.Pass;
        }

        private void HandleOwnerChanged(string runnerId, long generation)
        {
            if (String.IsNullOrWhiteSpace(runnerId) || generation <= 0) return;
            lock (_Gate)
            {
                if (_LatestEnrollmentGenerations.TryGetValue(runnerId, out long known) && known >= generation) return;
                _LatestEnrollmentGenerations[runnerId] = generation;
                if (_Sessions.TryGetValue(runnerId, out HarborRunnerSession? session)
                    && session.EnrollmentGeneration != generation)
                {
                    InvalidatePendingForSession(session);
                    _Sessions.Remove(runnerId);
                }
            }
        }

        private bool IsEnrollmentGenerationStale(string runnerId, long generation)
        {
            if (!_LatestEnrollmentGenerations.TryGetValue(runnerId, out long known))
            {
                _LatestEnrollmentGenerations[runnerId] = generation;
                return false;
            }
            if (generation > known)
            {
                _LatestEnrollmentGenerations[runnerId] = generation;
                return false;
            }
            return generation < known;
        }

        private interface IPendingRequest
        {
            string RunnerId { get; }
            long Generation { get; }
            bool TryComplete(object response);
            void Cancel();
        }

        private sealed class PendingRequest<T> : IPendingRequest
        {
            private readonly HarborPendingRequest<T> _Request;

            public string RunnerId { get; }
            public long Generation { get; }

            public PendingRequest(HarborPendingRequest<T> request, string runnerId, long generation)
            {
                _Request = request;
                RunnerId = runnerId;
                Generation = generation;
            }

            public bool TryComplete(object response) => _Request.TryComplete(response);
            public void Cancel() => _Request.Cancel();
        }
    }
}
