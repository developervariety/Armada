namespace Armada.Core.Enums
{
    using System;

    /// <summary>
    /// Repository anchors that a preparation claim depends on.
    /// </summary>
    [Flags]
    public enum ObjectivePreparationDependencyEnum
    {
        /// <summary>The claim is independent of repository anchors.</summary>
        None = 0,
        /// <summary>The claim depends on the source anchor.</summary>
        Source = 1,
        /// <summary>The claim depends on the target anchor.</summary>
        Target = 2,
        /// <summary>The claim depends on both anchors.</summary>
        Both = Source | Target
    }
}
