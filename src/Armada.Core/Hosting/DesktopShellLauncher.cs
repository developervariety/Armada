namespace Armada.Core.Hosting
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Starts <see cref="ShellLaunch"/> commands on this computer's desktop, and says whether this process can reach a
    /// desktop at all (a container, a Windows service, or a Linux session without a display cannot).
    /// </summary>
    public static class DesktopShellLauncher
    {
        #region Public-Methods

        /// <summary>
        /// Open a terminal window in a folder on this computer's desktop (see
        /// <see cref="DesktopShellCommandBuilder.BuildOpenTerminal"/>).
        /// </summary>
        /// <param name="path">Folder.</param>
        /// <param name="subject">What this computer is called in a failure sentence, for example "The Admiral" or "This
        /// Harbor".</param>
        /// <returns>Null when a terminal opened; otherwise why not, as one sentence.</returns>
        public static string? OpenTerminal(string path, string subject)
        {
            if (String.IsNullOrWhiteSpace(path)) return "No folder was given to open a terminal in.";
            if (!Directory.Exists(path)) return "The folder " + path + " does not exist.";
            string? noDesktop = DesktopUnavailableReason(subject);
            if (noDesktop != null) return noDesktop;

            List<ShellLaunch> candidates;
            try
            {
                candidates = DesktopShellCommandBuilder.BuildOpenTerminal(RegistrationContext.DetectPlatform(), path);
            }
            catch (PlatformNotSupportedException ex)
            {
                return ex.Message;
            }

            return TryStartFirst(candidates);
        }

        /// <summary>
        /// Start the first launch that works, trying the next when a program is not installed.
        /// </summary>
        /// <param name="candidates">Launches, preferred first.</param>
        /// <returns>Null when one started; otherwise why none did.</returns>
        /// <exception cref="ArgumentNullException">candidates is null.</exception>
        public static string? TryStartFirst(List<ShellLaunch> candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            List<string> failures = new List<string>();
            foreach (ShellLaunch launch in candidates)
            {
                string? error = TryStart(launch);
                if (error == null) return null;
                failures.Add(launch.FileName + ": " + error);
            }

            return failures.Count == 0
                ? "There is no terminal to start on this computer."
                : "No terminal could be started (" + String.Join("; ", failures) + ").";
        }

        /// <summary>
        /// Why this process cannot open windows on a desktop, or null when it can (as far as it can tell: on macOS a
        /// process without a GUI session finds out when open fails).
        /// </summary>
        /// <param name="subject">What this computer is called in the sentence, for example "The Admiral".</param>
        /// <returns>One sentence, or null.</returns>
        public static string? DesktopUnavailableReason(string subject)
        {
            string who = String.IsNullOrWhiteSpace(subject) ? "This process" : subject.Trim();
            if (String.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
                || File.Exists("/.dockerenv"))
                return who + " runs in a container, which has no desktop to open a terminal on.";
            if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
                return who + " runs as a Windows service, which has no desktop to open a terminal on.";
            if (OperatingSystem.IsLinux()
                && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                return who + " has no desktop session (no DISPLAY or WAYLAND_DISPLAY) to open a terminal on.";
            return null;
        }

        #endregion

        #region Private-Methods

        private static string? TryStart(ShellLaunch launch)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(launch.FileName) { UseShellExecute = launch.UseShellExecute };
                if (launch.RawArguments != null) psi.Arguments = launch.RawArguments;
                else foreach (string argument in launch.Arguments) psi.ArgumentList.Add(argument);
                if (!String.IsNullOrEmpty(launch.WorkingDirectory)) psi.WorkingDirectory = launch.WorkingDirectory;

                using (Process? process = Process.Start(psi))
                {
                    if (process == null) return "it did not start";
                    if (launch.WaitForExitMs > 0)
                    {
                        if (!process.WaitForExit(launch.WaitForExitMs)) return null;
                        if (process.ExitCode != 0) return "it exited with code " + process.ExitCode;
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is PlatformNotSupportedException || ex is IOException)
            {
                return ex.Message;
            }
        }

        #endregion
    }
}
