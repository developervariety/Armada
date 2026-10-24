namespace Armada.Server.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>Routes bounded workspace operations through the authenticated runner session that owns a checkout.</summary>
    public sealed partial class HarborWorkspaceCoordinator : IHarborWorkspaceOperations
    {
        private readonly object _Gate = new object();
        private readonly Dictionary<string, WorkspaceLink> _Links = new Dictionary<string, WorkspaceLink>(StringComparer.Ordinal);
        private readonly HarborRunnerSessionRegistry _Registry;
        private readonly IHarborRunnerAuthority _Authority;
        private readonly IHarborWorkspaceBindingVerifier _Verifier;
        private readonly LoggingModule _Logging;

        /// <summary>Instantiate the workspace coordinator.</summary>
        /// <param name="registry">Authenticated runner session registry.</param>
        /// <param name="authority">Shared Harbor owner authority rule.</param>
        /// <param name="verifier">Caller and durable checkout verifier.</param>
        /// <param name="logging">Logging module.</param>
        public HarborWorkspaceCoordinator(HarborRunnerSessionRegistry registry, IHarborRunnerAuthority authority, IHarborWorkspaceBindingVerifier verifier, LoggingModule logging)
        {
            _Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _Authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _Verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>Attach a send channel to an accepted workspace-capable session.</summary>
        /// <param name="session">Accepted session lease.</param>
        /// <param name="send">Sender for this WebSocket only.</param>
        public void Attach(HarborRunnerSession session, Func<HarborMessage, CancellationToken, Task> send)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (!session.HasWorkspaceCapability) return;
            lock (_Gate) _Links[session.Identity.RunnerId] = new WorkspaceLink(session, send);
        }

        /// <summary>Detach only the exact connection that attached the channel.</summary>
        /// <param name="session">Closed session lease.</param>
        public void Detach(HarborRunnerSession session)
        {
            if (session == null) return;
            lock (_Gate)
            {
                if (_Links.TryGetValue(session.Identity.RunnerId, out WorkspaceLink? link)
                    && Object.ReferenceEquals(link.Session, session)) _Links.Remove(session.Identity.RunnerId);
            }
        }

        /// <summary>Execute one typed operation after revalidating caller scope, enrollment and checkout binding.</summary>
        /// <param name="auth">Verified API or mission caller.</param>
        /// <param name="request">Typed operation request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The runner result or a stable refusal.</returns>
        public Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth, HarborWorkspaceOperationRequest request, CancellationToken token = default)
        {
            return ExecuteCoreAsync(auth, request, token);
        }

        /// <summary>Complete an outstanding operation only when the exact session and server correlation match.</summary>
        /// <param name="session">Session that delivered the result.</param>
        /// <param name="result">Typed runner result.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this result completed a pending request.</returns>
        public Task<bool> HandleResultAsync(HarborRunnerSession session, HarborWorkspaceOperationResult result, CancellationToken token = default)
        {
            if (session == null || result == null || !session.HasWorkspaceCapability
                || String.IsNullOrWhiteSpace(result.RequestId)
                || !String.Equals(result.CorrelationId, result.RequestId, StringComparison.Ordinal)
                || !IsBound(session)) return Task.FromResult(false);
            if (!ResultWithinLimit(result)) return Task.FromResult(false);
            return _Registry.CompletePendingAsync(session, result.RequestId, result, token);
        }

        private bool TryGetLink(HarborRunnerSession session, out Func<HarborMessage, CancellationToken, Task>? send)
        {
            lock (_Gate)
            {
                if (session.HasWorkspaceCapability
                    && _Links.TryGetValue(session.Identity.RunnerId, out WorkspaceLink? link)
                    && Object.ReferenceEquals(link.Session, session)
                    && _Registry.IsCurrent(session))
                {
                    send = link.Send;
                    return true;
                }
            }
            send = null;
            return false;
        }

        private bool IsBound(HarborRunnerSession session)
        {
            lock (_Gate)
                return _Links.TryGetValue(session.Identity.RunnerId, out WorkspaceLink? link)
                    && Object.ReferenceEquals(link.Session, session) && _Registry.IsCurrent(session);
        }

        private const int MaximumResultBytes = 8 * 1024 * 1024;
        private const int MaximumRequestTimeoutSeconds = 3600;
        private const int VerificationResponseGraceSeconds = 120;
        private static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromSeconds(
            MaximumRequestTimeoutSeconds + VerificationResponseGraceSeconds);
        private const string _Header = "[HarborWorkspaceCoordinator] ";
    }
}
