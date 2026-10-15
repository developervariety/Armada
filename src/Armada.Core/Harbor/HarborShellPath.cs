namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Gives the Harbor process the PATH the user's terminal has. A Harbor started from Finder, the Dock, or a login item
    /// gets launchd's minimal PATH (/usr/bin:/bin:/usr/sbin:/sbin), which leaves out where agent CLIs live (claude in
    /// ~/.local/bin, mux in ~/.dotnet/tools, Homebrew tools in /opt/homebrew/bin), so their launches fail. At startup on
    /// macOS and Linux the Harbor asks the user's login shell for its PATH (with a timeout), puts it ahead of the PATH it
    /// was started with, and adds the usual tool folders that exist. The shell runner and directory check are injectable
    /// for tests.
    /// </summary>
    public class HarborShellPath
    {
        #region Public-Members

        /// <summary>
        /// Marker printed before the shell's PATH, so text a profile prints (a banner, a warning) is ignored.
        /// </summary>
        public const string StartMarker = "__ARMADA_PATH_START__";

        /// <summary>
        /// Marker printed after the shell's PATH.
        /// </summary>
        public const string EndMarker = "__ARMADA_PATH_END__";

        /// <summary>
        /// How long to wait for the login shell. Minimum 100 ms; defaults to 5 seconds.
        /// </summary>
        public TimeSpan Timeout
        {
            get => _Timeout;
            set => _Timeout = value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : value;
        }

        /// <summary>
        /// Runs a shell command and returns its standard output, or null when it failed or timed out. Arguments: the shell
        /// executable, its arguments, and the timeout. Defaults to running the process directly.
        /// </summary>
        public Func<string, List<string>, TimeSpan, string?> RunShell
        {
            get => _RunShell;
            set => _RunShell = value ?? throw new ArgumentNullException(nameof(RunShell));
        }

        /// <summary>
        /// Whether a directory exists. Defaults to <see cref="Directory.Exists(string)"/>.
        /// </summary>
        public Func<string, bool> DirectoryExists
        {
            get => _DirectoryExists;
            set => _DirectoryExists = value ?? throw new ArgumentNullException(nameof(DirectoryExists));
        }

        #endregion

        #region Private-Members

        private TimeSpan _Timeout = TimeSpan.FromSeconds(5);
        private Func<string, List<string>, TimeSpan, string?> _RunShell = RunProcess;
        private Func<string, bool> _DirectoryExists = Directory.Exists;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the default shell runner and directory check.
        /// </summary>
        public HarborShellPath()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The folders agent CLIs are commonly installed in, under the given home directory: ~/.local/bin (claude),
        /// ~/.dotnet/tools (mux), /opt/homebrew/bin and /usr/local/bin (Homebrew, npm globals).
        /// </summary>
        /// <param name="home">The user's home directory, or null or empty to leave out the per-user folders.</param>
        /// <returns>The folders, in the order they are added.</returns>
        public static List<string> CommonDirectories(string? home)
        {
            List<string> directories = new List<string>();
            if (!String.IsNullOrWhiteSpace(home))
            {
                directories.Add(System.IO.Path.Combine(home, ".local", "bin"));
                directories.Add(System.IO.Path.Combine(home, ".dotnet", "tools"));
            }

            directories.Add("/opt/homebrew/bin");
            directories.Add("/usr/local/bin");
            return directories;
        }

        /// <summary>
        /// Read the PATH a login shell has: run <paramref name="shell"/> -l -c with a command that prints the PATH between
        /// markers, and return what is between them.
        /// </summary>
        /// <param name="shell">The shell executable (for example /bin/zsh).</param>
        /// <param name="error">Why the PATH could not be read, or null.</param>
        /// <returns>The PATH, or null.</returns>
        public string? ReadLoginShellPath(string shell, out string? error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(shell))
            {
                error = "no login shell";
                return null;
            }

            List<string> arguments = new List<string> { "-l", "-c", "printf '" + StartMarker + "%s" + EndMarker + "' \"$PATH\"" };
            string? output;
            try
            {
                output = _RunShell(shell, arguments, _Timeout);
            }
            catch (Exception e)
            {
                error = "could not run " + shell + ": " + e.Message;
                return null;
            }

            if (output == null)
            {
                error = shell + " did not answer within " + (int)_Timeout.TotalSeconds + "s";
                return null;
            }

            int start = output.IndexOf(StartMarker, StringComparison.Ordinal);
            int end = start < 0 ? -1 : output.IndexOf(EndMarker, start + StartMarker.Length, StringComparison.Ordinal);
            if (start < 0 || end < 0)
            {
                error = shell + " did not print its PATH";
                return null;
            }

            string path = output.Substring(start + StartMarker.Length, end - start - StartMarker.Length).Trim();
            if (path.Length == 0)
            {
                error = shell + " has an empty PATH";
                return null;
            }

            return path;
        }

        /// <summary>
        /// Merge PATHs: the login shell's entries first (the order the user's terminal resolves commands in), then entries
        /// of the current PATH it lacks, then the common folders that exist and are still missing. Empty and repeated
        /// entries are dropped.
        /// </summary>
        /// <param name="currentPath">The process's PATH, or null.</param>
        /// <param name="loginPath">The login shell's PATH, or null.</param>
        /// <param name="extraDirectories">Further folders to add when they exist, or null.</param>
        /// <returns>The merged PATH.</returns>
        public string Merge(string? currentPath, string? loginPath, IEnumerable<string>? extraDirectories)
        {
            List<string> merged = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            AddEntries(merged, seen, loginPath, false);
            AddEntries(merged, seen, currentPath, false);
            if (extraDirectories != null)
            {
                foreach (string directory in extraDirectories) AddEntries(merged, seen, directory, true);
            }

            return String.Join(System.IO.Path.PathSeparator.ToString(), merged);
        }

        /// <summary>
        /// Compute the PATH the Harbor should run with on this platform: on Windows the current PATH unchanged; on macOS and
        /// Linux the merge of the login shell's PATH (from $SHELL, else /bin/zsh on macOS or /bin/bash), the current PATH,
        /// and the common folders. Does not change the process environment.
        /// </summary>
        /// <param name="currentPath">The process's PATH, or null.</param>
        /// <param name="shell">The login shell ($SHELL), or null or empty for the platform default.</param>
        /// <param name="home">The user's home directory, or null.</param>
        /// <param name="isWindows">Whether the host is Windows.</param>
        /// <param name="isMac">Whether the host is macOS.</param>
        /// <returns>The result; <see cref="HarborPathImportResult.Path"/> is the PATH to use.</returns>
        public HarborPathImportResult Compute(string? currentPath, string? shell, string? home, bool isWindows, bool isMac)
        {
            HarborPathImportResult result = new HarborPathImportResult { OriginalPath = currentPath ?? String.Empty, Path = currentPath ?? String.Empty };
            if (isWindows) return result;

            string chosenShell = !String.IsNullOrWhiteSpace(shell) ? shell!.Trim() : (isMac ? "/bin/zsh" : "/bin/bash");
            result.Shell = chosenShell;
            string? loginPath = ReadLoginShellPath(chosenShell, out string? error);
            result.LoginShellRead = loginPath != null;
            result.Error = error;

            result.Path = Merge(currentPath, loginPath, CommonDirectories(home));
            HashSet<string> original = new HashSet<string>(Split(currentPath), StringComparer.Ordinal);
            foreach (string entry in Split(result.Path))
                if (!original.Contains(entry)) result.Added.Add(entry);
            return result;
        }

        /// <summary>
        /// Compute the PATH for this host (see <see cref="Compute"/>) from the process environment and set it on the
        /// process, so every captain the Harbor launches inherits it.
        /// </summary>
        /// <returns>What was done.</returns>
        public HarborPathImportResult ImportIntoProcess()
        {
            HarborPathImportResult result = Compute(
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("SHELL"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                OperatingSystem.IsWindows(),
                OperatingSystem.IsMacOS());
            if (!String.Equals(result.Path, result.OriginalPath, StringComparison.Ordinal))
                Environment.SetEnvironmentVariable("PATH", result.Path);
            return result;
        }

        #endregion

        #region Private-Methods

        private void AddEntries(List<string> merged, HashSet<string> seen, string? path, bool mustExist)
        {
            foreach (string entry in Split(path))
            {
                if (seen.Contains(entry)) continue;
                if (mustExist && !_DirectoryExists(entry)) continue;
                seen.Add(entry);
                merged.Add(entry);
            }
        }

        private static List<string> Split(string? path)
        {
            List<string> entries = new List<string>();
            if (String.IsNullOrEmpty(path)) return entries;
            foreach (string part in path.Split(System.IO.Path.PathSeparator))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) entries.Add(trimmed);
            }

            return entries;
        }

        private static string? RunProcess(string shell, List<string> arguments, TimeSpan timeout)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                process.StandardInput.Close();
                System.Threading.Tasks.Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(true); }
                    catch { }
                    return null;
                }

                process.WaitForExit();
                return stdout.Wait(timeout) ? stdout.Result : null;
            }
        }

        #endregion
    }
}
