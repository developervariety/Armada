namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    /// <summary>
    /// The start settings every admiral-run git process shares, so no call site can forget one: no terminal or
    /// credential-manager prompt and no pager. A private repository with no cached credentials then fails at once
    /// instead of waiting on a prompt nobody can answer, and output is never held by a pager.
    /// </summary>
    public static class GitProcessStartInfo
    {
        #region Public-Methods

        /// <summary>
        /// Create start settings for one git command.
        /// </summary>
        /// <param name="workingDirectory">Directory to run in; null or empty for the current directory.</param>
        /// <param name="arguments">Git arguments, one per element.</param>
        /// <returns>The start settings.</returns>
        public static ProcessStartInfo Create(string? workingDirectory, IEnumerable<string> arguments)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            ProcessStartInfo startInfo = new ProcessStartInfo("git");
            if (!String.IsNullOrEmpty(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            ApplyNonInteractive(startInfo);
            return startInfo;
        }

        /// <summary>
        /// Apply the non-interactive git settings to existing start settings.
        /// </summary>
        /// <param name="startInfo">Start settings to change.</param>
        public static void ApplyNonInteractive(ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));

            // GIT_TERMINAL_PROMPT covers git's own prompt; GCM_INTERACTIVE covers Git Credential Manager, whose
            // interface a timeout cannot reliably kill.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GCM_INTERACTIVE"] = "Never";
            startInfo.Environment["GIT_PAGER"] = "cat";
        }

        #endregion
    }
}
