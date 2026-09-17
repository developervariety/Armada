namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>A capacity reading for one mission: the model list to try first and where the reading came from.</summary>
    public sealed class CapacityReading
    {
        #region Public-Members

        /// <summary>The model list to try first.</summary>
        public CapacityChoiceEnum Choice { get; set; } = CapacityChoiceEnum.Default;

        /// <summary>The reading's source code.</summary>
        public string Source { get; set; } = String.Empty;

        #endregion
    }
}
