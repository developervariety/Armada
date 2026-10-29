namespace Armada.Core.Models
{

    /// <summary>
    /// A source change a Mutation grader applies; the tests must fail with it in place.
    /// </summary>
    public class BatteryMutant
    {
        #region Public-Members

        /// <summary>
        /// Identifier.
        /// </summary>
        public string? Id { get; set; } = null;

        /// <summary>
        /// File in the worktree.
        /// </summary>
        public string? File { get; set; } = null;

        /// <summary>
        /// Text to replace (must occur in the file).
        /// </summary>
        public string? Find { get; set; } = null;

        /// <summary>
        /// Replacement text.
        /// </summary>
        public string? Replace { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryMutant()
        {
        }

        #endregion
    }
}
