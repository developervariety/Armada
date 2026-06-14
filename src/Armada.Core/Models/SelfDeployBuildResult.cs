namespace Armada.Core.Models
{
    /// <summary>
    /// Result of a Release build invoked during self-deploy.
    /// </summary>
    public sealed class SelfDeployBuildResult
    {
        /// <summary>
        /// Whether the build process exited with code zero.
        /// </summary>
        public bool Succeeded { get; set; }

        /// <summary>
        /// Process exit code from dotnet build.
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Truncated combined stdout and stderr from the build.
        /// </summary>
        public string OutputTail { get; set; } = String.Empty;
    }
}
