namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Concurrent;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using Armada.Core.Harbor;
    using Armada.Core.Services.Interfaces;

    /// <summary>Executes authenticated, bounded operations only within configured repository grants.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private const int MaximumRequestTextBytes = 2 * 1024 * 1024;
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int MaximumEntries = 10000;
        private const int MaximumVerificationSeconds = 3600;
        private readonly HarborRunnerSettings _Settings;
        private readonly HarborCheckoutJournal _Journal;
        private readonly HarborJobJournal _Jobs;
        private readonly HarborWorkspaceOperationJournal _Operations;
        private readonly HarborVerificationOutputJournal _VerificationOutputs;
        private readonly IGitService? _Git;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _CheckoutLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>Create a runner workspace handler from validated local settings.</summary>
        /// <param name="settings">Runner identity and repository grants.</param>
        public HarborRunnerWorkspace(HarborRunnerSettings settings, IGitService? git = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Settings.Validate();
            _Journal = new HarborCheckoutJournal(_Settings.Supervisor.StateDirectory);
            _Jobs = new HarborJobJournal(_Settings.Supervisor);
            _Operations = new HarborWorkspaceOperationJournal(_Settings.Supervisor.StateDirectory);
            _VerificationOutputs = new HarborVerificationOutputJournal(_Settings.Supervisor);
            _Git = git;
        }

        /// <summary>Return the exact SHA-256 repository fingerprint used by server checkout bindings.</summary>
        /// <param name="repositoryUrl">Configured repository URL.</param>
        /// <returns>Lowercase hexadecimal SHA-256 digest of trimmed UTF-8 URL bytes.</returns>
        public static string RepositoryFingerprint(string repositoryUrl)
        {
            if (String.IsNullOrWhiteSpace(repositoryUrl)) throw new ArgumentException("A repository URL is required.", nameof(repositoryUrl));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(repositoryUrl.Trim()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static HarborJobJournalException Failure(string reason, string message)
            => new HarborJobJournalException(reason, message);
    }
}
