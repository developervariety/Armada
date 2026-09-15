namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Catalog and column prune migration proof. A database one version below the prune holds scope-named
    /// playbooks and their links, the named pipelines, the pruned persona and its templates, pack hints
    /// and threshold columns next to operator rows and native memory. The migration deletes only the
    /// named rows, clears every reference to them, drops the table and columns, restarts cleanly after
    /// an interruption and leaves the other
    /// rows unchanged.
    /// </summary>
    internal sealed class CatalogAndColumnPruneMigrationTests
    {
        private readonly DatabaseSettings _Settings;
        private sealed class StopException : Exception { }

        private sealed class Seed
        {
            internal string LearnedVessel = "";
            internal string LearnedPersona = "";
            internal string LearnedCaptain = "";
            internal string LearnedFleet = "";
            internal string OperatorPlaybook = "";
            internal string LookalikePlaybook = "";
            internal string NamedPipeline = "";
            internal string NamedDualJudgePipeline = "";
            internal string CustomPipeline = "";
            internal string PipelineWithConsolidatorStage = "";
            internal string ConsolidatorOnlyPipeline = "";
            internal string FleetId = "";
            internal string VesselWithMixedDefaults = "";
            internal string VesselWithOnlyLearnedDefaults = "";
            internal string ConsolidatorCaptain = "";
            internal string WorkerCaptain = "";
            internal string PrunedPipelineObjective = "";
            internal string CustomObjective = "";
            internal string VoyageId = "";
            internal string MissionId = "";
            internal string MemoryId = "";
        }

        internal CatalogAndColumnPruneMigrationTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 93, DatabaseTypeEnum.Postgresql => 94,
                DatabaseTypeEnum.Mysql => 85, DatabaseTypeEnum.SqlServer => 88,
                _ => throw new NotSupportedException()
            };

            await StopAtAsync(version, -1, token).ConfigureAwait(false);
            MigrationScenarioRunner history = new MigrationScenarioRunner(_Settings);
            Dictionary<int, string> before = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(before.Keys.All(key => key < version), "The prune is still pending and no later version applied");
            DatabaseAssert.True(await TableExistsAsync("vessel_pack_hints", token).ConfigureAwait(false), "Pack hints exist before the prune");

            Seed seed = await SeedAsync(token).ConfigureAwait(false);

            await StopAtAsync(version, 3, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted prune records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                // Later migrations may follow this one, so prove this version committed rather than a count.
                DatabaseAssert.True(committed.ContainsKey(version), "The restarted run commits the prune version");
                DatabaseAssert.True(committed.Keys.All(key => key <= version || !before.ContainsKey(key)), "Only pending versions are added");
                MigrationScenarioRunner.AssertHistory(before, committed);

                await AssertRemovedAsync(driver, seed, token).ConfigureAwait(false);
                await AssertRetainedAsync(driver, seed, token).ConfigureAwait(false);

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await AssertRetainedAsync(driver, seed, token).ConfigureAwait(false);
            }

            Console.WriteLine("PASS catalog and column prune migration: named rows and links removed, references cleared, consolidator stages and allow-list entries removed, table and columns dropped, interrupted run restarts, operator rows and native memory unchanged");
        }

        // Every row is seeded while the schema stops below the prune version and below the newest version, so each
        // row is written with SQL that names only columns present at the stop version, never through driver writes.
        private async Task<Seed> SeedAsync(CancellationToken token)
        {
            string tenant = Constants.DefaultTenantId;
            StopVersionSeed rows = new StopVersionSeed(_Settings);
            Seed seed = new Seed();

            seed.LearnedVessel = await CreatePlaybookAsync(rows, "vessel-example-learned.md", token).ConfigureAwait(false);
            seed.LearnedPersona = await CreatePlaybookAsync(rows, "persona-worker-learned.md", token).ConfigureAwait(false);
            seed.LearnedCaptain = await CreatePlaybookAsync(rows, "captain-cpt_example-learned.md", token).ConfigureAwait(false);
            seed.LearnedFleet = await CreatePlaybookAsync(rows, "fleet-flt_example-learned.md", token).ConfigureAwait(false);
            seed.OperatorPlaybook = await CreatePlaybookAsync(rows, "vessel-guide.md", token).ConfigureAwait(false);
            seed.LookalikePlaybook = await CreatePlaybookAsync(rows, "release-notes-learned.md", token).ConfigureAwait(false);

            seed.NamedPipeline = await rows.CreatePipelineAsync("Reflections", new[] { "MemoryConsolidator" }, token).ConfigureAwait(false);
            seed.NamedDualJudgePipeline = await rows.CreatePipelineAsync("ReflectionsDualJudge", new[] { "MemoryConsolidator", "Judge" }, token).ConfigureAwait(false);
            seed.CustomPipeline = await rows.CreatePipelineAsync("CustomReview", new[] { "Worker", "Judge" }, token).ConfigureAwait(false);
            seed.PipelineWithConsolidatorStage = await rows.CreatePipelineAsync("OperatorWithConsolidator", new[] { "Worker", "MemoryConsolidator", "Judge" }, token).ConfigureAwait(false);
            seed.ConsolidatorOnlyPipeline = await rows.CreatePipelineAsync("OperatorConsolidatorOnly", new[] { "MemoryConsolidator" }, token).ConfigureAwait(false);

            await rows.CreateTemplateAsync("persona.memory_consolidator", "persona", false, token).ConfigureAwait(false);
            await rows.CreateTemplateAsync("mission.model_context_updates", "mission", false, token).ConfigureAwait(false);
            await rows.CreateTemplateAsync("persona.custom_reviewer", "persona", false, token).ConfigureAwait(false);

            await rows.CreatePersonaAsync("MemoryConsolidator", "persona.memory_consolidator", false, Selections(seed.LearnedPersona), token).ConfigureAwait(false);
            await rows.CreatePersonaAsync("CustomReviewer", "persona.custom_reviewer", false, Selections(seed.LearnedPersona, seed.OperatorPlaybook), token).ConfigureAwait(false);

            Fleet fleet = new Fleet("PruneFleet");
            seed.FleetId = fleet.Id;
            Dictionary<string, object?> fleetRow = rows.TimestampedRow();
            fleetRow["id"] = fleet.Id;
            fleetRow["tenant_id"] = tenant;
            fleetRow["name"] = fleet.Name;
            fleetRow["default_pipeline_id"] = seed.NamedPipeline;
            fleetRow["default_playbooks"] = Selections(seed.LearnedFleet, seed.OperatorPlaybook);
            fleetRow["active"] = rows.Flag(true);
            await rows.InsertAsync("fleets", fleetRow, token).ConfigureAwait(false);

            // One entry uses the PascalCase key an older writer produced; parsing is case-insensitive.
            string mixedDefaults = "[{\"playbookId\":\"" + seed.OperatorPlaybook + "\",\"deliveryMode\":\"InlineFullContent\"},"
                + "{\"PlaybookId\":\"" + seed.LearnedVessel + "\",\"DeliveryMode\":\"InlineFullContent\"}]";
            seed.VesselWithMixedDefaults = await CreateVesselAsync(rows, "MixedDefaults", "https://example.com/mixed.git", seed.FleetId, seed.NamedPipeline, mixedDefaults, token).ConfigureAwait(false);
            seed.VesselWithOnlyLearnedDefaults = await CreateVesselAsync(rows, "OnlyLearnedDefaults", "https://example.com/learned.git", null, seed.CustomPipeline, Selections(seed.LearnedVessel), token).ConfigureAwait(false);

            seed.ConsolidatorCaptain = await CreateCaptainAsync(rows, "consolidator-captain", "MemoryConsolidator", "[\"MemoryConsolidator\"]", Selections(seed.LearnedCaptain), token).ConfigureAwait(false);
            seed.WorkerCaptain = await CreateCaptainAsync(rows, "worker-captain", "Worker", "[\"Worker\",\"MemoryConsolidator\",\"Judge\"]", Selections(seed.OperatorPlaybook), token).ConfigureAwait(false);

            seed.PrunedPipelineObjective = await CreateObjectiveAsync(rows, "Consolidate memory", seed.NamedDualJudgePipeline, token).ConfigureAwait(false);
            seed.CustomObjective = await CreateObjectiveAsync(rows, "Ordinary review", seed.CustomPipeline, token).ConfigureAwait(false);

            Voyage voyage = new Voyage("Prune voyage");
            seed.VoyageId = voyage.Id;
            Dictionary<string, object?> voyageRow = rows.TimestampedRow();
            voyageRow["id"] = voyage.Id;
            voyageRow["tenant_id"] = tenant;
            voyageRow["title"] = voyage.Title;
            voyageRow["status"] = voyage.Status.ToString();
            await rows.InsertAsync("voyages", voyageRow, token).ConfigureAwait(false);
            string[] voyagePlaybooks = new[] { seed.LearnedVessel, seed.OperatorPlaybook };
            for (int index = 0; index < voyagePlaybooks.Length; index++)
            {
                Dictionary<string, object?> selection = new Dictionary<string, object?>();
                selection["voyage_id"] = seed.VoyageId;
                selection["playbook_id"] = voyagePlaybooks[index];
                selection["selection_order"] = index;
                selection["delivery_mode"] = new SelectedPlaybook().DeliveryMode.ToString();
                await rows.InsertAsync("voyage_playbooks", selection, token).ConfigureAwait(false);
            }

            Mission mission = new Mission("Prune mission");
            seed.MissionId = mission.Id;
            Dictionary<string, object?> missionRow = rows.TimestampedRow();
            missionRow["id"] = mission.Id;
            missionRow["tenant_id"] = tenant;
            missionRow["voyage_id"] = seed.VoyageId;
            missionRow["vessel_id"] = seed.VesselWithMixedDefaults;
            missionRow["title"] = mission.Title;
            missionRow["status"] = mission.Status.ToString();
            missionRow["priority"] = mission.Priority;
            await rows.InsertAsync("missions", missionRow, token).ConfigureAwait(false);
            await CreateSnapshotAsync(rows, seed.MissionId, 0, seed.LearnedVessel, "vessel-example-learned.md", "learned", token).ConfigureAwait(false);
            await CreateSnapshotAsync(rows, seed.MissionId, 1, seed.OperatorPlaybook, "vessel-guide.md", "operator", token).ConfigureAwait(false);

            Memory memory = new Memory();
            seed.MemoryId = memory.Id;
            Dictionary<string, object?> memoryRow = rows.TimestampedRow();
            memoryRow["id"] = memory.Id;
            memoryRow["tenant_id"] = tenant;
            memoryRow["user_id"] = Constants.DefaultUserId;
            memoryRow["scope"] = memory.Scope.ToString();
            memoryRow["type"] = memory.Type.ToString();
            memoryRow["memory_key"] = "catalog-column-prune/native";
            memoryRow["content"] = "Native memory survives the prune.";
            memoryRow["salience"] = memory.Salience;
            memoryRow["version"] = memory.Version;
            await rows.InsertAsync("memories", memoryRow, token).ConfigureAwait(false);

            Dictionary<string, object?> vesselThresholds = new Dictionary<string, object?>();
            vesselThresholds["@id"] = seed.VesselWithMixedDefaults;
            await rows.ExecuteAsync("UPDATE vessels SET reflection_threshold = 5, reorganize_threshold = 6, pack_curate_threshold = 7, last_reflection_mission_id = 'msn_reflection' WHERE id = @id;",
                vesselThresholds, token).ConfigureAwait(false);
            Dictionary<string, object?> personaThresholds = new Dictionary<string, object?>();
            personaThresholds["@playbook"] = seed.LearnedPersona;
            await rows.ExecuteAsync("UPDATE personas SET curate_threshold = 3, learned_playbook_id = @playbook WHERE name = 'MemoryConsolidator';",
                personaThresholds, token).ConfigureAwait(false);
            Dictionary<string, object?> captainThresholds = new Dictionary<string, object?>();
            captainThresholds["@playbook"] = seed.LearnedCaptain;
            captainThresholds["@id"] = seed.ConsolidatorCaptain;
            await rows.ExecuteAsync("UPDATE captains SET curate_threshold = 4, learned_playbook_id = @playbook WHERE id = @id;",
                captainThresholds, token).ConfigureAwait(false);
            Dictionary<string, object?> fleetThresholds = new Dictionary<string, object?>();
            fleetThresholds["@playbook"] = seed.LearnedFleet;
            fleetThresholds["@id"] = seed.FleetId;
            await rows.ExecuteAsync("UPDATE fleets SET curate_threshold = 2, learned_playbook_id = @playbook WHERE id = @id;",
                fleetThresholds, token).ConfigureAwait(false);
            Dictionary<string, object?> packHint = new Dictionary<string, object?>();
            packHint["@vessel"] = seed.VesselWithMixedDefaults;
            packHint["@active"] = true;
            packHint["@created"] = rows.Timestamp();
            await rows.ExecuteAsync("INSERT INTO vessel_pack_hints (id, vessel_id, goal_pattern, must_include, must_exclude, priority, confidence, source_mission_ids, active, created_utc, last_update_utc) "
                + "VALUES ('vph_example', @vessel, 'build', '[]', '[]', 1, 'high', '[]', @active, @created, @created);",
                packHint, token).ConfigureAwait(false);

            return seed;
        }

        private async Task AssertRemovedAsync(DatabaseDriver driver, Seed seed, CancellationToken token)
        {
            foreach (string learned in new[] { seed.LearnedVessel, seed.LearnedPersona, seed.LearnedCaptain, seed.LearnedFleet })
                DatabaseAssert.True(await driver.Playbooks.ReadAsync(learned, token).ConfigureAwait(false) == null, "Learned playbook " + learned + " is removed");

            DatabaseAssert.True(await driver.Pipelines.ReadByNameAsync("Reflections", token).ConfigureAwait(false) == null, "Named pipeline is removed");
            DatabaseAssert.True(await driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge", token).ConfigureAwait(false) == null, "Named dual-Judge pipeline is removed");
            DatabaseAssert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM pipeline_stages WHERE pipeline_id IN (@a, @b);", token,
                ("@a", seed.NamedPipeline), ("@b", seed.NamedDualJudgePipeline)).ConfigureAwait(false), "Named pipeline stages are removed");
            DatabaseAssert.True(await driver.Pipelines.ReadAsync(seed.ConsolidatorOnlyPipeline, token).ConfigureAwait(false) == null, "A pipeline left with no stages is removed");
            DatabaseAssert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM pipeline_stages WHERE persona_name = @persona OR pipeline_id = @id;", token,
                ("@persona", "MemoryConsolidator"), ("@id", seed.ConsolidatorOnlyPipeline)).ConfigureAwait(false), "No stage names the removed consolidator");

            DatabaseAssert.True(await driver.Personas.ReadByNameAsync("MemoryConsolidator", token).ConfigureAwait(false) == null, "Memory consolidator persona is removed");
            DatabaseAssert.True(await driver.PromptTemplates.ReadByNameAsync("persona.memory_consolidator", token).ConfigureAwait(false) == null, "Consolidator template is removed");
            DatabaseAssert.True(await driver.PromptTemplates.ReadByNameAsync("mission.model_context_updates", token).ConfigureAwait(false) == null, "Named mission template is removed");

            DatabaseAssert.True(!await TableExistsAsync("vessel_pack_hints", token).ConfigureAwait(false), "Pack hints table is dropped");
            foreach (string column in new[] { "reflection_threshold", "reorganize_threshold", "pack_curate_threshold", "last_reflection_mission_id" })
                DatabaseAssert.True(!await ColumnExistsAsync("vessels", column, token).ConfigureAwait(false), "vessels." + column + " is dropped");
            foreach (string table in new[] { "personas", "captains", "fleets" })
                foreach (string column in new[] { "curate_threshold", "learned_playbook_id" })
                    DatabaseAssert.True(!await ColumnExistsAsync(table, column, token).ConfigureAwait(false), table + "." + column + " is dropped");
        }

        private async Task AssertRetainedAsync(DatabaseDriver driver, Seed seed, CancellationToken token)
        {
            DatabaseAssert.NotNull(await driver.Playbooks.ReadAsync(seed.OperatorPlaybook, token).ConfigureAwait(false), "Operator playbook is retained");
            DatabaseAssert.NotNull(await driver.Playbooks.ReadAsync(seed.LookalikePlaybook, token).ConfigureAwait(false), "A playbook outside the learned name shapes is retained");

            List<SelectedPlaybook> voyageSelections = await driver.Playbooks.GetVoyageSelectionsAsync(seed.VoyageId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, voyageSelections.Count, "Voyage keeps only its operator playbook link");
            DatabaseAssert.Equal(seed.OperatorPlaybook, voyageSelections[0].PlaybookId, "Voyage operator playbook link");

            List<MissionPlaybookSnapshot> snapshots = await driver.Playbooks.GetMissionSnapshotsAsync(seed.MissionId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, snapshots.Count, "Mission keeps only its operator playbook snapshot");
            DatabaseAssert.Equal("vessel-guide.md", snapshots[0].FileName, "Mission operator snapshot");

            Fleet fleet = DatabaseAssert.NotNull(await driver.Fleets.ReadAsync(seed.FleetId, token).ConfigureAwait(false), "Fleet retained");
            DatabaseAssert.True(fleet.DefaultPipelineId == null, "Fleet default pipeline reference to a named pipeline is cleared");
            AssertOnlyOperatorDefault(fleet.GetDefaultPlaybooks(), seed, "Fleet");

            Vessel mixed = DatabaseAssert.NotNull(await driver.Vessels.ReadAsync(seed.VesselWithMixedDefaults, token).ConfigureAwait(false), "Mixed vessel retained");
            DatabaseAssert.True(mixed.DefaultPipelineId == null, "Vessel default pipeline reference to a named pipeline is cleared");
            AssertOnlyOperatorDefault(mixed.GetDefaultPlaybooks(), seed, "Mixed vessel");

            Vessel onlyLearned = DatabaseAssert.NotNull(await driver.Vessels.ReadAsync(seed.VesselWithOnlyLearnedDefaults, token).ConfigureAwait(false), "Learned-only vessel retained");
            DatabaseAssert.Equal(seed.CustomPipeline, onlyLearned.DefaultPipelineId, "An operator pipeline reference is unchanged");
            DatabaseAssert.True(onlyLearned.DefaultPlaybooks != null, "A default list emptied by the removal stays a list");
            DatabaseAssert.Equal(0, onlyLearned.GetDefaultPlaybooks().Count, "Learned-only vessel defaults are empty");

            Persona reviewer = DatabaseAssert.NotNull(await driver.Personas.ReadByNameAsync("CustomReviewer", token).ConfigureAwait(false), "Operator persona retained");
            AssertOnlyOperatorDefault(reviewer.GetDefaultPlaybooks(), seed, "Operator persona");
            DatabaseAssert.NotNull(await driver.PromptTemplates.ReadByNameAsync("persona.custom_reviewer", token).ConfigureAwait(false), "Operator template retained");

            Pipeline custom = DatabaseAssert.NotNull(await driver.Pipelines.ReadAsync(seed.CustomPipeline, token).ConfigureAwait(false), "Operator pipeline retained");
            DatabaseAssert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM pipeline_stages WHERE pipeline_id = @id;", token, ("@id", custom.Id)).ConfigureAwait(false), "Operator pipeline stages retained");

            Captain consolidatorCaptain = DatabaseAssert.NotNull(await driver.Captains.ReadAsync(seed.ConsolidatorCaptain, token).ConfigureAwait(false), "Captain retained");
            DatabaseAssert.True(consolidatorCaptain.PreferredPersona == null, "Preferred persona naming the removed consolidator is cleared");
            DatabaseAssert.Equal(0, consolidatorCaptain.GetDefaultPlaybooks().Count, "Captain learned default is removed");
            DatabaseAssert.Equal("", String.Join(",", AllowedPersonas(consolidatorCaptain)), "A list that held only the consolidator becomes empty");
            DatabaseAssert.True(consolidatorCaptain.AllowedPersonas != null, "An emptied allow-list stays a list, so the captain stays restricted");
            DatabaseAssert.Equal("Worker:1,Judge:2", await StagesAsync(seed.PipelineWithConsolidatorStage, token).ConfigureAwait(false),
                "The consolidator stage is removed and the remaining stages are renumbered without a gap");
            Captain workerCaptain = DatabaseAssert.NotNull(await driver.Captains.ReadAsync(seed.WorkerCaptain, token).ConfigureAwait(false), "Worker captain retained");
            DatabaseAssert.Equal("Worker", workerCaptain.PreferredPersona, "Operator preferred persona unchanged");
            AssertOnlyOperatorDefault(workerCaptain.GetDefaultPlaybooks(), seed, "Worker captain");
            DatabaseAssert.Equal("Worker,Judge", String.Join(",", AllowedPersonas(workerCaptain)), "The consolidator is removed from the allow-list and the order is kept");

            Objective prunedPipelineObjective = DatabaseAssert.NotNull(await driver.Objectives.ReadAsync(seed.PrunedPipelineObjective, token).ConfigureAwait(false), "Objective retained");
            DatabaseAssert.True(prunedPipelineObjective.SuggestedPipelineId == null, "Objective reference to a named pipeline is cleared");
            Objective customObjective = DatabaseAssert.NotNull(await driver.Objectives.ReadAsync(seed.CustomObjective, token).ConfigureAwait(false), "Operator objective retained");
            DatabaseAssert.Equal(seed.CustomPipeline, customObjective.SuggestedPipelineId, "Operator objective pipeline reference unchanged");

            DatabaseAssert.NotNull(await driver.Memories.ReadAsync(seed.MemoryId, token).ConfigureAwait(false), "Native memory record retained");
            DatabaseAssert.True(await TableExistsAsync("memories", token).ConfigureAwait(false), "Native memory table untouched");
        }

        private static void AssertOnlyOperatorDefault(List<SelectedPlaybook> defaults, Seed seed, string owner)
        {
            DatabaseAssert.Equal(1, defaults.Count, owner + " keeps exactly its operator default playbook");
            DatabaseAssert.Equal(seed.OperatorPlaybook, defaults[0].PlaybookId, owner + " operator default playbook");
        }

        private static List<string> AllowedPersonas(Captain captain)
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(captain.AllowedPersonas ?? "null") ?? new List<string> { "<null>" };
        }

        private async Task<string> StagesAsync(string pipelineId, CancellationToken token)
        {
            List<string> stages = new List<string>();
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = Command(connection, "SELECT persona_name, stage_order FROM pipeline_stages WHERE pipeline_id = @id ORDER BY stage_order, persona_name;", new[] { ("@id", (object)pipelineId) }))
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        stages.Add(Convert.ToString(reader[0], CultureInfo.InvariantCulture) + ":" + Convert.ToInt64(reader[1], CultureInfo.InvariantCulture));
                }
            }
            return String.Join(",", stages);
        }

        private static string Selections(params string[] playbookIds)
        {
            return "[" + String.Join(",", playbookIds.Select(id => "{\"playbookId\":\"" + id + "\",\"deliveryMode\":\"InlineFullContent\"}")) + "]";
        }

        private static async Task<string> CreatePlaybookAsync(StopVersionSeed rows, string fileName, CancellationToken token)
        {
            Playbook playbook = new Playbook(fileName, "# " + fileName + "\n\n- guidance\n");
            Dictionary<string, object?> row = rows.TimestampedRow();
            row["id"] = playbook.Id;
            row["tenant_id"] = Constants.DefaultTenantId;
            row["file_name"] = fileName;
            row["content"] = playbook.Content;
            row["active"] = rows.Flag(false);
            await rows.InsertAsync("playbooks", row, token).ConfigureAwait(false);
            return playbook.Id;
        }

        private static async Task<string> CreateVesselAsync(StopVersionSeed rows, string name, string repoUrl, string? fleetId, string pipelineId, string defaultPlaybooks, CancellationToken token)
        {
            Vessel vessel = new Vessel(name, repoUrl);
            Dictionary<string, object?> row = rows.TimestampedRow();
            row["id"] = vessel.Id;
            row["tenant_id"] = Constants.DefaultTenantId;
            if (fleetId != null) row["fleet_id"] = fleetId;
            row["name"] = name;
            row["repo_url"] = repoUrl;
            row["default_branch"] = vessel.DefaultBranch;
            row["default_pipeline_id"] = pipelineId;
            row["default_playbooks"] = defaultPlaybooks;
            row["active"] = rows.Flag(true);
            await rows.InsertAsync("vessels", row, token).ConfigureAwait(false);
            return vessel.Id;
        }

        private static async Task<string> CreateCaptainAsync(StopVersionSeed rows, string name, string preferredPersona, string allowedPersonas, string defaultPlaybooks, CancellationToken token)
        {
            Captain captain = new Captain(name);
            Dictionary<string, object?> row = rows.TimestampedRow();
            row["id"] = captain.Id;
            row["tenant_id"] = Constants.DefaultTenantId;
            row["name"] = name;
            row["runtime"] = captain.Runtime.ToString();
            row["state"] = captain.State.ToString();
            row["preferred_persona"] = preferredPersona;
            row["allowed_personas"] = allowedPersonas;
            row["default_playbooks"] = defaultPlaybooks;
            await rows.InsertAsync("captains", row, token).ConfigureAwait(false);
            return captain.Id;
        }

        private static async Task<string> CreateObjectiveAsync(StopVersionSeed rows, string title, string suggestedPipelineId, CancellationToken token)
        {
            Objective objective = new Objective();
            Dictionary<string, object?> row = rows.TimestampedRow();
            row["id"] = objective.Id;
            row["tenant_id"] = Constants.DefaultTenantId;
            row["title"] = title;
            row["status"] = objective.Status.ToString();
            row["kind"] = objective.Kind.ToString();
            row["priority"] = objective.Priority.ToString();
            row["backlog_state"] = objective.BacklogState.ToString();
            row["effort"] = objective.Effort.ToString();
            row["suggested_pipeline_id"] = suggestedPipelineId;
            await rows.InsertAsync("objectives", row, token).ConfigureAwait(false);
            return objective.Id;
        }

        private static async Task CreateSnapshotAsync(StopVersionSeed rows, string missionId, int order, string playbookId, string fileName, string content, CancellationToken token)
        {
            Dictionary<string, object?> row = new Dictionary<string, object?>();
            row["mission_id"] = missionId;
            row["playbook_id"] = playbookId;
            row["selection_order"] = order;
            row["file_name"] = fileName;
            row["content"] = content;
            row["delivery_mode"] = new MissionPlaybookSnapshot().DeliveryMode.ToString();
            await rows.InsertAsync("mission_playbook_snapshots", row, token).ConfigureAwait(false);
        }

        private async Task<bool> TableExistsAsync(string table, CancellationToken token)
        {
            string sql = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;",
                DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name=@table;",
                DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table;",
                DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.tables WHERE name=@table;",
                _ => throw new NotSupportedException()
            };
            return await CountAsync(sql, token, ("@table", table)).ConfigureAwait(false) == 1L;
        }

        private async Task<bool> ColumnExistsAsync(string table, string column, CancellationToken token)
        {
            string sql = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name=@column;",
                DatabaseTypeEnum.Postgresql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table AND column_name=@column;",
                DatabaseTypeEnum.Mysql => "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column;",
                DatabaseTypeEnum.SqlServer => "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@table) AND name=@column;",
                _ => throw new NotSupportedException()
            };
            return await CountAsync(sql, token, ("@table", table), ("@column", column)).ConfigureAwait(false) == 1L;
        }

        private async Task<long> CountAsync(string sql, CancellationToken token, params (string Name, object Value)[] parameters)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = Command(connection, sql, parameters))
                {
                    return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                }
            }
        }

        private static DbCommand Command(DbConnection connection, string sql, (string Name, object Value)[] parameters)
        {
            DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        private async Task StopAtAsync(int version, int ordinal, CancellationToken token)
        {
            using (DatabaseDriver driver = CreateDriver())
            {
                driver.MigrationCheckpoint = (current, statement) =>
                {
                    if (current == version && statement == ordinal) throw new StopException();
                };
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Prune checkpoint was not reached"); }
                catch (StopException) { }
            }
        }

        private DatabaseDriver CreateDriver()
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            return DatabaseDriverFactory.Create(_Settings, logging);
        }
    }
}
