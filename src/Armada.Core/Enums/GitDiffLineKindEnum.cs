namespace Armada.Core.Enums
{
    /// <summary>
    /// The role of one body line inside a unified-diff hunk.
    /// </summary>
    public enum GitDiffLineKindEnum
    {
        /// <summary>An unchanged line present on both sides.</summary>
        Context,

        /// <summary>A line present only after the change.</summary>
        Added,

        /// <summary>A line present only before the change.</summary>
        Removed
    }
}
