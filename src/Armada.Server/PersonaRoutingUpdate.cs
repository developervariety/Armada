namespace Armada.Server
{
    /// <summary>
    /// The routing fields of a persona update request. A null member leaves the stored value unchanged, so a
    /// request that omits the field never clears it.
    /// </summary>
    public class PersonaRoutingUpdate
    {
        #region Public-Members

        /// <summary>
        /// Whether missions of this persona are routed only to Premium captains. Null leaves the stored value unchanged.
        /// </summary>
        public bool? Specialist { get; set; } = null;

        #endregion
    }
}
