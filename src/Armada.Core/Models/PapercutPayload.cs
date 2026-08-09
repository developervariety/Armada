namespace Armada.Core.Models
{
    /// <summary>
    /// Raw captain-supplied papercut fields, before validation. Every field is a string: a captain can
    /// emit an unknown category or a misspelled severity, and an unknown value must degrade to a
    /// default rather than throw away the whole report.
    /// </summary>
    public class PapercutPayload
    {
        #region Public-Members

        /// <summary>
        /// Category name.
        /// </summary>
        public string? Category { get; set; } = null;

        /// <summary>
        /// Severity name.
        /// </summary>
        public string? Severity { get; set; } = null;

        /// <summary>
        /// One-line summary.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Longer explanation.
        /// </summary>
        public string? Detail { get; set; } = null;

        /// <summary>
        /// Repository-relative path.
        /// </summary>
        public string? Path { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PapercutPayload()
        {
        }

        #endregion
    }
}
