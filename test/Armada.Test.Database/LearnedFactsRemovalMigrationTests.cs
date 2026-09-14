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
    /// Learned-facts removal migration proof. A database one version below the removal holds learned
    /// playbooks and their links, the reflection pipelines, the memory consolidator persona and its
    /// templates, pack hints and reflection columns next to operator rows and native memory. The
    /// migration removes only the learned-facts rows, clears every reference to them, drops the
    /// learned-facts table and columns, restarts cleanly after an interruption and leaves the other
    /// rows unchanged.
    /// </summary>
    internal sealed class LearnedFactsRemovalMigrationTests
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
            internal string Reflections = "";
            internal string ReflectionsDualJudge = "";
            internal string CustomPipeline = "";
            internal string PipelineWithConsolidatorStage = "";
            internal string ConsolidatorOnlyPipeline = "";
            internal string FleetId = "";
            internal string VesselWithMixedDefaults = "";
            internal string VesselWithOnlyLearnedDefaults = "";
            internal string ConsolidatorCaptain = "";
            internal string WorkerCaptain = "";
            internal string ReflectionObjective = "";
            internal string CustomObjective = "";
            internal string VoyageId = "";
            internal string MissionId = "";
            internal string MemoryId = "";
        }

        internal LearnedFactsRemovalMigrationTests(DatabaseSettings settings) { _Settings = settings; }

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
            DatabaseAssert.True(before.Keys.All(key => key < version), "Learned-facts removal is still pending and no later version applied");
            DatabaseAssert.True(await TableExistsAsync("vessel_pack_hints", token).ConfigureAwait(false), "Pack hints exist before the removal");

            Seed seed;
            using (DatabaseDriver driver = CreateDriver())
            {
                seed = await SeedAsync(driver, token).ConfigureAwait(false);
            }

            await StopAtAsync(version, 3, token).ConfigureAwait(false);
            Dictionary<int, string> interrupted = await history.ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.Equal(before.Count, interrupted.Count, "An interrupted removal records no version");
            MigrationScenarioRunner.AssertHistory(before, interrupted);

            using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dictionary<int, string> committed = await history.ReadHistoryAsync(token).ConfigureAwait(false);
                // Later migrations may follow this one, so prove this version committed rather than a count.
                DatabaseAssert.True(committed.ContainsKey(version), "The restarted run commits the removal version");
                DatabaseAssert.True(committed.Keys.All(key => key <= version || !before.ContainsKey(key)), "Only pending versions are added");
                MigrationScenarioRunner.AssertHistory(before, committed);

                await AssertRemovedAsync(driver, seed, token).ConfigureAwait(false);
                await AssertRetainedAsync(driver, seed, token).ConfigureAwait(false);

                await driver.InitializeAsync(token).ConfigureAwait(false);
                MigrationScenarioRunner.AssertHistory(committed, await history.ReadHistoryAsync(token).ConfigureAwait(false));
                await AssertRetainedAsync(driver, seed, token).ConfigureAwait(false);
            }

            Console.WriteLine("PASS learned-facts removal migration: learned rows and links removed, references cleared, consolidator stages and allow-list entries removed, table and columns dropped, interrupted run restarts, operator rows and native memory unchanged");
        }

        private async Task<Seed> SeedAsync(DatabaseDriver driver, CancellationToken token)
        {
            string tenant = Constants.DefaultTenantId;
            Seed seed = new Seed();

            seed.LearnedVessel = await CreatePlaybookAsync(driver, "vessel-example-learned.md", token).ConfigureAwait(false);
            seed.LearnedPersona = await CreatePlaybookAsync(driver, "persona-worker-learned.md", token).ConfigureAwait(false);
            seed.LearnedCaptain = await CreatePlaybookAsync(driver, "captain-cpt_example-learned.md", token).ConfigureAwait(false);
            seed.LearnedFleet = await CreatePlaybookAsync(driver, "fleet-flt_example-learned.md", token).ConfigureAwait(false);
            seed.OperatorPlaybook = await CreatePlaybookAsync(driver, "vessel-guide.md", token).ConfigureAwait(false);
            seed.LookalikePlaybook = await CreatePlaybookAsync(driver, "release-notes-learned.md", token).ConfigureAwait(false);

            seed.Reflections = await CreatePipelineAsync(driver, "Reflections", new[] { "MemoryConsolidator" }, token).ConfigureAwait(false);
            seed.ReflectionsDualJudge = await CreatePipelineAsync(driver, "ReflectionsDualJudge", new[] { "MemoryConsolidator", "Judge" }, token).ConfigureAwait(false);
            seed.CustomPipeline = await CreatePipelineAsync(driver, "CustomReview", new[] { "Worker", "Judge" }, token).ConfigureAwait(false);
            seed.PipelineWithConsolidatorStage = await CreatePipelineAsync(driver, "OperatorWithConsolidator", new[] { "Worker", "MemoryConsolidator", "Judge" }, token).ConfigureAwait(false);
            seed.ConsolidatorOnlyPipeline = await CreatePipelineAsync(driver, "OperatorConsolidatorOnly", new[] { "MemoryConsolidator" }, token).ConfigureAwait(false);

            await CreateTemplateAsync(driver, "persona.memory_consolidator", "persona", token).ConfigureAwait(false);
            await CreateTemplateAsync(driver, "mission.model_context_updates", "mission", token).ConfigureAwait(false);
            await CreateTemplateAsync(driver, "persona.custom_reviewer", "persona", token).ConfigureAwait(false);

            Persona consolidator = new Persona("MemoryConsolidator", "persona.memory_consolidator");
            consolidator.TenantId = tenant;
            consolidator.DefaultPlaybooks = Selections(seed.LearnedPersona);
            await driver.Personas.CreateAsync(consolidator, token).ConfigureAwait(false);

            Persona reviewer = new Persona("CustomReviewer", "persona.custom_reviewer");
            reviewer.TenantId = tenant;
            reviewer.DefaultPlaybooks = Selections(seed.LearnedPersona, seed.OperatorPlaybook);
            await driver.Personas.CreateAsync(reviewer, token).ConfigureAwait(false);

            Fleet fleet = new Fleet("LearnedFactsFleet");
            fleet.TenantId = tenant;
            fleet.DefaultPipelineId = seed.Reflections;
            fleet.DefaultPlaybooks = Selections(seed.LearnedFleet, seed.OperatorPlaybook);
            seed.FleetId = (await driver.Fleets.CreateAsync(fleet, token).ConfigureAwait(false)).Id;

            Vessel mixed = new Vessel("MixedDefaults", "https://example.com/mixed.git");
            mixed.TenantId = tenant;
            mixed.FleetId = seed.FleetId;
            mixed.DefaultPipelineId = seed.Reflections;
            // One entry uses the PascalCase key an older writer produced; parsing is case-insensitive.
            mixed.DefaultPlaybooks = "[{\"playbookId\":\"" + seed.OperatorPlaybook + "\",\"deliveryMode\":\"InlineFullContent\"},"
                + "{\"PlaybookId\":\"" + seed.LearnedVessel + "\",\"DeliveryMode\":\"InlineFullContent\"}]";
            seed.VesselWithMixedDefaults = (await driver.Vessels.CreateAsync(mixed, token).ConfigureAwait(false)).Id;

            Vessel onlyLearned = new Vessel("OnlyLearnedDefaults", "https://example.com/learned.git");
            onlyLearned.TenantId = tenant;
            onlyLearned.DefaultPipelineId = seed.CustomPipeline;
            onlyLearned.DefaultPlaybooks = Selections(seed.LearnedVessel);
            seed.VesselWithOnlyLearnedDefaults = (await driver.Vessels.CreateAsync(onlyLearned, token).ConfigureAwait(false)).Id;

            Captain consolidatorCaptain = new Captain("consolidator-captain");
            consolidatorCaptain.TenantId = tenant;
            consolidatorCaptain.PreferredPersona = "MemoryConsolidator";
            consolidatorCaptain.AllowedPersonas = "[\"MemoryConsolidator\"]";
            consolidatorCaptain.DefaultPlaybooks = Selections(seed.LearnedCaptain);
            seed.ConsolidatorCaptain = (await driver.Captains.CreateAsync(consolidatorCaptain, token).ConfigureAwait(false)).Id;

            Captain workerCaptain = new Captain("worker-captain");
            workerCaptain.TenantId = tenant;
            workerCaptain.PreferredPersona = "Worker";
            workerCaptain.AllowedPersonas = "[\"Worker\",\"MemoryConsolidator\",\"Judge\"]";
            workerCaptain.DefaultPlaybooks = Selections(seed.OperatorPlaybook);
            seed.WorkerCaptain = (await driver.Captains.CreateAsync(workerCaptain, token).ConfigureAwait(false)).Id;

            Objective reflectionObjective = new Objective();
            reflectionObjective.TenantId = tenant;
            reflectionObjective.Title = "Consolidate memory";
            reflectionObjective.SuggestedPipelineId = seed.ReflectionsDualJudge;
            seed.ReflectionObjective = (await driver.Objectives.CreateAsync(reflectionObjective, token).ConfigureAwait(false)).Id;

            Objective customObjective = new Objective();
            customObjective.TenantId = tenant;
            customObjective.Title = "Ordinary review";
            customObjective.SuggestedPipelineId = seed.CustomPipeline;
            seed.CustomObjective = (await driver.Objectives.CreateAsync(customObjective, token).ConfigureAwait(false)).Id;

            Voyage voyage = new Voyage("Learned facts voyage");
            voyage.TenantId = tenant;
            seed.VoyageId = (await driver.Voyages.CreateAsync(voyage, token).ConfigureAwait(false)).Id;
            await driver.Playbooks.SetVoyageSelectionsAsync(seed.VoyageId, new List<SelectedPlaybook>
            {
                new SelectedPlaybook { PlaybookId = seed.LearnedVessel },
                new SelectedPlaybook { PlaybookId = seed.OperatorPlaybook }
            }, token).ConfigureAwait(false);

            Mission mission = new Mission("Learned facts mission");
            mission.TenantId = tenant;
            mission.VoyageId = seed.VoyageId;
            mission.VesselId = seed.VesselWithMixedDefaults;
            seed.MissionId = (await driver.Missions.CreateAsync(mission, token).ConfigureAwait(false)).Id;
            await driver.Playbooks.SetMissionSnapshotsAsync(seed.MissionId, new List<MissionPlaybookSnapshot>
            {
                new MissionPlaybookSnapshot { PlaybookId = seed.LearnedVessel, FileName = "vessel-example-learned.md", Content = "learned" },
                new MissionPlaybookSnapshot { PlaybookId = seed.OperatorPlaybook, FileName = "vessel-guide.md", Content = "operator" }
            }, token).ConfigureAwait(false);

            Memory memory = new Memory();
            memory.TenantId = tenant;
            memory.UserId = Constants.DefaultUserId;
            memory.Key = "learned-facts-removal/native";
            memory.Content = "Native memory survives the learned-facts removal.";
            seed.MemoryId = (await driver.Memories.CreateAsync(memory, token).ConfigureAwait(false)).Id;

            await ExecuteAsync("UPDATE vessels SET reflection_threshold = 5, reorganize_threshold = 6, pack_curate_threshold = 7, last_reflection_mission_id = 'msn_reflection' WHERE id = @id;",
                token, ("@id", seed.VesselWithMixedDefaults)).ConfigureAwait(false);
            await ExecuteAsync("UPDATE personas SET curate_threshold = 3, learned_playbook_id = @playbook WHERE name = 'MemoryConsolidator';",
                token, ("@playbook", seed.LearnedPersona)).ConfigureAwait(false);
            await ExecuteAsync("UPDATE captains SET curate_threshold = 4, learned_playbook_id = @playbook WHERE id = @id;",
                token, ("@playbook", seed.LearnedCaptain), ("@id", seed.ConsolidatorCaptain)).ConfigureAwait(false);
            await ExecuteAsync("UPDATE fleets SET curate_threshold = 2, learned_playbook_id = @playbook WHERE id = @id;",
                token, ("@playbook", seed.LearnedFleet), ("@id", seed.FleetId)).ConfigureAwait(false);
            await ExecuteAsync("INSERT INTO vessel_pack_hints (id, vessel_id, goal_pattern, must_include, must_exclude, priority, confidence, source_mission_ids, active, created_utc, last_update_utc) "
                + "VALUES ('vph_example', @vessel, 'build', '[]', '[]', 1, 'high', '[]', @active, @created, @created);",
                token, ("@vessel", seed.VesselWithMixedDefaults), ("@active", true), ("@created", Timestamp())).ConfigureAwait(false);

            return seed;
        }

        private async Task AssertRemovedAsync(DatabaseDriver driver, Seed seed, CancellationToken token)
        {
            foreach (string learned in new[] { seed.LearnedVessel, seed.LearnedPersona, seed.LearnedCaptain, seed.LearnedFleet })
                DatabaseAssert.True(await driver.Playbooks.ReadAsync(learned, token).ConfigureAwait(false) == null, "Learned playbook " + learned + " is removed");

            DatabaseAssert.True(await driver.Pipelines.ReadByNameAsync("Reflections", token).ConfigureAwait(false) == null, "Reflections pipeline is removed");
            DatabaseAssert.True(await driver.Pipelines.ReadByNameAsync("ReflectionsDualJudge", token).ConfigureAwait(false) == null, "Dual-Judge reflections pipeline is removed");
            DatabaseAssert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM pipeline_stages WHERE pipeline_id IN (@a, @b);", token,
                ("@a", seed.Reflections), ("@b", seed.ReflectionsDualJudge)).ConfigureAwait(false), "Reflection pipeline stages are removed");
            DatabaseAssert.True(await driver.Pipelines.ReadAsync(seed.ConsolidatorOnlyPipeline, token).ConfigureAwait(false) == null, "A pipeline left with no stages is removed");
            DatabaseAssert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM pipeline_stages WHERE persona_name = @persona OR pipeline_id = @id;", token,
                ("@persona", "MemoryConsolidator"), ("@id", seed.ConsolidatorOnlyPipeline)).ConfigureAwait(false), "No stage names the removed consolidator");

            DatabaseAssert.True(await driver.Personas.ReadByNameAsync("MemoryConsolidator", token).ConfigureAwait(false) == null, "Memory consolidator persona is removed");
            DatabaseAssert.True(await driver.PromptTemplates.ReadByNameAsync("persona.memory_consolidator", token).ConfigureAwait(false) == null, "Consolidator template is removed");
            DatabaseAssert.True(await driver.PromptTemplates.ReadByNameAsync("mission.model_context_updates", token).ConfigureAwait(false) == null, "Learned-fact proposal template is removed");

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
            DatabaseAssert.True(fleet.DefaultPipelineId == null, "Fleet default pipeline reference to reflections is cleared");
            AssertOnlyOperatorDefault(fleet.GetDefaultPlaybooks(), seed, "Fleet");

            Vessel mixed = DatabaseAssert.NotNull(await driver.Vessels.ReadAsync(seed.VesselWithMixedDefaults, token).ConfigureAwait(false), "Mixed vessel retained");
            DatabaseAssert.True(mixed.DefaultPipelineId == null, "Vessel default pipeline reference to reflections is cleared");
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

            Objective reflectionObjective = DatabaseAssert.NotNull(await driver.Objectives.ReadAsync(seed.ReflectionObjective, token).ConfigureAwait(false), "Objective retained");
            DatabaseAssert.True(reflectionObjective.SuggestedPipelineId == null, "Objective reference to a reflection pipeline is cleared");
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

        private static async Task<string> CreatePlaybookAsync(DatabaseDriver driver, string fileName, CancellationToken token)
        {
            Playbook playbook = new Playbook(fileName, "# " + fileName + "\n\n- guidance\n");
            playbook.TenantId = Constants.DefaultTenantId;
            playbook.Active = false;
            return (await driver.Playbooks.CreateAsync(playbook, token).ConfigureAwait(false)).Id;
        }

        private static async Task<string> CreatePipelineAsync(DatabaseDriver driver, string name, string[] personas, CancellationToken token)
        {
            Pipeline pipeline = new Pipeline(name);
            pipeline.TenantId = Constants.DefaultTenantId;
            pipeline.Stages = personas.Select((persona, index) => new PipelineStage(index + 1, persona)).ToList();
            return (await driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false)).Id;
        }

        private static async Task CreateTemplateAsync(DatabaseDriver driver, string name, string category, CancellationToken token)
        {
            PromptTemplate template = new PromptTemplate(name, "template body for " + name);
            template.TenantId = Constants.DefaultTenantId;
            template.Category = category;
            await driver.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
        }

        private object Timestamp()
        {
            DateTime timestamp = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            // SQLite and SQL Server store this historical column as text; the other providers as a timestamp.
            return _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                ? timestamp.ToString("o", CultureInfo.InvariantCulture)
                : timestamp;
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

        private async Task ExecuteAsync(string sql, CancellationToken token, params (string Name, object Value)[] parameters)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = Command(connection, sql, parameters))
                {
                    int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    DatabaseAssert.True(changed == 1, "Fixture statement changes exactly one row: " + sql);
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
                try { await driver.InitializeAsync(token).ConfigureAwait(false); throw new Exception("Learned-facts removal checkpoint was not reached"); }
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
