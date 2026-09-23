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
        /// Legacy input retained for old update clients. It does not change routing.
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

        #region Private-Members

        private string? _DefaultCaptainId = null;
        private CaptainTierEnum? _MinimumTier = null;

        #endregion
    }
}
