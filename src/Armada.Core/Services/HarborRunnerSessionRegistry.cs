namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Fail-closed registry for authenticated Harbor runner sessions and their typed pending responses.
    /// This core has no network or process-launch behavior; transport adapters must present the session
    /// lease returned here for every request, response, and disconnect. Enabled use also requires an
    /// authoritative owner resolver backed by durable runner enrollment.
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
        /// <param name="session">New session lease when accepted.</param>
        /// <param name="failureReason">Stable denial reason when rejected.</param>
        /// <returns>True when registered.</returns>
        public bool TryRegister(
            string runnerId,
            AuthContext auth,
            out HarborRunnerSession? session,
            out string failureReason)
        {
            session = null;
            failureReason = String.Empty;
            if (!Enabled)
            {
                failureReason = "runner_registration_disabled";
                return false;
            }
            if (auth == null)
            {
                failureReason = "runner_identity_unverified";
                return false;
            }

            HarborRunnerIdentity identity;
            try
            {
                identity = HarborRunnerIdentity.FromVerifiedAuthContext(runnerId, auth);
            }
            catch (UnauthorizedAccessException)
            {
                failureReason = "runner_identity_unverified";
                return false;
            }
            catch (ArgumentException)
            {
                failureReason = "runner_id_invalid";
                return false;
            }

            if (_OwnerResolver == null)
            {
                failureReason = "runner_owner_unconfigured";
                return false;
            }

            AuthContext? ownerAuth;
            long enrollmentGeneration;
            try
            {
                if (!TryResolveOwner(identity.RunnerId, out ownerAuth, out enrollmentGeneration) || ownerAuth == null)
                {
                    failureReason = "runner_owner_unknown";
                    return false;
                }
            }
            catch
            {
                failureReason = "runner_owner_unavailable";
                return false;
            }

            if (!identity.Matches(ownerAuth))
            {
                failureReason = "runner_owner_mismatch";
                return false;
            }

            lock (_Gate)
            {
                if (IsEnrollmentGenerationStale(identity.RunnerId, enrollmentGeneration))
                {
                    failureReason = "runner_enrollment_generation_stale";
                    return false;
                }
                if (_Sessions.TryGetValue(identity.RunnerId, out HarborRunnerSession? existing)
                    && (!String.Equals(existing.Identity.TenantId, identity.TenantId, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.UserId, identity.UserId, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.AuthMethod, identity.AuthMethod, StringComparison.Ordinal)
                        || !String.Equals(existing.Identity.CredentialId, identity.CredentialId, StringComparison.Ordinal)))
                {
                    failureReason = "runner_identity_conflict";
                    return false;
                }

                if (existing != null) InvalidatePendingForSession(existing);
                HarborRunnerSession replacement = new HarborRunnerSession(identity, ++_NextGeneration, enrollmentGeneration);
                _Sessions[identity.RunnerId] = replacement;
                session = replacement;
                return true;
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
        /// <param name="pending">Pending response when accepted.</param>
        /// <param name="failureReason">Stable denial reason when rejected.</param>
        /// <returns>True when registered.</returns>
        public bool TryRegisterPending<T>(
            HarborRunnerSession session,
            out HarborPendingRequest<T>? pending,
            out string failureReason)
        {
            pending = null;
            failureReason = String.Empty;
            if (session == null)
            {
                failureReason = "runner_session_missing";
                return false;
            }

            if (!SessionOwnerIsCurrent(session, out failureReason)) return false;

            lock (_Gate)
            {
                if (!_Sessions.TryGetValue(session.Identity.RunnerId, out HarborRunnerSession? current)
                    || !Object.ReferenceEquals(current, session))
                {
                    failureReason = "runner_session_stale";
                    return false;
                }
                if (IsEnrollmentGenerationStale(session.Identity.RunnerId, session.EnrollmentGeneration))
                {
                    failureReason = "runner_enrollment_generation_stale";
                    return false;
                }
                string requestId = "g" + session.Generation.ToString(CultureInfo.InvariantCulture)
                    + "-r" + (++_NextRequestSequence).ToString(CultureInfo.InvariantCulture);
                if (_Pending.ContainsKey(requestId))
                {
                    failureReason = "request_id_duplicate";
                    return false;
                }
                if (_ReplayIds.Contains(requestId))
                {
                    failureReason = "request_id_replayed";
                    return false;
                }

                HarborPendingRequest<T> accepted = new HarborPendingRequest<T>(requestId, session.Identity.RunnerId, session.Generation);
                _Pending[requestId] = new PendingRequest<T>(accepted, session.Identity.RunnerId, session.Generation);
                pending = accepted;
                return true;
            }
        }

        /// <summary>
        /// Complete a pending response only when both its runner and connection generation match. A second
        /// response for the same request is rejected as a replay.
        /// </summary>
        /// <param name="session">Session that delivered the response.</param>
        /// <param name="requestId">Correlation identifier.</param>
        /// <param name="response">Typed response.</param>
        /// <returns>True when the response completed its owner.</returns>
        public bool TryCompletePending<T>(HarborRunnerSession session, string requestId, T response)
        {
            if (session == null || String.IsNullOrWhiteSpace(requestId)) return false;
            if (!SessionOwnerIsCurrent(session, out _))
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

        private bool TryResolveOwner(string runnerId, out AuthContext? owner, out long enrollmentGeneration)
        {
            enrollmentGeneration = 0;
            if (_OwnerResolver is IHarborRunnerOwnerGenerationResolver versioned)
                return versioned.TryGetOwner(runnerId, out owner, out enrollmentGeneration);
            return _OwnerResolver!.TryGetOwner(runnerId, out owner);
        }

        private bool SessionOwnerIsCurrent(HarborRunnerSession session, out string failureReason)
        {
            failureReason = String.Empty;
            AuthContext? owner;
            long enrollmentGeneration;
            try
            {
                if (!TryResolveOwner(session.Identity.RunnerId, out owner, out enrollmentGeneration) || owner == null)
                {
                    failureReason = "runner_owner_unavailable";
                    return false;
                }
            }
            catch
            {
                failureReason = "runner_owner_unavailable";
                return false;
            }
            if (!session.Identity.Matches(owner))
            {
                failureReason = "runner_owner_mismatch";
                return false;
            }
            if (_OwnerResolver is IHarborRunnerOwnerGenerationResolver
                && enrollmentGeneration != session.EnrollmentGeneration)
            {
                failureReason = "runner_enrollment_generation_stale";
                return false;
            }
            return true;
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
