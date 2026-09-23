namespace Armada.Core.Models
{
    /// <summary>
    /// One dependent row, or one dependent list, that a cascade cleanup could not remove, with the reason.
    /// </summary>
    public class CascadeCleanupSkip
    {
        #region Public-Members

        /// <summary>
        /// What was skipped: <c>Event</c>, <c>PlanningSession</c> or <c>RefinementSession</c> for one row, or the
        /// same kind with a <c>List</c> suffix when the rows could not be read at all.
        /// </summary>
        public string Kind { get; set; } = "";

        /// <summary>
        /// Identifier of the skipped row, or of the parent whose dependents could not be read.
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Why the row or list was skipped.
        /// </summary>
        public string Reason { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CascadeCleanupSkip()
        {
        }

        /// <summary>
        /// Instantiate with values.
        /// </summary>
        /// <param name="kind">What was skipped.</param>
        /// <param name="id">Identifier of the skipped row or parent.</param>
        /// <param name="reason">Why it was skipped.</param>
        public CascadeCleanupSkip(string kind, string id, string reason)
        {
            Kind = kind ?? "";
            Id = id ?? "";
            Reason = reason ?? "";
        }

        #endregion
    }
}
