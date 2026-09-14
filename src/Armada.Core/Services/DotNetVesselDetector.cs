namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Decides whether a vessel is a .NET repository, for Checks that only apply to .NET code.
    /// </summary>
    /// <remarks>
    /// Two independent signals count: a workflow-profile command that invokes <c>dotnet</c> or
    /// <c>msbuild</c>, or a solution, project or MSBuild root file in the vessel working directory
    /// or one directory below it. The search is shallow on purpose: a working directory can hold
    /// nested clones, and a recursive walk would read them.
    /// </remarks>
    public static class DotNetVesselDetector
    {
        #region Private-Members

        private static readonly Regex _DotNetCommand = new Regex(@"(?:^|[\s;&|(/\\""'])(?:dotnet|msbuild)(?:\.exe)?(?=\s|$|[;&|)""'])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] _MarkerPatterns = new[]
        {
            "*.sln",
            "*.slnx",
            "*.csproj",
            "*.fsproj",
            "*.vbproj",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the vessel is a .NET repository.
        /// </summary>
        /// <param name="vessel">The vessel. Null returns false.</param>
        /// <param name="profile">The vessel's resolved workflow profile, if any.</param>
        /// <param name="evidence">What decided the answer, for the arming log line.</param>
        /// <returns>True when a .NET signal was found.</returns>
        public static bool IsDotNetVessel(Vessel? vessel, WorkflowProfile? profile, out string evidence)
        {
            if (vessel == null)
            {
                evidence = "no vessel";
                return false;
            }

            if (profile != null)
            {
                foreach (KeyValuePair<string, string?> command in ProfileCommands(profile))
                {
                    if (!String.IsNullOrWhiteSpace(command.Value) && _DotNetCommand.IsMatch(command.Value))
                    {
                        evidence = "profile " + command.Key + " invokes dotnet or msbuild";
                        return true;
                    }
                }
            }

            string? directory = vessel.WorkingDirectory;
            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                evidence = "no .NET profile command and no readable working directory";
                return false;
            }

            try
            {
                string? marker = FindMarker(directory);
                if (marker == null)
                {
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        if (Path.GetFileName(child).StartsWith(".", StringComparison.Ordinal)) continue;
                        marker = FindMarker(child);
                        if (marker != null) break;
                    }
                }

                if (marker != null)
                {
                    evidence = "working directory contains " + Path.GetFileName(marker);
                    return true;
                }

                evidence = "no .NET profile command and no solution, project or MSBuild root file in the working directory";
                return false;
            }
            catch (IOException ex)
            {
                evidence = "working directory could not be read: " + ex.Message;
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                evidence = "working directory could not be read: " + ex.Message;
                return false;
            }
        }

        #endregion

        #region Private-Methods

        private static IEnumerable<KeyValuePair<string, string?>> ProfileCommands(WorkflowProfile profile)
        {
            yield return new KeyValuePair<string, string?>("BuildCommand", profile.BuildCommand);
            yield return new KeyValuePair<string, string?>("UnitTestCommand", profile.UnitTestCommand);
            yield return new KeyValuePair<string, string?>("LintCommand", profile.LintCommand);
            yield return new KeyValuePair<string, string?>("IntegrationTestCommand", profile.IntegrationTestCommand);
        }

        private static string? FindMarker(string directory)
        {
            foreach (string pattern in _MarkerPatterns)
            {
                string? match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (match != null) return match;
            }

            return null;
        }

        #endregion
    }
}
