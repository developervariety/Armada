namespace Test.Shared.Suites.E2E
{
    /// <summary>
    /// The fields of GET /api/v1/settings that <see cref="PartialUpdateApiSuite"/> reads.
    /// </summary>
    public sealed class RequireHarborSettingsView
    {
        /// <summary>Whether missions wait for a Harbor instead of running on the Admiral host.</summary>
        public bool RequireHarborForLaunch { get; set; } = false;

        /// <summary>Maximum captains (used for an unrelated update).</summary>
        public int MaxCaptains { get; set; } = 0;
    }
}
