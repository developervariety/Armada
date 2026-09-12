namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Bounded repository preparation that can be delivered to objective dispatch.
    /// </summary>
    public class ObjectivePreparation
    {
        /// <summary>True when dispatch must fail unless the required preparation is verified.</summary>
        public bool RequiredForDispatch { get; set; } = false;

        /// <summary>Claim kinds that must have at least one current evidence-backed claim.</summary>
        public List<ObjectivePreparationClaimKindEnum> RequiredClaimKinds { get; set; } = new List<ObjectivePreparationClaimKindEnum>();

        /// <summary>Sibling repository inputs that the target vessel must provision.</summary>
        public List<ObjectivePreparationSiblingInput> RequiredSiblingInputs { get; set; } = new List<ObjectivePreparationSiblingInput>();

        /// <summary>
        /// Source repository revision inspected during preparation.
        /// </summary>
        public ObjectivePreparationAnchor? Source { get; set; } = null;

        /// <summary>
        /// Target repository revision inspected during preparation.
        /// </summary>
        public ObjectivePreparationAnchor? Target { get; set; } = null;

        /// <summary>
        /// Evidence-backed preparation claims.
        /// </summary>
        public List<ObjectivePreparationClaim> Claims { get; set; } = new List<ObjectivePreparationClaim>();
    }
}
