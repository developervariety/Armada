namespace Armada.Core.Enums
{
    /// <summary>
    /// How an operator dispatch must treat a dispatch preview once the preflight rule is applied.
    /// </summary>
    public enum PreflightGateOutcomeEnum
    {
        /// <summary>The preview has no blocking issue; dispatch proceeds.</summary>
        Ready,

        /// <summary>A blocking issue other than an incomplete preflight is present; dispatch is refused and a force flag cannot override it.</summary>
        BlockedByOther,

        /// <summary>Only an incomplete preflight blocks dispatch and no force flag was given; dispatch is refused.</summary>
        BlockedByPreflight,

        /// <summary>Only an incomplete preflight blocked dispatch and the operator forced it; dispatch proceeds and the override is recorded.</summary>
        OverriddenPreflight
    }
}
