namespace Armada.Core.Services
{
    using Armada.Core.Models;

    /// <summary>
    /// Pure, stateless evaluator that computes drift between a running server build and the landed commit.
    /// </summary>
    public static class BuildDriftEvaluator
    {
        #region Public-Methods

        /// <summary>
        /// Evaluates drift between the running commit and the landed commit.
        /// </summary>
        /// <param name="runningCommit">The commit SHA the running server was built from.</param>
        /// <param name="landedCommit">The commit SHA of the latest landed main commit.</param>
        /// <param name="behindBy">Number of commits the running build is behind the landed commit.</param>
        /// <returns>A <see cref="BuildDriftReport"/> describing the drift state.</returns>
        public static BuildDriftReport Evaluate(string? runningCommit, string? landedCommit, int behindBy)
        {
            int clampedBehindBy = behindBy < 0 ? 0 : behindBy;

            bool isDrifted = !string.IsNullOrEmpty(runningCommit)
                && !string.IsNullOrEmpty(landedCommit)
                && !string.Equals(runningCommit, landedCommit, StringComparison.OrdinalIgnoreCase);

            string? warning = isDrifted
                ? $"running build is {clampedBehindBy} commits behind landed main -- rebuild + restart to deploy"
                : null;

            return new BuildDriftReport
            {
                RunningCommit = runningCommit,
                LandedCommit = landedCommit,
                BehindBy = clampedBehindBy,
                IsDrifted = isDrifted,
                Warning = warning
            };
        }

        #endregion
    }
}
