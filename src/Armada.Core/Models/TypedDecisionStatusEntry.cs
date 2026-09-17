namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>One decision in the typed-decision operator view.</summary>
    public sealed class TypedDecisionStatusEntry
    {
        #region Public-Members

        /// <summary>The decision's settings key.</summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>The decision's own stored mode; the global effective mode caps it.</summary>
        public TypedDecisionModeEnum Mode { get; set; } = TypedDecisionModeEnum.Off;

        /// <summary>The gate threshold.</summary>
        public double Threshold { get; set; } = 0.0;

        /// <summary>What the decision decides.</summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// Why no decision point consults this decision, or null when one does. A mode reported
        /// without this reason means the decision really runs; a decision that ships in a mode and
        /// consults nothing would otherwise read as enforced.
        /// </summary>
        public string? UnwiredReason { get; set; } = null;

        #endregion
    }
}
