namespace Armada.Core.Models
{
    using Armada.Core.Settings;

    /// <summary>
    /// Inputs for the self-deploy safety preflight.
    /// </summary>
    public sealed class SelfDeployPreflightRequest
    {
        /// <summary>
        /// Self-hosted vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>
        /// Working directory containing the candidate build.
        /// </summary>
        public string WorkingDirectory { get; set; } = String.Empty;

        /// <summary>
        /// Absolute path to the candidate server assembly.
        /// </summary>
        public string CandidateServerDllPath { get; set; } = String.Empty;

        /// <summary>
        /// Self-deploy settings used for the candidate build.
        /// </summary>
        public SelfDeploySettings Settings { get; set; } = new SelfDeploySettings();

        /// <summary>
        /// Successful build result that produced the candidate.
        /// </summary>
        public SelfDeployBuildResult BuildResult { get; set; } = new SelfDeployBuildResult();
    }
}
