namespace Armada.Core.Enums
{
    /// <summary>
    /// Scope at which a project profile applies.
    /// </summary>
    public enum ProjectProfileScopeEnum
    {
        /// <summary>
        /// Profile is generally available within the tenant.
        /// </summary>
        Global = 0,

        /// <summary>
        /// Profile applies to a specific fleet.
        /// </summary>
        Fleet = 1,

        /// <summary>
        /// Profile applies to a specific vessel (project).
        /// </summary>
        Vessel = 2
    }
}
