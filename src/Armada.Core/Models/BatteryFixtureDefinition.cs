namespace Armada.Core.Models
{

    /// <summary>
    /// Where a pack's fixture repository content lives.
    /// </summary>
    public class BatteryFixtureDefinition
    {
        #region Public-Members

        /// <summary>
        /// Directory, relative to the pack directory, whose files become the fixture repository.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Branch of the fixture repository that every task starts from.
        /// </summary>
        public string DefaultBranch
        {
            get => _DefaultBranch;
            set => _DefaultBranch = value ?? "main";
        }

        #endregion

        #region Private-Members

        private string _DefaultBranch = "main";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryFixtureDefinition()
        {
        }

        #endregion
    }
}
