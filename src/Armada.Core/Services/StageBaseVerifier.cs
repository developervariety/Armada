namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// What a stage's checkout proved about the commit it was supposed to build on.
    /// </summary>
    public enum StageBaseVerdictEnum
    {
        /// <summary>The mission is not a downstream pipeline stage, so there is no base to check.</summary>
        NotApplicable = 0,

        /// <summary>The checkout contains the upstream stage's commit.</summary>
        Verified = 1,

        /// <summary>
        /// The upstream stage produced no commit, or ancestry could not be determined. Nothing is
        /// proved either way, so the stage proceeds and the fact is recorded.
        /// </summary>
        Unverifiable = 2,

        /// <summary>
        /// The checkout demonstrably does NOT contain the upstream stage's commit. The stage would
        /// rebuild on a base its predecessor already moved past.
        /// </summary>
        BaseMissing = 3
    }

    /// <summary>
    /// Verifies that a pipeline stage is cut from the commit its predecessor produced.
    /// </summary>
    /// <remarks>
    /// A stage inherits the upstream branch during handoff, but inheriting a branch NAME is not
    /// the same as inheriting its commit: a local ref can predate the upstream stage's push, and
    /// the resulting worktree then looks correct while missing the work.
    /// <para>
    /// The failure is expensive and reads as something else entirely. A Worker's dock was cut from
    /// the target branch without the preceding stage's commit, so it rebuilt on a base that still
    /// carried errors its predecessor had already fixed, failed on them, and took ten downstream
    /// missions down with it. Every symptom pointed at the Worker's own code.
    /// </para>
    /// <para>
    /// Silence is the thing being removed here. A stage that cannot prove its base is not treated
    /// as correct; it is either proved, or the gap is stated.
    /// </para>
    /// </remarks>
    public static class StageBaseVerifier
    {
        #region Public-Methods

        /// <summary>
        /// Decide whether a stage's checkout carries its predecessor's work.
        /// </summary>
        /// <param name="dependsOnMissionId">The upstream mission, if this is a pipeline stage.</param>
        /// <param name="upstreamCommitHash">
        /// The commit the upstream stage produced. Empty when the upstream legitimately produced
        /// none, which an Audit or Research stage does.
        /// </param>
        /// <param name="dependencyIsCrossVessel">
        /// Whether the dependency lives in a different repository. Commits cannot be shared across
        /// repositories, so ancestry says nothing there.
        /// </param>
        /// <param name="checkoutContainsUpstreamCommit">
        /// Whether the provisioned checkout contains that commit. Null when ancestry could not be
        /// determined.
        /// </param>
        /// <returns>The verdict.</returns>
        public static StageBaseVerdictEnum Evaluate(
            string? dependsOnMissionId,
            string? upstreamCommitHash,
            bool dependencyIsCrossVessel,
            bool? checkoutContainsUpstreamCommit)
        {
            if (String.IsNullOrWhiteSpace(dependsOnMissionId)) return StageBaseVerdictEnum.NotApplicable;

            // A different repository has a different commit graph; ancestry is meaningless across
            // one, and demanding it would fail every legitimate cross-vessel stage.
            if (dependencyIsCrossVessel) return StageBaseVerdictEnum.NotApplicable;

            // An upstream stage that produced no commit is normal for report-only work. There is
            // nothing to inherit, so there is nothing to fail.
            if (String.IsNullOrWhiteSpace(upstreamCommitHash)) return StageBaseVerdictEnum.Unverifiable;

            if (checkoutContainsUpstreamCommit == null) return StageBaseVerdictEnum.Unverifiable;

            return checkoutContainsUpstreamCommit.Value
                ? StageBaseVerdictEnum.Verified
                : StageBaseVerdictEnum.BaseMissing;
        }

        /// <summary>
        /// Build the operator-facing explanation for a stage that is missing its base.
        /// </summary>
        /// <param name="dependsOnMissionId">The upstream mission.</param>
        /// <param name="upstreamCommitHash">The commit the checkout should have contained.</param>
        /// <param name="branchName">The branch the stage was provisioned on.</param>
        /// <returns>A reason naming the commit, so the diagnosis does not start at the code.</returns>
        public static string BuildBaseMissingReason(
            string? dependsOnMissionId,
            string? upstreamCommitHash,
            string? branchName)
        {
            return "stage_base_missing: this stage was provisioned on branch '"
                + (branchName ?? "(unknown)")
                + "' which does NOT contain commit " + Shorten(upstreamCommitHash)
                + " produced by upstream stage " + (dependsOnMissionId ?? "(unknown)")
                + ". The stage would rebuild on a base its predecessor already moved past, and would"
                + " fail on problems that upstream has already fixed. This is a provisioning fault,"
                + " not a defect in the stage's own work.";
        }

        #endregion

        #region Private-Methods

        private static string Shorten(string? commitHash)
        {
            if (String.IsNullOrWhiteSpace(commitHash)) return "(unknown)";
            string trimmed = commitHash.Trim();
            return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
        }

        #endregion
    }
}
