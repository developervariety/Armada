namespace Armada.Server
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The routing fields of a persona update request. A member the request omits leaves the stored value
    /// unchanged, so a request that omits the field never clears it.
    /// </summary>
    public class PersonaRoutingUpdate
    {
        #region Public-Members

        /// <summary>
        /// Whether missions of this persona are routed only to Premium captains. Null leaves the stored value unchanged.
        /// </summary>
        public bool? Specialist { get; set; } = null;

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

        #endregion
    }
}
