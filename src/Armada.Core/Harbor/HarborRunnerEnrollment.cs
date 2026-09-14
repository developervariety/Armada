namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Durable administrator-controlled binding between a Harbor runner and one verified principal.
    /// The record contains identifiers only. It never stores a credential secret.
    /// </summary>
    public sealed class HarborRunnerEnrollment
    {
        #region Public-Members

        /// <summary>Runner identifier.</summary>
        public string RunnerId
        {
            get { return _RunnerId; }
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(RunnerId));
                _RunnerId = value.Trim();
            }
        }

        /// <summary>Owning tenant identifier.</summary>
        public string TenantId
        {
            get { return _TenantId; }
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(TenantId));
                _TenantId = value.Trim();
            }
        }

        /// <summary>Owning user identifier.</summary>
        public string UserId
        {
            get { return _UserId; }
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(UserId));
                _UserId = value.Trim();
            }
        }

        /// <summary>Authentication method bound to the runner.</summary>
        public string AuthMethod
        {
            get { return _AuthMethod; }
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(AuthMethod));
                _AuthMethod = value.Trim();
            }
        }

        /// <summary>Credential identifier bound to the runner, when the method uses a credential.</summary>
        public string? CredentialId { get; set; }

        /// <summary>Monotonic enrollment generation. Re-enrollment increments this value.</summary>
        public long Generation { get; set; } = 1;

        /// <summary>Whether this enrollment can authorize a new session.</summary>
        public bool Active { get; set; } = true;

        /// <summary>Creation timestamp in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Last state change timestamp in UTC.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Revocation timestamp in UTC, when revoked.</summary>
        public DateTime? RevokedUtc { get; set; }

        /// <summary>Administrator user identifier that revoked the enrollment, when revoked.</summary>
        public string? RevokedByUserId { get; set; }

        #endregion

        #region Private-Members

        private string _RunnerId = String.Empty;
        private string _TenantId = String.Empty;
        private string _UserId = String.Empty;
        private string _AuthMethod = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate an enrollment.</summary>
        public HarborRunnerEnrollment()
        {
        }

        #endregion
    }
}
