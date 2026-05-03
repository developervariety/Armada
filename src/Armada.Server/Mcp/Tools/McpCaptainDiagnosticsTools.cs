namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers read-only MCP diagnostics for captain progress inspection.
    /// </summary>
    public static class McpCaptainDiagnosticsTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Register captain diagnostics MCP tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="codeIndex">Optional code index service.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, ICodeIndexService? codeIndex = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                "armada_captain_diagnostics",
                "Read-only progress diagnostics for a captain, including active mission, elapsed minutes, dock git status, and code index freshness when available.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                    },
                    required = new[] { "captainId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    CaptainIdArgs request = JsonSerializer.Deserialize<CaptainIdArgs>(args.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.CaptainId)) return (object)new { Error = "captainId is required" };

                    Captain? captain = await database.Captains.ReadAsync(request.CaptainId).ConfigureAwait(false);
                    if (captain == null) return (object)new { Error = "Captain not found" };

                    Mission? mission = null;
                    if (!String.IsNullOrWhiteSpace(captain.CurrentMissionId))
                    {
                        mission = await database.Missions.ReadAsync(captain.CurrentMissionId).ConfigureAwait(false);
                    }

                    Dock? dock = null;
                    string? dockId = captain.CurrentDockId ?? mission?.DockId;
                    if (!String.IsNullOrWhiteSpace(dockId))
                    {
                        dock = await database.Docks.ReadAsync(dockId).ConfigureAwait(false);
                    }

                    GitStatusResult gitStatus = await ReadDockGitStatusAsync(dock?.WorktreePath).ConfigureAwait(false);
                    object codeIndexStatus = await ReadCodeIndexStatusAsync(codeIndex, mission, dock).ConfigureAwait(false);

                    return (object)new
                    {
                        captainId = captain.Id,
                        state = captain.State.ToString(),
                        activeMissionId = mission?.Id,
                        activeMissionTitle = mission?.Title,
                        elapsedMinutes = CalculateElapsedMinutes(mission),
                        dockId = dock?.Id,
                        dockPath = dock?.WorktreePath,
                        dockGitStatus = gitStatus.Output,
                        dockGitStatusError = gitStatus.Error,
                        hasUncommittedDockChanges = gitStatus.HasChanges,
                        codeIndex = codeIndexStatus
                    };
                });
        }

        private static double? CalculateElapsedMinutes(Mission? mission)
        {
            if (mission == null) return null;

            DateTime startedUtc = mission.StartedUtc ?? mission.CreatedUtc;
            double elapsed = (DateTime.UtcNow - startedUtc).TotalMinutes;
            if (elapsed < 0) elapsed = 0;
            return Math.Round(elapsed, 2);
        }

        private static async Task<object> ReadCodeIndexStatusAsync(ICodeIndexService? codeIndex, Mission? mission, Dock? dock)
        {
            if (codeIndex == null)
            {
                return new
                {
                    available = false,
                    vesselId = (string?)null,
                    freshness = (string?)null,
                    isStale = (bool?)null,
                    error = "code index service unavailable"
                };
            }

            string? vesselId = mission?.VesselId ?? dock?.VesselId;
            if (String.IsNullOrWhiteSpace(vesselId))
            {
                return new
                {
                    available = true,
                    vesselId = (string?)null,
                    freshness = (string?)null,
                    isStale = (bool?)null,
                    error = "active mission has no vessel"
                };
            }

            try
            {
                CodeIndexStatus status = await codeIndex.GetStatusAsync(vesselId).ConfigureAwait(false);
                bool isStale = !String.Equals(status.Freshness, "Fresh", StringComparison.OrdinalIgnoreCase);
                return new
                {
                    available = true,
                    vesselId = status.VesselId,
                    freshness = status.Freshness,
                    isStale,
                    indexedCommitSha = status.IndexedCommitSha,
                    currentCommitSha = status.CurrentCommitSha,
                    indexedAtUtc = status.IndexedAtUtc,
                    lastError = status.LastError,
                    error = (string?)null
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    available = true,
                    vesselId,
                    freshness = "Error",
                    isStale = true,
                    error = ex.Message
                };
            }
        }

        private static async Task<GitStatusResult> ReadDockGitStatusAsync(string? dockPath)
        {
            if (String.IsNullOrWhiteSpace(dockPath))
            {
                return GitStatusResult.NotAvailable("dock path unavailable");
            }

            if (!Directory.Exists(dockPath))
            {
                return GitStatusResult.NotAvailable("dock path does not exist");
            }

            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = dockPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.StartInfo.ArgumentList.Add("status");
                process.StartInfo.ArgumentList.Add("--short");

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    return GitStatusResult.NotAvailable(ex.Message);
                }

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                    }

                    return GitStatusResult.NotAvailable("git status timed out");
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                output = output.TrimEnd('\r', '\n');
                error = error.TrimEnd('\r', '\n');

                if (process.ExitCode != 0)
                {
                    return GitStatusResult.NotAvailable(String.IsNullOrWhiteSpace(error) ? "git status failed" : error);
                }

                return new GitStatusResult
                {
                    Output = output,
                    HasChanges = !String.IsNullOrWhiteSpace(output)
                };
            }
        }

        private sealed class GitStatusResult
        {
            public string Output { get; set; } = "";

            public string? Error { get; set; } = null;

            public bool HasChanges { get; set; } = false;

            public static GitStatusResult NotAvailable(string error)
            {
                return new GitStatusResult
                {
                    Error = error
                };
            }
        }

        private sealed class CaptainIdArgs
        {
            public string CaptainId { get; set; } = "";
        }
    }
}
