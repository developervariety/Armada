namespace Armada.Core.Services
{
    using System.Diagnostics;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Spawns the external watchdog script as a detached process.
    /// </summary>
    public sealed class SelfDeploySupervisor : ISelfDeploySupervisor
    {
        private readonly LoggingModule _Logging;
        private const string _Header = "[SelfDeploySupervisor] ";

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public SelfDeploySupervisor(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public Task<bool> RequestSupervisedRestartAsync(
            string workingDirectory,
            int admiralProcessId,
            string serverDllPath,
            string supervisorScriptPath,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (admiralProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(admiralProcessId));
            if (String.IsNullOrWhiteSpace(serverDllPath)) throw new ArgumentNullException(nameof(serverDllPath));
            if (String.IsNullOrWhiteSpace(supervisorScriptPath)) throw new ArgumentNullException(nameof(supervisorScriptPath));

            if (!File.Exists(supervisorScriptPath))
            {
                _Logging.Warn(_Header + "supervisor script not found: " + supervisorScriptPath);
                return Task.FromResult(false);
            }

            if (!File.Exists(serverDllPath))
            {
                _Logging.Warn(_Header + "server dll not found: " + serverDllPath);
                return Task.FromResult(false);
            }

            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                string scriptArgs = "-WorkingDirectory \"" + workingDirectory + "\" -AdmiralPid " + admiralProcessId
                    + " -ServerDll \"" + serverDllPath + "\"";
                startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + supervisorScriptPath + "\" " + scriptArgs,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                string scriptArgs = "--working-directory \"" + workingDirectory + "\" --admiral-pid " + admiralProcessId
                    + " --server-dll \"" + serverDllPath + "\"";
                startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "\"" + supervisorScriptPath + "\" " + scriptArgs,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            Process process = new Process { StartInfo = startInfo };
            process.Start();
            _Logging.Info(_Header + "spawned supervisor pid " + process.Id + " for admiral pid " + admiralProcessId);
            return Task.FromResult(true);
        }
    }
}
