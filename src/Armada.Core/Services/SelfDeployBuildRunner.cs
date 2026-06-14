namespace Armada.Core.Services
{
    using System.Diagnostics;
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
        private const string _Header = "[SelfDeployBuildRunner] ";

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public SelfDeployBuildRunner(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
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
            string arguments = "build \"" + solutionPath + "\" -c " + settings.BuildConfiguration
                + " -f " + settings.TargetFramework;

            _Logging.Info(_Header + "running " + arguments + " in " + workingDirectory);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeoutSource.CancelAfter(TimeSpan.FromSeconds(settings.BuildTimeoutSeconds));
                    try
                    {
                        await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }

                        return new SelfDeployBuildResult
                        {
                            Succeeded = false,
                            ExitCode = -1,
                            OutputTail = "Build timed out after " + settings.BuildTimeoutSeconds + " seconds."
                        };
                    }
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                string combined = stdout;
                if (!String.IsNullOrEmpty(stderr))
                {
                    combined += "\n--- STDERR ---\n" + stderr;
                }

                return new SelfDeployBuildResult
                {
                    Succeeded = process.ExitCode == 0,
                    ExitCode = process.ExitCode,
                    OutputTail = TruncateOutput(combined)
                };
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
