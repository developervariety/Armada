namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A generic, durable, single-holder lease used to coordinate work across one or more
    /// Admiral instances sharing a database. Replaces the previous in-memory guards (process-exit
    /// dedup, merge-queue processing flag, per-repository provisioning locks) with rows that
    /// survive restarts and are safe under concurrency: acquisition is an atomic compare-and-swap
    /// keyed by <see cref="Name"/>, and a lease whose <see cref="ExpiresUtc"/> has elapsed may be
    /// taken over so a crashed holder never blocks progress forever.
    /// </summary>
    public class CoordinationLease
    {
        #region Public-Members

        /// <summary>
        /// Unique lease name and primary key, e.g. <c>merge-queue:process:{vesselId}</c>,
        /// <c>repo-provision:{repoPath}</c>, or <c>process-exit:{processId}</c>.
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Name));
                _Name = value;
            }
        }

        /// <summary>
        /// Opaque identifier of the current holder (instance id plus a generation stamp). Used to
        /// verify ownership on renewal and release so a stale holder cannot release a lease it no
        /// longer owns.
        /// </summary>
        public string Holder
        {
            get => _Holder;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Holder));
                _Holder = value;
            }
        }

        /// <summary>
        /// Optional tenant scope for the lease.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// UTC time the lease was acquired.
        /// </summary>
        public DateTime AcquiredUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time the lease expires. A lease past this time is eligible for takeover.
        /// </summary>
        public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Name = "";
        private string _Holder = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationLease()
        {
        }

        /// <summary>
        /// Instantiate a lease with a name, holder, and duration.
        /// </summary>
        /// <param name="name">Unique lease name.</param>
        /// <param name="holder">Holder identifier.</param>
        /// <param name="ttl">How long the lease is held before it may be taken over.</param>
        public CoordinationLease(string name, string holder, TimeSpan ttl)
        {
            Name = name;
            Holder = holder;
            AcquiredUtc = DateTime.UtcNow;
            ExpiresUtc = DateTime.UtcNow.Add(ttl);
        }

        #endregion
    }
}
