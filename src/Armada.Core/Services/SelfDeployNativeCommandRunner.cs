namespace Armada.Core.Services
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Runs a provider utility with argument-list execution and environment-only credentials.
    /// </summary>
    public sealed class SelfDeployNativeCommandRunner : ISelfDeployNativeCommandRunner
    {
        /// <summary>Maximum captured characters per native output stream.</summary>
        public const int MaximumOutputCharacters = 256 * 1024;

        /// <summary>Marker appended when a native output stream exceeds its capture limit.</summary>
        public const string OutputTruncationMarker = "[ARMADA: native command output truncated]";

        /// <summary>Default time to wait for redirected pipes to drain after the utility exits.</summary>
        public static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan _CommandCeiling = TimeSpan.FromHours(24);
        private static readonly TimeSpan _KillWaitTimeout = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _OutputDrainTimeout;

        /// <summary>Create a runner with the default pipe drain timeout.</summary>
        public SelfDeployNativeCommandRunner()
            : this(DefaultOutputDrainTimeout)
        {
        }

        /// <summary>
        /// Create a runner with an explicit pipe drain timeout: how long to wait for redirected pipes to close after
        /// the utility exits before failing with <c>native_command_io_drain_timeout</c>.
        /// </summary>
        public SelfDeployNativeCommandRunner(TimeSpan outputDrainTimeout)
        {
            if (outputDrainTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(outputDrainTimeout), "Must be positive.");
            _OutputDrainTimeout = outputDrainTimeout;
        }

        /// <inheritdoc />
        public async Task<SelfDeployNativeCommandResult> RunAsync(
            SelfDeployNativeCommandRequest request,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("Command executable is required.", nameof(request));

            ProcessStartInfo startInfo = new ProcessStartInfo(request.FileName);
            if (!String.IsNullOrWhiteSpace(request.WorkingDirectory))
                startInfo.WorkingDirectory = request.WorkingDirectory;

            foreach (string argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> variable in request.EnvironmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }

            // The caller's token carries the operation's own deadline; the runner's timeout is only a ceiling.
            // Each stream keeps its beginning, so a noisy provider cannot pin server memory, and a pipe a
            // descendant still holds after exit fails the command closed instead of hanging it.
            BoundedProcessRequest run = new BoundedProcessRequest(startInfo, _CommandCeiling)
            {
                OutputLimitBytes = MaximumOutputCharacters,
                OutputShape = BoundedOutputShapeEnum.Head,
                TruncationMarker = "\n" + OutputTruncationMarker,
                OutputDrainTimeout = _OutputDrainTimeout,
                KillDrainTimeout = _KillWaitTimeout,
                // A cancellation also kills a background child the utility left behind.
                OwnProcessGroup = true
            };
            if (!String.IsNullOrWhiteSpace(request.StandardInputFilePath))
            {
                string inputPath = request.StandardInputFilePath!;
                run.StandardInputWriter = (input, inputToken) => CopyFileToStandardInputAsync(inputPath, input, inputToken);
            }

            BoundedProcessResult result;
            try
            {
                result = await BoundedProcessRunner.RunAsync(run, token).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                // ENOENT / ERROR_FILE_NOT_FOUND (2) and ERROR_PATH_NOT_FOUND (3) mean the tool is not installed;
                // any other start failure (for example EACCES) means it exists but cannot be executed.
                bool missing = ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3;
                throw new SelfDeployNativeClientMissingException(request.FileName, !missing, ex);
            }

            if (result.Cancelled || result.TimedOut)
            {
                if (result.KillError != null) throw new InvalidOperationException("native_command_termination_failed");
                if (result.StillRunningAfterKill) throw new InvalidOperationException("native_command_termination_timeout");
                if (result.OutputDrainTimedOut) throw new InvalidOperationException("native_command_io_drain_timeout");
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("native command exceeded " + _CommandCeiling.TotalHours.ToString("0") + " hours");
            }

            if (result.OutputDrainTimedOut) throw new InvalidOperationException("native_command_io_drain_timeout");
            if (result.StandardInputError != null)
                throw new InvalidOperationException("native_command_input_failed: " + result.StandardInputError);

            return new SelfDeployNativeCommandResult
            {
                ExitCode = result.ExitCode ?? -1,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError
            };
        }

        private static async Task CopyFileToStandardInputAsync(string path, Stream input, CancellationToken token)
        {
            using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await source.CopyToAsync(input, 81920, token).ConfigureAwait(false);
            }
        }
    }
}
