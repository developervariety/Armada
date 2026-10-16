namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Features of the installed Mux CLI that depend on its version, read once from <c>mux --version</c> and cached for
    /// <see cref="CacheDuration"/> so an upgrade is picked up without restarting the Admiral.
    /// </summary>
    public static class MuxCapabilities
    {
        #region Public-Members

        /// <summary>
        /// First Mux version with <c>mux print --permission-prompt-tool</c> (and <c>--permission-prompt-timeout</c>,
        /// <c>--auto-approve-tools</c>).
        /// </summary>
        public static readonly Version PermissionPromptToolVersion = new Version(1, 5, 0);

        /// <summary>
        /// How long a probed version is reused before <c>mux --version</c> runs again.
        /// </summary>
        public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Replaces the <c>mux --version</c> probe (tests, or hosts that know the version another way). It returns the
        /// command's output, or null when Mux is not installed. Null (the default) runs the command. Setting it clears
        /// the cache.
        /// </summary>
        public static Func<string?>? VersionProbe
        {
            get => _VersionProbe;
            set
            {
                lock (_Lock)
                {
                    _VersionProbe = value;
                    _CachedAtUtc = null;
                    _CachedVersion = null;
                }
            }
        }

        #endregion

        #region Private-Members

        private const string Executable = "mux";
        private const int ProbeTimeoutMs = 5000;
        private static readonly Regex _VersionPattern = new Regex(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
        private static readonly object _Lock = new object();
        private static Func<string?>? _VersionProbe = null;
        private static DateTime? _CachedAtUtc = null;
        private static Version? _CachedVersion = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The installed Mux version, or null when Mux is not installed or its version cannot be read.
        /// </summary>
        /// <returns>The version, or null.</returns>
        public static Version? GetInstalledVersion()
        {
            lock (_Lock)
            {
                DateTime now = DateTime.UtcNow;
                if (_CachedAtUtc.HasValue && now - _CachedAtUtc.Value < CacheDuration) return _CachedVersion;

                string? output;
                try
                {
                    output = _VersionProbe != null ? _VersionProbe() : RunVersionCommand();
                }
                catch (Exception)
                {
                    output = null;
                }

                _CachedVersion = ParseVersion(output);
                _CachedAtUtc = now;
                return _CachedVersion;
            }
        }

        /// <summary>
        /// Whether the installed Mux supports <c>--permission-prompt-tool</c>, so Approve in Armada can send a Mux
        /// captain's tool calls to Armada.
        /// </summary>
        /// <returns>True for Mux <see cref="PermissionPromptToolVersion"/> or later.</returns>
        public static bool SupportsPermissionPromptTool()
        {
            Version? version = GetInstalledVersion();
            return version != null && version >= PermissionPromptToolVersion;
        }

        /// <summary>
        /// Read the first "major.minor.patch" in <c>mux --version</c> output (for example "mux v1.5.0").
        /// </summary>
        /// <param name="output">Command output, or null.</param>
        /// <returns>The version, or null when the output has none.</returns>
        public static Version? ParseVersion(string? output)
        {
            if (String.IsNullOrWhiteSpace(output)) return null;
            Match match = _VersionPattern.Match(output);
            if (!match.Success) return null;
            return new Version(Int32.Parse(match.Groups[1].Value), Int32.Parse(match.Groups[2].Value), Int32.Parse(match.Groups[3].Value));
        }

        #endregion

        #region Private-Methods

        private static string? RunVersionCommand()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            if (process == null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!output.Wait(ProbeTimeoutMs) || !process.WaitForExit(ProbeTimeoutMs))
            {
                try { process.Kill(true); } catch (Exception) { }
                return null;
            }

            return output.Result;
        }

        #endregion
    }
}
