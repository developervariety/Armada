namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Production implementation that shells out to dotnet build for the self-deploy gate.
    /// </summary>
    public sealed class SelfDeployBuildRunner : ISelfDeployBuildRunner
    {
        private readonly LoggingModule _Logging;
        private readonly ISelfDeployNativeCommandRunner _CommandRunner;
        private const string _Header = "[SelfDeployBuildRunner] ";

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="commandRunner">Bounded native command runner.</param>
        public SelfDeployBuildRunner(LoggingModule logging, ISelfDeployNativeCommandRunner? commandRunner = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CommandRunner = commandRunner ?? new SelfDeployNativeCommandRunner();
        }

        /// <inheritdoc />
        public async Task<SelfDeployBuildResult> BuildAsync(
            string workingDirectory,
            SelfDeploySettings settings,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            string solutionPath = Path.Combine(workingDirectory, settings.SolutionRelativePath);
            string[] arguments = new[]
            {
                "build",
                solutionPath,
                "-c",
                settings.BuildConfiguration,
                "-f",
                settings.TargetFramework
            };

            _Logging.Info(_Header + "running dotnet build for " + solutionPath + " in " + workingDirectory);

            using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(settings.BuildTimeoutSeconds));
                try
                {
                    SelfDeployNativeCommandResult result = await _CommandRunner.RunAsync(
                        new SelfDeployNativeCommandRequest
                        {
                            FileName = "dotnet",
                            Arguments = arguments,
                            WorkingDirectory = workingDirectory
                        },
                        timeoutSource.Token).ConfigureAwait(false);

                    string combined = result.StandardOutput ?? String.Empty;
                    if (!String.IsNullOrEmpty(result.StandardError))
                    {
                        combined += "\n--- STDERR ---\n" + result.StandardError;
                    }

                    string outputTail = TruncateOutput(combined);
                    bool outputWasTruncated = result.StandardOutput.Contains(
                        SelfDeployNativeCommandRunner.OutputTruncationMarker, StringComparison.Ordinal)
                        || result.StandardError.Contains(
                            SelfDeployNativeCommandRunner.OutputTruncationMarker, StringComparison.Ordinal);
                    if (outputWasTruncated && !outputTail.Contains(
                        SelfDeployNativeCommandRunner.OutputTruncationMarker, StringComparison.Ordinal))
                    {
                        outputTail += "\n" + SelfDeployNativeCommandRunner.OutputTruncationMarker;
                    }

                    return new SelfDeployBuildResult
                    {
                        Succeeded = result.Succeeded,
                        ExitCode = result.ExitCode,
                        OutputTail = outputTail
                    };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                {
                    return new SelfDeployBuildResult
                    {
                        Succeeded = false,
                        ExitCode = -1,
                        OutputTail = "Build timed out after " + settings.BuildTimeoutSeconds + " seconds."
                    };
                }
            }
        }

        private static string TruncateOutput(string output)
        {
            if (String.IsNullOrEmpty(output)) return String.Empty;
            if (output.Length <= 4096) return output;
            return output.Substring(output.Length - 4096);
        }
    }
}
