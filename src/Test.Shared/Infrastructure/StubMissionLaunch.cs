namespace Test.Shared.Infrastructure
{
    /// <summary>
    /// One mission launch of <see cref="StubCaptainRuntime"/>, handed to <see cref="StubCaptainBehavior.OnMission"/> so a
    /// test can script a different outcome per attempt.
    /// </summary>
    public sealed class StubMissionLaunch
    {
        #region Public-Members

        /// <summary>
        /// The launch's working directory (the dock).
        /// </summary>
        public string WorkingDirectory { get; }

        /// <summary>
        /// The launch prompt.
        /// </summary>
        public string Prompt { get; }

        /// <summary>
        /// One-based count of mission launches the behavior has seen, across all missions.
        /// </summary>
        public int LaunchNumber { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="workingDirectory">Working directory.</param>
        /// <param name="prompt">Prompt.</param>
        /// <param name="launchNumber">One-based launch count.</param>
        public StubMissionLaunch(string workingDirectory, string prompt, int launchNumber)
        {
            WorkingDirectory = workingDirectory;
            Prompt = prompt;
            LaunchNumber = launchNumber;
        }

        #endregion
    }
}
