namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    public sealed partial class HarborJobSupervisor
    {
        private static void ApplyMissionRuntimeConfiguration(ProcessStartInfo start, HarborLaunchRequest request, string workingDirectory)
        {
            HarborMissionRuntimeConfiguration? configuration = request.MissionConfiguration;
            if (configuration == null) return;
            if (request.CheckoutBinding == null || String.IsNullOrWhiteSpace(request.DockId)
                || String.IsNullOrWhiteSpace(configuration.Token) || configuration.Token.Length > 32768
                || configuration.Token.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new HarborJobJournalException("mission_configuration_invalid", "The mission configuration is not bound to an owned checkout.");
            string directory = ValidateWorkingDirectory(new List<string> { workingDirectory }, configuration.Directory);
            if (String.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(workingDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new HarborJobJournalException("mission_configuration_invalid", "The mission configuration requires its own contained directory.");
            start.Environment[McpLaunchCredential.EnvironmentVariable] = configuration.Token;
            if (configuration.ScopedHome)
            {
                start.Environment["HOME"] = directory;
                start.Environment["USERPROFILE"] = directory;
            }
            if (configuration.OpenCodeConfig)
                start.Environment["OPENCODE_CONFIG"] = Path.Combine(directory, "opencode.json");
        }
    }
}
