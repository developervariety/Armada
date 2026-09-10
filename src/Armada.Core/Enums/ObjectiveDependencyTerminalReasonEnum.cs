namespace Armada.Core.Enums
{
    /// <summary>
    /// Reason that one objective dependency chain cannot advance.
    /// </summary>
    public enum ObjectiveDependencyTerminalReasonEnum
    {
        /// <summary>The final objective exists but is not complete.</summary>
        Incomplete,

        /// <summary>The final objective identifier does not resolve.</summary>
        Missing,

        /// <summary>The chain returns to an objective already on the same path.</summary>
        Cycle
    }
}
