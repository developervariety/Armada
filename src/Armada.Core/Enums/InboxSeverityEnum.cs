namespace Armada.Core.Enums
{
    /// <summary>
    /// Severity of a "needs you" inbox item.
    /// </summary>
    public enum InboxSeverityEnum
    {
        /// <summary>
        /// Informational; no urgent action required.
        /// </summary>
        Info = 0,

        /// <summary>
        /// Something needs attention.
        /// </summary>
        Warning = 1,

        /// <summary>
        /// Something is blocking progress and needs prompt action.
        /// </summary>
        Critical = 2
    }
}
