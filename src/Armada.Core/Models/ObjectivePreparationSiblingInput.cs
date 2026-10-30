namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A sibling repository input that must be declared and provisionable before dispatch.
    /// </summary>
    public class ObjectivePreparationSiblingInput
    {
        /// <summary>Registered vessel ID or name that supplies the sibling repository.</summary>
        public string VesselRef { get; set; } = String.Empty;

        /// <summary>Checkout path, relative to the target dock, that the consumer expects.</summary>
        public string RelativePath { get; set; } = String.Empty;

        /// <summary>Extraction artifact paths that the sibling declaration must provision.</summary>
        public List<string> RequiredArtifactPaths { get; set; } = new List<string>();

        /// <summary>
        /// Optional sibling commit the objective's evidence was measured at. Without
        /// <see cref="ProvisionPinnedCommit"/>, dispatch refuses a pin that is not an ancestor of the branch docks
        /// provision, and the brief reports the provisioned HEAD against it; the provisioned HEAD is the tree a
        /// captain reads.
        /// </summary>
        public string? PinnedCommit { get; set; } = null;

        /// <summary>
        /// When true, every dock of the objective's dispatch checks out <see cref="PinnedCommit"/> itself in its own
        /// sibling checkout, from any ref of the sibling repository that holds it, instead of the declared branch.
        /// The pin never lands or moves a sibling ref, and the consumer's landing waits until the pin is an ancestor
        /// of the sibling vessel's default branch. Requires <see cref="PinnedCommit"/>.
        /// </summary>
        public bool ProvisionPinnedCommit { get; set; } = false;
    }
}
