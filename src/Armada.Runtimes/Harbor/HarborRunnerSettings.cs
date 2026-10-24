namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Collections.Generic;

    /// <summary>Local settings for a headless Harbor runner client.</summary>
    public sealed class HarborRunnerSettings
    {
        /// <summary>Stable runner identifier enrolled with the Admiral.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Display name sent during link handshake.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Full WebSocket URI for the Admiral Harbor link.</summary>
        public string LinkUri { get; set; } = String.Empty;

        /// <summary>Bearer credential sent only in the Harbor link authorization header.</summary>
        public string Credential { get; set; } = String.Empty;

        /// <summary>Local policy for the per-job supervisor.</summary>
        public HarborSupervisorOptions Supervisor { get; set; } = new HarborSupervisorOptions();

        /// <summary>Explicit local repository grants available to typed checkout operations.</summary>
        public List<HarborRepositoryGrant> RepositoryGrants { get; set; } = new List<HarborRepositoryGrant>();

        /// <summary>Maximum jobs advertised to the Admiral.</summary>
        public int MaxConcurrentJobs { get; set; } = 4;

        /// <summary>Heartbeat interval in seconds.</summary>
        public int HeartbeatIntervalSeconds { get; set; } = 10;

        /// <summary>Delay between reconnect attempts in seconds.</summary>
        public int ReconnectDelaySeconds { get; set; } = 2;

        /// <summary>Validate the runner identity, link URI, and local supervisor limits.</summary>
        public void Validate()
        {
            if (String.IsNullOrWhiteSpace(RunnerId) || RunnerId.Length > 160)
                throw new ArgumentException("RunnerId must contain between 1 and 160 characters.", nameof(RunnerId));
            if (String.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 160)
                throw new ArgumentException("Name must contain between 1 and 160 characters.", nameof(Name));
            if (String.IsNullOrWhiteSpace(Credential) || Credential.Length > 4096 || Credential.Any(character => Char.IsWhiteSpace(character) || Char.IsControl(character)))
                throw new ArgumentException("Credential must contain between 1 and 4096 characters.", nameof(Credential));
            if (MaxConcurrentJobs < 1 || MaxConcurrentJobs > 256)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentJobs), "Must be in range [1, 256].");
            if (HeartbeatIntervalSeconds < 1 || HeartbeatIntervalSeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalSeconds), "Must be in range [1, 60].");
            if (ReconnectDelaySeconds < 1 || ReconnectDelaySeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(ReconnectDelaySeconds), "Must be in range [1, 300].");

            if (!Uri.TryCreate(LinkUri, UriKind.Absolute, out Uri? linkUri)
                || linkUri == null
                || (linkUri.Scheme != "wss" && linkUri.Scheme != "ws")
                || !String.IsNullOrEmpty(linkUri.UserInfo)
                || !String.IsNullOrEmpty(linkUri.Query)
                || !String.IsNullOrEmpty(linkUri.Fragment)
                || (linkUri.Scheme == "ws" && !linkUri.IsLoopback))
                throw new ArgumentException("LinkUri must use wss, or ws on loopback, and must not contain user information, a query, or a fragment.", nameof(LinkUri));

            if (Supervisor == null) throw new ArgumentNullException(nameof(Supervisor));
            if (RepositoryGrants == null) throw new ArgumentNullException(nameof(RepositoryGrants));
            if (String.IsNullOrWhiteSpace(Supervisor.StateDirectory))
                throw new ArgumentException("Supervisor.StateDirectory is required.", nameof(Supervisor));
            if (Supervisor.WorkingRoots == null || Supervisor.WorkingRoots.Count == 0)
                throw new ArgumentException("Supervisor.WorkingRoots must contain at least one path.", nameof(Supervisor));
            if (Supervisor.Commands == null || Supervisor.Commands.Count == 0)
                throw new ArgumentException("Supervisor.Commands must contain at least one runtime command.", nameof(Supervisor));
            HarborSupervisorStoragePolicy.Validate(Supervisor);
            if (MaxConcurrentJobs > Supervisor.MaxRetainedJobs)
                throw new ArgumentException("MaxConcurrentJobs cannot exceed Supervisor.MaxRetainedJobs.", nameof(MaxConcurrentJobs));
            foreach (string root in Supervisor.WorkingRoots)
            {
                if (String.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                    throw new ArgumentException("Every supervisor working root must be an absolute path.", nameof(Supervisor.WorkingRoots));
            }
            foreach (System.Collections.Generic.KeyValuePair<string, string> command in Supervisor.Commands)
            {
                if (String.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 80
                    || String.IsNullOrWhiteSpace(command.Value) || !Path.IsPathFullyQualified(command.Value) || !File.Exists(command.Value))
                    throw new ArgumentException("Supervisor.Commands must contain named runtime commands.", nameof(Supervisor.Commands));
            }

            HashSet<string> grantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarborRepositoryGrant grant in RepositoryGrants)
            {
                if (grant == null) throw new ArgumentException("RepositoryGrants cannot contain null entries.", nameof(RepositoryGrants));
                grant.Validate();
                if (!grantIds.Add(grant.Id))
                    throw new ArgumentException("Repository grant identifiers must be unique.", nameof(RepositoryGrants));
            }
        }
    }
}
