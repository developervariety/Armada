namespace Armada.Test.Unit.TestHelpers
{
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Shared Reflection v1 test helpers (output contract shape and vessel bootstrap).
    /// </summary>
    public static class ReflectionTestHelpers
    {
        /// <summary>
        /// Marker embedded in end-to-end smoke playbook content so snapshots can be asserted without ambiguity.
        /// </summary>
        public const string SmokeLearnedContentMarker = "E2E_REFLECTION_SMOKE_LEARNED_MARKER";

        /// <summary>
        /// Resolved learned-facts markdown filename for a vessel (matches production sanitization).
        /// </summary>
        /// <param name="vessel">Vessel whose name is sanitized.</param>
        /// <returns>Deterministic filename such as vessel-my-repo-learned.md.</returns>
        public static string ReflectionLearnedMarkdownFileName(Vessel vessel)
        {
            string lower = vessel.Name.ToLowerInvariant();
            string sanitized = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
            return "vessel-" + sanitized + "-learned.md";
        }

        /// <summary>
        /// Valid JSON body for the reflections-diff fenced block.
        /// </summary>
        /// <returns>Minimal structured diff JSON accepted by the parser.</returns>
        public static string ValidReflectionDiffJson()
        {
            return "{\n  \"added\": [],\n  \"removed\": [],\n  \"merged\": [],\n  \"unchangedCount\": 1,\n  \"evidenceConfidence\": \"high\",\n  \"notes\": \"ok\"\n}";
        }

        /// <summary>
        /// Builds MemoryConsolidator AgentOutput with candidate and diff fences.
        /// </summary>
        /// <param name="candidateBody">Markdown inside the candidate fence.</param>
        /// <returns>Full agent output string.</returns>
        public static string BuildReflectionProposalAgentOutput(string candidateBody)
        {
            return "```reflections-candidate\n" + candidateBody.TrimEnd() + "\n```\n```reflections-diff\n"
                + ValidReflectionDiffJson() + "\n```\n";
        }

        /// <summary>
        /// Persists a vessel row then runs <see cref="ReflectionMemoryBootstrapService"/> so learned playbook exists and is default-attached.
        /// </summary>
        /// <param name="database">SQLite test driver.</param>
        /// <param name="vesselName">Unique vessel display name.</param>
        /// <returns>Vessel after bootstrap (re-read from storage).</returns>
        public static async Task<Vessel> CreateBootstrappedReflectionVesselAsync(DatabaseDriver database, string vesselName)
        {
            Vessel vessel = new Vessel(vesselName, "https://github.com/test/" + vesselName.Replace(' ', '-') + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            vessel = await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ReflectionMemoryBootstrapService bootstrap = new ReflectionMemoryBootstrapService(database, logging);
            await bootstrap.BootstrapAsync().ConfigureAwait(false);

            Vessel? updated = await database.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
            if (updated == null)
            {
                throw new InvalidOperationException("Bootstrap vessel disappeared");
            }

            return updated;
        }

        /// <summary>
        /// Inserts a terminal Worker mission counts as reflection evidence material.
        /// </summary>
        /// <param name="database">Driver.</param>
        /// <param name="vesselId">Owning vessel.</param>
        /// <param name="titleSuffix">Suffix for title or description uniqueness.</param>
        /// <param name="completedUtc">Completion timestamp ordering evidence.</param>
        /// <returns>Persisted mission.</returns>
        public static async Task<Mission> CreateReflectionEvidenceMissionAsync(
            DatabaseDriver database,
            string vesselId,
            string titleSuffix,
            DateTime completedUtc)
        {
            Mission mission = new Mission("Smoke evidence " + titleSuffix, "Shipped unit " + titleSuffix + " with tests.");
            mission.VesselId = vesselId;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = completedUtc;
            mission.DiffSnapshot = "diff --git a/src/Example.cs\n+++ b/src/Example.cs\n@@ -1 +1,2 @@\n+// " + titleSuffix + "\n";
            mission.AgentOutput = "[ARMADA:RESULT] COMPLETE\nSummary for " + titleSuffix + ".";
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }
    }
}
