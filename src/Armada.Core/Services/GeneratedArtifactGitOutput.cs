namespace Armada.Core.Services
{
    /// <summary>Exit code and output from a candidate-preparation git command.</summary>
    public sealed class GeneratedArtifactGitOutput
    {
        /// <summary>Process exit code.</summary>
        public int ExitCode { get; }

        /// <summary>Standard output, without process credentials.</summary>
        public string StandardOutput { get; }

        /// <summary>Construct a command result.</summary>
        public GeneratedArtifactGitOutput(int exitCode, string standardOutput)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
        }
    }
}
