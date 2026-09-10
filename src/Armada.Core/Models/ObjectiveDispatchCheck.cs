namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One Check that the effective dispatch configuration requires.
    /// </summary>
    public class ObjectiveDispatchCheck
    {
        /// <summary>
        /// Check type.
        /// </summary>
        public CheckRunTypeEnum Type { get; set; } = CheckRunTypeEnum.Build;

        /// <summary>
        /// Resolved command, or null when the workflow profile does not configure it.
        /// </summary>
        public string? Command { get; set; } = null;

        /// <summary>
        /// True when all workflow inputs that apply to dispatch Checks resolve.
        /// </summary>
        public bool RequiredInputsReady { get; set; }
    }
}
