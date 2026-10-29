namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads the version string a runtime executable prints for <c>--version</c>. One bounded probe runs per
    /// executable and its answer is cached, so a launch never pays for a probe it already ran. A launch waits a few
    /// seconds at most: a slow probe keeps running in the background, fills the cache for the next launch, and the
    /// launch records no version. The probe never throws.
    /// </summary>
    public sealed class RuntimeVersionProbe
    {
        #region Public-Members

        /// <summary>Longest a caller waits for the answer before it proceeds without one.</summary>
        public static readonly TimeSpan CallerWait = TimeSpan.FromSeconds(3);

        /// <summary>Longest the probe process itself may run.</summary>
        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>How long a version answer stays cached.</summary>
        public static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(30);

        /// <summary>How long an empty answer (missing executable, failure, timeout) stays cached.</summary>
        public static readonly TimeSpan EmptyLifetime = TimeSpan.FromMinutes(5);

        /// <summary>Maximum stored length of a version string.</summary>
        public const int MaximumVersionLength = 120;

        #endregion

        #region Private-Members

        private readonly Func<string, CancellationToken, Task<string?>> _Runner;
        private readonly Func<DateTime> _UtcNow;
        private readonly TimeSpan _CallerWait;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, Entry> _Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate a probe that runs the real executable.</summary>
        public RuntimeVersionProbe() : this(null, null, null)
        {
        }

        /// <summary>Instantiate a probe with replaceable collaborators.</summary>
        /// <param name="runner">Runs one probe for an executable and returns its raw version line, or null.</param>
        /// <param name="utcNow">Clock.</param>
        /// <param name="callerWait">How long a caller waits; defaults to <see cref="CallerWait"/>.</param>
        public RuntimeVersionProbe(Func<string, CancellationToken, Task<string?>>? runner, Func<DateTime>? utcNow, TimeSpan? callerWait)
        {
            _Runner = runner ?? RunProcessAsync;
            _UtcNow = utcNow ?? (() => DateTime.UtcNow);
            _CallerWait = callerWait ?? CallerWait;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the cached version of an executable, probing it when no fresh answer exists.
        /// </summary>
        /// <param name="command">Executable name or path, as the runtime launches it.</param>
        /// <param name="token">Cancels the wait. The probe itself keeps running.</param>
        /// <returns>The trimmed version line, or null when the probe is unavailable, failed, or is still running.</returns>
        public async Task<string?> GetVersionAsync(string? command, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(command)) return null;

            Entry entry;
            lock (_Lock)
            {
                if (!_Entries.TryGetValue(command, out Entry? existing) || existing.IsExpired(_UtcNow()))
                {
                    existing = new Entry();
                    _Entries[command] = existing;
                    existing.Probe = ProbeAsync(command, existing);
                }
                entry = existing;
            }

            Task finished = await Task.WhenAny(entry.Probe, Task.Delay(_CallerWait, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(finished, entry.Probe)) return null;
            return await entry.Probe.ConfigureAwait(false);
        }

        /// <summary>
        /// Reduce raw probe output to one bounded line: the first non-empty line, trimmed.
        /// </summary>
        /// <param name="output">Raw output.</param>
        /// <returns>The version line, or null when the output holds none.</returns>
        public static string? Normalize(string? output)
        {
            if (String.IsNullOrWhiteSpace(output)) return null;
            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed.Length > MaximumVersionLength ? trimmed.Substring(0, MaximumVersionLength) : trimmed;
            }
            return null;
        }

        #endregion

        #region Private-Methods

        private async Task<string?> ProbeAsync(string command, Entry entry)
        {
            string? version = null;
            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(ProbeTimeout))
                {
                    version = Normalize(await _Runner(command, timeout.Token).ConfigureAwait(false));
                }
            }
            catch (Exception)
            {
                // An unavailable or failing probe is the empty answer; the cache keeps it for EmptyLifetime so a
                // missing executable is not re-run on every launch.
                version = null;
            }

            entry.ExpiresUtc = _UtcNow() + (version == null ? EmptyLifetime : SuccessLifetime);
            return version;
        }

        private static async Task<string?> RunProcessAsync(string command, CancellationToken token)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo { FileName = command };
            startInfo.ArgumentList.Add("--version");
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, ProbeTimeout)
            {
                OutputLimitBytes = 4096
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.TimedOut || result.Cancelled || result.ExitCode != 0) return null;
            return String.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        }

        #endregion

        #region Private-Types

        private sealed class Entry
        {
            public Task<string?> Probe = Task.FromResult<string?>(null);
            public DateTime? ExpiresUtc = null;

            public bool IsExpired(DateTime nowUtc)
            {
                return ExpiresUtc.HasValue && nowUtc >= ExpiresUtc.Value;
            }
        }

        #endregion
    }
}
