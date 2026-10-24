namespace Armada.Runtimes.Harbor
{
    using System;

    /// <summary>Validates the fixed aggregate reservation for retained Harbor output spools.</summary>
    public static class HarborSupervisorStoragePolicy
    {
        /// <summary>Maximum output-spool space reserved by one runner.</summary>
        public const long MaximumAggregateSpoolBytes = 1024L * 1024L * 1024L;

        /// <summary>Validate per-job and aggregate spool bounds for client and standalone supervisor modes.</summary>
        /// <param name="options">Supervisor storage settings.</param>
        public static void Validate(HarborSupervisorOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaxSpoolBytes < 1024 || options.MaxSpoolBytes > MaximumAggregateSpoolBytes)
                throw new ArgumentOutOfRangeException(nameof(options.MaxSpoolBytes), "The per-job spool limit is outside its allowed range.");
            if (options.MaxRetainedJobs < 1 || options.MaxRetainedJobs > 1024)
                throw new ArgumentOutOfRangeException(nameof(options.MaxRetainedJobs), "The retained job limit is outside its allowed range.");
            if (options.MaxVerificationSpoolBytes < 0 || options.MaxVerificationSpoolBytes > MaximumAggregateSpoolBytes)
                throw new ArgumentOutOfRangeException(nameof(options.MaxVerificationSpoolBytes), "The verification spool reservation is outside its allowed range.");
            long jobReservation = checked(options.MaxSpoolBytes * options.MaxRetainedJobs);
            if (jobReservation > MaximumAggregateSpoolBytes - options.MaxVerificationSpoolBytes)
                throw new ArgumentException("The configured aggregate spool reservation exceeds 1 GiB.", nameof(options));
        }
    }
}
