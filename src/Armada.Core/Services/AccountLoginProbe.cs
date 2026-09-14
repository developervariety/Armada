namespace Armada.Core.Services
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Runs a runtime's own login status command inside an account's login home, with a bounded lifetime. Only runtimes
    /// whose status command reports the login of that home in a machine-readable way are probed: Claude Code
    /// (<c>claude auth status --json</c>) and Codex (<c>codex login status</c>). OpenCode's <c>opencode auth list</c>
    /// exits 0 with human-readable text whether or not a credential exists, and <c>cursor-agent status</c> reports the
    /// stored login and ignores CURSOR_API_KEY, so those runtimes keep the file or variable check. Command output is
    /// read only to decide the result; it is never logged, stored, or returned.
    /// </summary>
    public static class AccountLoginProbe
    {
        #region Public-Members

        /// <summary>The login file is present but the runtime reports the home as not logged in: expired or revoked.</summary>
        public const string ReasonLoginExpired = "account_login_expired";

        /// <summary>The status command did not finish within its timeout.</summary>
        public const string ReasonProbeTimeout = "account_login_probe_timeout";

        /// <summary>The runtime CLI could not be started.</summary>
        public const string ReasonProbeUnavailable = "account_login_probe_unavailable";

        /// <summary>The status command gave a result this probe cannot interpret.</summary>
        public const string ReasonProbeFailed = "account_login_probe_failed";

        #endregion

        #region Private-Members

        private const int _MaxOutputChars = 65536;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Public-Methods

        /// <summary>True when the runtime has a status command that reports the login of one home.</summary>
        public static bool HasStatusCommand(AgentRuntimeEnum runtime)
        {
            return runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.Codex;
        }

        /// <summary>Default CLI name for a probed runtime.</summary>
        public static string DefaultExecutable(AgentRuntimeEnum runtime)
        {
            return runtime switch
            {
                AgentRuntimeEnum.ClaudeCode => "claude",
                AgentRuntimeEnum.Codex => "codex",
                _ => throw new ArgumentException("Runtime " + runtime + " has no login status command.", nameof(runtime))
            };
        }

        /// <summary>
        /// Probe one account. Returns null when the runtime reports the home as logged in, or when the account has no
        /// probed login; otherwise a safe reason code. Never throws for a probe outcome; cancellation still propagates.
        /// </summary>
        /// <param name="account">Account whose login home is probed.</param>
        /// <param name="executable">CLI to run; normally <see cref="DefaultExecutable"/>.</param>
        /// <param name="timeout">Bound on the whole command.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task<string?> RunAsync(UsageAccountSettings account, string executable, TimeSpan timeout, CancellationToken token = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (String.IsNullOrWhiteSpace(executable)) throw new ArgumentNullException(nameof(executable));
            if (!CaptainAccountLaunch.HasLaunchIdentity(account) || !HasStatusCommand(account.Runtime!.Value) || String.IsNullOrWhiteSpace(account.HomeDirectory)) return null;
            AgentRuntimeEnum runtime = account.Runtime.Value;

            ProcessStartInfo info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            if (runtime == AgentRuntimeEnum.ClaudeCode)
            {
                info.ArgumentList.Add("auth");
                info.ArgumentList.Add("status");
                info.ArgumentList.Add("--json");
                info.Environment[CaptainAccountLaunch.ClaudeConfigDirVariable] = account.HomeDirectory;
            }
            else
            {
                info.ArgumentList.Add("login");
                info.ArgumentList.Add("status");
                info.Environment[CaptainAccountLaunch.CodexHomeVariable] = account.HomeDirectory;
            }

            using (Process process = new Process { StartInfo = info })
            using (CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    if (!process.Start()) return ReasonProbeUnavailable;
                }
                catch (Win32Exception)
                {
                    return ReasonProbeUnavailable;
                }
                catch (FileNotFoundException)
                {
                    return ReasonProbeUnavailable;
                }

                process.StandardInput.Close();
                bound.CancelAfter(timeout);
                Task<string> stdout = ReadBoundedAsync(process.StandardOutput);
                Task<string> stderr = ReadBoundedAsync(process.StandardError);
                try
                {
                    await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { /* The process exited between the timeout and the kill. */ }
                    catch (Win32Exception) { /* The process tree is already gone; the timeout result stands. */ }
                    // The kill closes the pipes; wait briefly so the readers finish and nothing is left unobserved.
                    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { /* A detached grandchild still holds a pipe; the timeout result stands. */ }
                    catch (IOException) { /* The pipe broke during the kill; the timeout result stands. */ }
                    catch (ObjectDisposedException) { /* The pipe closed during the kill; the timeout result stands. */ }
                    token.ThrowIfCancellationRequested();
                    return ReasonProbeTimeout;
                }

                string output = await stdout.ConfigureAwait(false);
                string errors = await stderr.ConfigureAwait(false);
                return runtime == AgentRuntimeEnum.ClaudeCode
                    ? InterpretClaude(process.ExitCode, output)
                    : InterpretCodex(process.ExitCode, output + "\n" + errors);
            }
        }

        #endregion

        #region Private-Methods

        private static string? InterpretClaude(int exitCode, string output)
        {
            ClaudeStatus? status;
            try { status = JsonSerializer.Deserialize<ClaudeStatus>(output, _Json); }
            catch (JsonException) { return ReasonProbeFailed; }
            if (status?.LoggedIn == true && exitCode == 0) return null;
            if (status?.LoggedIn == false) return ReasonLoginExpired;
            return ReasonProbeFailed;
        }

        private static string? InterpretCodex(int exitCode, string output)
        {
            if (exitCode == 0) return null;
            return output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ? ReasonLoginExpired : ReasonProbeFailed;
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            StringBuilder text = new StringBuilder();
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                // Keep draining past the bound so the child never blocks on a full pipe.
                if (text.Length < _MaxOutputChars) text.Append(buffer, 0, Math.Min(read, _MaxOutputChars - text.Length));
            }
            return text.ToString();
        }

        private sealed class ClaudeStatus
        {
            public bool? LoggedIn { get; set; }
        }

        #endregion
    }
}
