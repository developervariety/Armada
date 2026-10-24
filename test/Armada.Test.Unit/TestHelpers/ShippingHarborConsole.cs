namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Runtimes.Harbor;

    /// <summary>Runs the shipping console and preserves its independent supervisors when the link process stops.</summary>
    public sealed class ShippingHarborConsole : IAsyncDisposable
    {
        private readonly Process _Process;
        private readonly Task<string> _Errors;
        private readonly bool _OwnsJournalJobs;

        /// <summary>Settings used by this console instance.</summary>
        public HarborRunnerSettings Settings { get; }

        /// <summary>Journal written by its real supervisor processes.</summary>
        public HarborJobJournal Journal { get; }

        /// <summary>Whether the console process has exited.</summary>
        public bool HasExited => _Process.HasExited;

        /// <summary>Exit code after the console stops.</summary>
        public int ExitCode => _Process.ExitCode;

        /// <summary>Start a console with a private configuration in the fixture root.</summary>
        /// <param name="root">Fixture directory retained across console restarts.</param>
        /// <param name="linkUri">Authenticated test Admiral endpoint.</param>
        /// <param name="credential">Credential enrolled for the test runner.</param>
        /// <param name="ownsJournalJobs">Whether this fixture owns cleanup of retained jobs; false for an expected duplicate console.</param>
        public ShippingHarborConsole(string root, string linkUri, string credential, bool ownsJournalJobs = true)
        {
            _OwnsJournalJobs = ownsJournalJobs;
            Directory.CreateDirectory(root);
            string work = Path.Combine(root, "work");
            Directory.CreateDirectory(work);
            HarborPrivateStorage.EnsurePrivateDirectory(root);
            Settings = new HarborRunnerSettings
            {
                RunnerId = "hbr_a", Name = "shipping test runner", LinkUri = linkUri, Credential = credential,
                HeartbeatIntervalSeconds = 1, ReconnectDelaySeconds = 1,
                Supervisor = new HarborSupervisorOptions
                {
                    StateDirectory = Path.Combine(root, "state"), WorkingRoots = new List<string> { work },
                    Commands = new Dictionary<string, string> { ["fixture"] = DotnetHost() }
                }
            };
            Journal = new HarborJobJournal(Settings.Supervisor);
            string config = Path.Combine(root, "runner.json");
            File.WriteAllText(config, JsonSerializer.Serialize(Settings));
            HarborPrivateStorage.EnsurePrivateFile(config);
            ProcessStartInfo start = new ProcessStartInfo(DotnetHost())
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = work
            };
            start.ArgumentList.Add(ConsoleAssembly());
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(config);
            start.Environment["UNRELATED_RUNNER_AUTH"] = credential;
            _Process = Process.Start(start) ?? throw new InvalidOperationException("The shipping Harbor console did not start.");
            _Errors = _Process.StandardError.ReadToEndAsync();
            _ = _Process.StandardOutput.ReadToEndAsync();
        }

        /// <summary>Stop only the link console, leaving its job supervisors alive.</summary>
        public async Task StopConsoleOnlyAsync()
        {
            if (!_Process.HasExited) _Process.Kill(false);
            await _Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        /// <summary>Read bounded diagnostic text after the console exits.</summary>
        /// <returns>Console errors, or an active-process marker.</returns>
        public string Diagnostics()
        {
            if (!_Process.HasExited) return "console is still running";
            return _Errors.IsCompletedSuccessfully ? _Errors.Result : "console stderr has not closed";
        }

        /// <summary>Stop the fixture console and every exact local child it created.</summary>
        public async ValueTask DisposeAsync()
        {
            await StopConsoleOnlyAsync().ConfigureAwait(false);
            foreach (HarborLocalJobRecord record in _OwnsJournalJobs ? Journal.Enumerate() : new List<HarborLocalJobRecord>())
            {
                if (!record.IsRunning) continue;
                Journal.WriteStopRequest(record.JobId, record.LaunchKey, 0);
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (Journal.Read(record.JobId).IsRunning && DateTime.UtcNow < deadline)
                    await Task.Delay(20).ConfigureAwait(false);
                HarborLocalJobRecord latest = Journal.Read(record.JobId);
                if (latest.IsRunning && latest.ProcessId.HasValue && latest.ProcessStartedUtc.HasValue
                    && HarborJobJournal.IsExactProcessAlive(latest.ProcessId.Value, latest.ProcessStartedUtc.Value,
                        latest.ProcessLinuxStartTicks, latest.ProcessLinuxBootId))
                {
                    using (Process child = Process.GetProcessById(latest.ProcessId.Value))
                    {
                        if (!child.HasExited && HarborJobJournal.IsExactProcessAlive(latest.ProcessId.Value, latest.ProcessStartedUtc.Value,
                            latest.ProcessLinuxStartTicks, latest.ProcessLinuxBootId))
                            child.Kill(true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
            }
            _Process.Dispose();
        }

        /// <summary>Find the actual .NET host used to run fixture children.</summary>
        /// <returns>An absolute executable path.</returns>
        public static string DotnetHost()
        {
            string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!String.IsNullOrEmpty(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            string filename = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            if (String.Equals(Path.GetFileName(Environment.ProcessPath), filename, StringComparison.OrdinalIgnoreCase)) return Environment.ProcessPath!;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? String.Empty).Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(directory, filename);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new FileNotFoundException("The .NET host for the shipping runner fixture was not found.");
        }

        private static string ConsoleAssembly()
        {
            DirectoryInfo framework = new DirectoryInfo(AppContext.BaseDirectory);
            string configuration = framework.Parent!.Name;
            DirectoryInfo? root = framework;
            while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "Armada.sln"))) root = root.Parent;
            if (root == null) throw new DirectoryNotFoundException("The fixture source root was not found.");
            string assembly = Path.Combine(root.FullName, "src", "Armada.Harbor", "bin", configuration, framework.Name, "Armada.Harbor.dll");
            if (!File.Exists(assembly)) throw new FileNotFoundException("The shipping Harbor console was not built.", assembly);
            return assembly;
        }
    }
}
