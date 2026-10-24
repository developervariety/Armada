namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Owns the private, bounded local journal for Harbor supervisor jobs.</summary>
    public sealed partial class HarborJobJournal
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };
        private readonly HarborSupervisorOptions _Options;
        private readonly string _Root;

        /// <summary>Validate policy and open the private state root.</summary>
        /// <param name="options">Supervisor settings.</param>
        public HarborJobJournal(HarborSupervisorOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            ValidateOptions(options);
            _Options = new HarborSupervisorOptions
            {
                StateDirectory = options.StateDirectory,
                WorkingRoots = new List<string>(options.WorkingRoots),
                Commands = new Dictionary<string, string>(options.Commands, StringComparer.Ordinal),
                MaxSpoolBytes = options.MaxSpoolBytes,
                MaxRetainedJobs = options.MaxRetainedJobs,
                MaxVerificationSpoolBytes = options.MaxVerificationSpoolBytes
            };
            try { _Root = EnsurePrivateRoot(_Options.StateDirectory); }
            catch (HarborJobJournalException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                throw Failure("state_directory_unavailable", "The private journal directory cannot be created or inspected.", exception);
            }
            using (FileStream admission = AcquireAdmissionLock()) EnsureStoragePolicyPinned();
        }

        private static readonly object _Sync = new object();
    }
}
