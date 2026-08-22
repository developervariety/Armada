namespace Armada.Core.Enums
{
    /// <summary>
    /// How much of a mission's change set could actually address a defect.
    /// </summary>
    public enum ChangeSubstanceEnum
    {
        /// <summary>
        /// No files changed at all. The mission ran and left the tree as it found it.
        /// </summary>
        None = 0,

        /// <summary>
        /// Only documentation or narrative files changed. The work was described, not done.
        /// </summary>
        DocumentationOnly = 1,

        /// <summary>
        /// At least one file that can carry behavior changed.
        /// </summary>
        Substantive = 2
    }
}
