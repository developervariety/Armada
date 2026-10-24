namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    /// <summary>Builds the child launch from configured commands and validates runner-local paths.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static ProcessStartInfo BuildStartInfo(HarborSupervisorOptions options, HarborLaunchRequest request)
        {
            if (String.IsNullOrWhiteSpace(request.Runtime) || !options.Commands.TryGetValue(request.Runtime, out string? command) || String.IsNullOrWhiteSpace(command))
                throw new HarborJobJournalException("runtime_not_configured", "The requested runtime key is not configured.");
            if (!Path.IsPathRooted(command) || !File.Exists(command))
                throw new HarborJobJournalException("runtime_command_invalid", "The configured runtime executable is not an existing absolute file.");
            string workingDirectory = ValidateWorkingDirectory(options.WorkingRoots, request.WorkingDirectory);
            if (request.Arguments == null || request.Arguments.Any(argument => argument == null))
                throw new HarborJobJournalException("launch_arguments_invalid", "The resolved argument list is missing or invalid.");
            Dictionary<string, string> allowed = HarborLaunchEnvironment.Select(request.Environment ?? new Dictionary<string, string>());
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false),
                CreateNoWindow = true
            };
            foreach (string argument in request.Arguments) startInfo.ArgumentList.Add(argument);
            CaptainEnvironmentScrub.Scrub(startInfo.Environment, null);
            foreach (KeyValuePair<string, string> variable in allowed) startInfo.Environment[variable.Key] = variable.Value;
            ApplyMissionRuntimeConfiguration(startInfo, request, workingDirectory);
            return startInfo;
        }

        private static string ValidateWorkingDirectory(List<string> roots, string requestedPath)
        {
            if (String.IsNullOrWhiteSpace(requestedPath)) throw new HarborJobJournalException("working_directory_invalid", "The launch working directory is missing.");
            string full = Path.GetFullPath(requestedPath);
            EnsureNoSymlinkAncestors(full);
            if (!Directory.Exists(full)) throw new HarborJobJournalException("working_directory_missing", "The launch working directory does not exist.");
            foreach (string rootValue in roots)
            {
                if (String.IsNullOrWhiteSpace(rootValue)) continue;
                string root = Path.GetFullPath(rootValue).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                EnsureNoSymlinkAncestors(root);
                string prefix = root + Path.DirectorySeparatorChar;
                if (String.Equals(full, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return full;
            }
            throw new HarborJobJournalException("working_directory_outside_roots", "The launch working directory is outside configured roots.");
        }

        private static void EnsureNoSymlinkAncestors(string path)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (String.IsNullOrEmpty(root)) throw new HarborJobJournalException("path_invalid", "A launch path is invalid.");
            string current = root;
            foreach (string segment in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new HarborJobJournalException("working_directory_link_refused", "A launch path contains a symbolic link or reparse point.");
            }
        }
    }
}
