namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Operating-system process host that never acts on a bare process id: every check and termination
    /// compares the recorded start time, so a reused id is treated as an exited process and is never signalled.
    /// </summary>
    public sealed class SelfDeployProcessHost : ISelfDeployProcessHost
    {
        /// <summary>
        /// Maximum difference between a recorded and an observed start time for the same process.
        /// </summary>
        public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

        /// <inheritdoc />
        public SelfDeployProcessIdentity? Capture(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited) return null;
                    return new SelfDeployProcessIdentity
                    {
                        ProcessId = processId,
                        StartedUtc = process.StartTime.ToUniversalTime()
                    };
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        /// <inheritdoc />
        public SelfDeployProcessStateEnum GetState(SelfDeployProcessIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (identity.ProcessId <= 0) return SelfDeployProcessStateEnum.Exited;
            Process process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                return SelfDeployProcessStateEnum.Exited;
            }

            using (process)
            {
                return ClassifyLiveProcess(process, identity);
            }
        }

        /// <inheritdoc />
        public SelfDeployProcessIdentity Start(SelfDeployLaunchSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (String.IsNullOrWhiteSpace(spec.FileName)) throw new SelfDeployCutoverException("launch_executable_missing");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (!String.IsNullOrWhiteSpace(spec.WorkingDirectory)) startInfo.WorkingDirectory = spec.WorkingDirectory;
            foreach (string argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
            foreach (KeyValuePair<string, string> variable in spec.EnvironmentVariables)
                startInfo.Environment[variable.Key] = variable.Value;

            Process process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) throw new SelfDeployCutoverException("launch_did_not_start");
                return new SelfDeployProcessIdentity
                {
                    ProcessId = process.Id,
                    StartedUtc = process.StartTime.ToUniversalTime()
                };
            }
            catch (SelfDeployCutoverException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SelfDeployCutoverException("launch_failed", ex);
            }
            finally
            {
                // Disposing the handle releases it without stopping the started process.
                process.Dispose();
            }
        }

        /// <inheritdoc />
        public async Task<bool> TerminateAsync(
            SelfDeployProcessIdentity identity,
            bool entireProcessTree,
            TimeSpan timeout,
            CancellationToken token = default)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            Process process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                return true;
            }

            using (process)
            {
                // Classify on the handle that is signalled, so an id reused since any earlier check is never terminated.
                SelfDeployProcessStateEnum state = ClassifyLiveProcess(process, identity);
                if (state == SelfDeployProcessStateEnum.Exited) return true;
                if (state == SelfDeployProcessStateEnum.Unverified) return false;
                SignalKill(process, entireProcessTree);
            }

            // The kill request is not the evidence; only an observed exit confirms termination.
            SelfDeployProcessStateEnum final = await WaitForExitAsync(
                identity, timeout, TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
            return final == SelfDeployProcessStateEnum.Exited;
        }

        /// <inheritdoc />
        public async Task<SelfDeployProcessStateEnum> WaitForExitAsync(
            SelfDeployProcessIdentity identity,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken token = default)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            TimeSpan delay = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : pollInterval;
            DateTime deadline = DateTime.UtcNow + timeout;
            SelfDeployProcessStateEnum state = GetState(identity);

            // A process that is exiting can be briefly unreadable, so only Exited ends the wait early;
            // the last observed state is returned at the deadline.
            while (state != SelfDeployProcessStateEnum.Exited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                state = GetState(identity);
            }
            return state;
        }

        private static void SignalKill(Process process, bool entireProcessTree)
        {
            try
            {
                process.Kill(entireProcessTree);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the identity check and the signal.
            }
            catch (Exception ex) when (ex is Win32Exception || ex is NotSupportedException || ex is AggregateException)
            {
                if (!entireProcessTree) return;
                try
                {
                    // Descendant enumeration can fail while processes change; still stop the verified root.
                    process.Kill(false);
                }
                catch (Exception rootEx) when (rootEx is InvalidOperationException || rootEx is Win32Exception || rootEx is NotSupportedException)
                {
                    // The caller's exit wait reports the observed outcome; a failed signal leaves the process running.
                }
            }
        }

        private static SelfDeployProcessStateEnum ClassifyLiveProcess(Process process, SelfDeployProcessIdentity identity)
        {
            try
            {
                if (process.HasExited) return SelfDeployProcessStateEnum.Exited;
            }
            catch (InvalidOperationException)
            {
                return SelfDeployProcessStateEnum.Exited;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is NotSupportedException)
            {
                return StillExists(identity.ProcessId) ? SelfDeployProcessStateEnum.Unverified : SelfDeployProcessStateEnum.Exited;
            }

            DateTime observedStartUtc;
            try
            {
                observedStartUtc = process.StartTime.ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return SelfDeployProcessStateEnum.Exited;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is NotSupportedException)
            {
                return StillExists(identity.ProcessId) ? SelfDeployProcessStateEnum.Unverified : SelfDeployProcessStateEnum.Exited;
            }

            TimeSpan difference = observedStartUtc - identity.StartedUtc;
            if (difference.Duration() <= StartTimeTolerance) return SelfDeployProcessStateEnum.Running;

            // A live process with the same id but a different start time is a reused id.
            return SelfDeployProcessStateEnum.Exited;
        }

        private static bool StillExists(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
