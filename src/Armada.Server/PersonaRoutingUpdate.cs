namespace Armada.Server
{
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// The routing fields of a persona update request. A member the request omits leaves the stored value
    /// unchanged, so a request that omits the field never clears it.
    /// </summary>
    public class PersonaRoutingUpdate
    {
        #region Public-Members

        /// <summary>
        /// Error code for a request that still sends the retired <c>specialist</c> flag.
        /// </summary>
        public const string SpecialistRetiredErrorCode = "specialist_retired";

        /// <summary>
        /// The retired specialist flag. It no longer changes routing, so a request that sends it is refused by
        /// <see cref="RetiredFieldError"/> instead of being accepted and ignored.
        /// </summary>
        public bool? Specialist { get; set; } = null;

        /// <summary>Minimum capability tier; an explicit null clears the floor.</summary>
        public CaptainTierEnum? MinimumTier
        {
            get => _MinimumTier;
            set { _MinimumTier = value; MinimumTierSupplied = true; }
        }

        [JsonIgnore]
        public bool MinimumTierSupplied { get; private set; }

        /// <summary>
        /// Requested default captain id. Only meaningful when <see cref="DefaultCaptainIdSupplied"/> is true: a null
        /// or empty value then clears the default captain.
        /// </summary>
        public string? DefaultCaptainId
        {
            get
            {
                return _DefaultCaptainId;
            }
            set
            {
                _DefaultCaptainId = value;
                DefaultCaptainIdSupplied = true;
            }
        }

        /// <summary>
        /// True when the request carried the default captain member, including an explicit null. An omitted member
        /// leaves the stored default captain unchanged.
        /// </summary>
        [JsonIgnore]
        public bool DefaultCaptainIdSupplied { get; private set; } = false;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The refusal for a request that sends a retired routing field, or null when it sends none. Every persona
        /// create and update entry point calls this before it writes anything.
        /// </summary>
        /// <returns>The error message naming <see cref="SpecialistRetiredErrorCode"/>, or null.</returns>
        public string? RetiredFieldError()
        {
            if (!Specialist.HasValue) return null;
            return SpecialistRetiredErrorCode + ": the specialist flag no longer changes routing. "
                + "Set minimumTier (Economy, Standard or Premium, or null for no floor) instead.";
        }

        #endregion

        #region Private-Members

        private string? _DefaultCaptainId = null;
        private CaptainTierEnum? _MinimumTier = null;

        #endregion
    }
}
