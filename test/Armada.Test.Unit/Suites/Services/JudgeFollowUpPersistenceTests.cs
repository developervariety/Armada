namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

    /// <summary>
    /// Tests durable Judge follow-up capture and late merge association.
    /// </summary>
    public class JudgeFollowUpPersistenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Judge Follow-up Persistence";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("All providers register the durable follow-up migration", () =>
            {
                AssertMigration(SqliteTableQueries.GetMigrations(), 80, "SQLite");
                AssertMigration(PostgresqlTableQueries.GetMigrations(), 82, "PostgreSQL");
                AssertMigration(SqlServerTableQueries.GetMigrations(), 76, "SQL Server");

                AssertContains("judge_follow_ups", MysqlTableQueries.MigrationV73Statements[0]);
                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                    "GetMigrations",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL must expose its migration registration list internally");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlGetMigrations!.Invoke(null, Array.Empty<object>())!;
                AssertMigration(mysql, 73, "MySQL");
                return Task.CompletedTask;
            });

            await RunTest("Capture persists without a predecessor merge entry and is idempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-no-merge", "vsl_follow-up-one");

                    JudgeFollowUp first = await service.CaptureAsync(
                        judge, "PASS", "- Add the missing boundary test.").ConfigureAwait(false);
                    JudgeFollowUp second = await service.CaptureAsync(
                        judge, "PASS", "- Add the missing boundary test.").ConfigureAwait(false);

                    AssertEqual(first.Id, second.Id, "Repeated completion handling must keep one canonical record");
                    AssertNull(second.MergeEntryId, "A missing merge entry must not discard or fake the association");
                    List<JudgeFollowUp> pending = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync().ConfigureAwait(false);
                    AssertEqual(1, pending.Count, "The Judge mission is the idempotency key");
                    AssertEqual(judge.Id, pending[0].JudgeMissionId);
                    AssertContains("missing boundary test", pending[0].SuggestedFollowUps ?? String.Empty);
                }
            });

            await RunTest("Follow-up text splits into one item per bullet and holds no item for an explicit none", () =>
            {
                List<string> items = JudgeFollowUpItems.Split("- Add the boundary test.\n  It covers the empty frame.\n* Rename the helper.\n2. Pin the clock in the retry test.");
                AssertEqual(3, items.Count, "three bullets are three items");
                AssertEqual("Add the boundary test. It covers the empty frame.", items[0], "an indented line continues its bullet");
                AssertEqual("Rename the helper.", items[1]);
                AssertEqual("Pin the clock in the retry test.", items[2], "a numbered line is an item");
                AssertEqual(1, JudgeFollowUpItems.Split("Document the retry limit.").Count, "unmarked text is one item");
                foreach (string? none in new[] { "(none)", "- None", "None.", "n/a", "No follow-up needed.", "  ", null })
                    AssertEqual(0, JudgeFollowUpItems.Split(none).Count, "'" + none + "' holds no item");
                return Task.CompletedTask;
            });

            await RunTest("The follow-up listing returns reviews with items, newest first, inside the window and vessel filter", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime now = DateTime.UtcNow;
                    await StoreFollowUpAsync(testDb.Driver, "msn_judge-old", "vsl_list-a", "- Old item.", now.AddHours(-300)).ConfigureAwait(false);
                    await StoreFollowUpAsync(testDb.Driver, "msn_judge-none", "vsl_list-a", "(none)", now.AddHours(-3)).ConfigureAwait(false);
                    await StoreFollowUpAsync(testDb.Driver, "msn_judge-older", "vsl_list-a", "- First item.\n- Second item.", now.AddHours(-20)).ConfigureAwait(false);
                    await StoreFollowUpAsync(testDb.Driver, "msn_judge-newer", "vsl_list-a", "- Newest item.", now.AddHours(-2)).ConfigureAwait(false);
                    await StoreFollowUpAsync(testDb.Driver, "msn_judge-other", "vsl_list-b", "- Other vessel item.", now.AddHours(-1)).ConfigureAwait(false);

                    Func<System.Text.Json.JsonElement?, Task<object>>? list = null;
                    Armada.Server.Mcp.Tools.McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == Armada.Server.Mcp.Tools.McpAuditTools.ListJudgeFollowUpsToolName) list = McpTestCaller.Wrap(handler);
                    }, testDb.Driver);
                    AssertNotNull(list, "the listing tool is registered");

                    string json = System.Text.Json.JsonSerializer.Serialize(await list!(System.Text.Json.JsonSerializer.SerializeToElement(new { vesselId = "vsl_list-a", sinceHours = 168 })).ConfigureAwait(false));
                    AssertContains("\"ReviewsWithFollowUps\":2", json, "the explicit none and the out-of-window review are left out");
                    AssertContains("\"TotalItems\":3", json);
                    AssertFalse(json.Contains("Other vessel item", StringComparison.Ordinal), "the vessel filter holds");
                    AssertFalse(json.Contains("Old item", StringComparison.Ordinal), "the window holds");
                    AssertTrue(json.IndexOf("msn_judge-newer", StringComparison.Ordinal) < json.IndexOf("msn_judge-older", StringComparison.Ordinal), "newest first");

                    string paged = System.Text.Json.JsonSerializer.Serialize(await list(System.Text.Json.JsonSerializer.SerializeToElement(new { vesselId = "vsl_list-a", limit = 1, offset = 1 })).ConfigureAwait(false));
                    AssertContains("msn_judge-older", paged, "offset reaches the second review");
                    AssertContains("\"HasMore\":false", paged);
                }
            });

            await RunTest("All providers register the follow-up handled-item migration", () =>
            {
                AssertHandledItemMigration(SqliteTableQueries.GetMigrations(), 115, "SQLite");
                AssertHandledItemMigration(PostgresqlTableQueries.GetMigrations(), 119, "PostgreSQL");
                AssertHandledItemMigration(SqlServerTableQueries.GetMigrations(), 111, "SQL Server");
                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                    "GetMigrations",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertHandledItemMigration((List<SchemaMigration>)mysqlGetMigrations!.Invoke(null, Array.Empty<object>())!, 108, "MySQL");
                return Task.CompletedTask;
            });

            await RunTest("A handled marker hides its item from the follow-up listing, survives a restart, and the listing counts what it omitted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime now = DateTime.UtcNow;
                    JudgeFollowUp twoItems = await StoreFollowUpAsync(testDb.Driver, "msn_judge-two", "vsl_handled", "- First handled item.\n- Second open item.", now.AddHours(-3)).ConfigureAwait(false);
                    JudgeFollowUp oneItem = await StoreFollowUpAsync(testDb.Driver, "msn_judge-one", "vsl_handled", "- Only item, decided.", now.AddHours(-2)).ConfigureAwait(false);

                    Func<System.Text.Json.JsonElement?, Task<object>>? list = null;
                    Func<System.Text.Json.JsonElement?, Task<object>>? record = null;
                    Armada.Server.Mcp.Tools.McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == Armada.Server.Mcp.Tools.McpAuditTools.ListJudgeFollowUpsToolName) list = McpTestCaller.Wrap(handler);
                        if (name == Armada.Server.Mcp.Tools.McpAuditTools.RecordJudgeFollowUpHandledToolName) record = McpTestCaller.Wrap(handler);
                    }, testDb.Driver);
                    AssertNotNull(record, "the handled-marker tool is registered");

                    object first = await record!(System.Text.Json.JsonSerializer.SerializeToElement(new { followUpId = twoItems.Id, itemNumber = 1, kind = "Note", disposition = "covered by the retry fix" })).ConfigureAwait(false);
                    AssertTrue(first is JudgeFollowUpHandledItem, "a valid marker is recorded: " + System.Text.Json.JsonSerializer.Serialize(first));
                    object second = await record(System.Text.Json.JsonSerializer.SerializeToElement(new { followUpId = oneItem.Id, itemNumber = 1, kind = "Decision", disposition = "owner declined" })).ConfigureAwait(false);
                    AssertTrue(second is JudgeFollowUpHandledItem, "a decision marker is recorded");

                    string open = System.Text.Json.JsonSerializer.Serialize(await list!(System.Text.Json.JsonSerializer.SerializeToElement(new { vesselId = "vsl_handled" })).ConfigureAwait(false));
                    AssertContains("\"HandledItemsOmitted\":2", open, "the listing counts the handled items it left out");
                    AssertContains("\"OpenItems\":1", open);
                    AssertContains("Second open item.", open, "an open item of a partly handled review stays listed");
                    AssertFalse(open.Contains("First handled item.", StringComparison.Ordinal), "a handled item is hidden by default");
                    AssertFalse(open.Contains("msn_judge-one", StringComparison.Ordinal), "a review whose items are all handled is hidden");
                    AssertContains("\"Number\":2", open, "an open item keeps its original item number");

                    string all = System.Text.Json.JsonSerializer.Serialize(await list(System.Text.Json.JsonSerializer.SerializeToElement(new { vesselId = "vsl_handled", includeHandled = true })).ConfigureAwait(false));
                    AssertContains("First handled item.", all, "includeHandled lists handled items");
                    AssertContains("covered by the retry fix", all, "a listed handled item carries its disposition");
                    AssertContains("msn_judge-one", all);
                    AssertContains("\"HandledItemsOmitted\":0", all);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    using (Armada.Core.Database.Sqlite.SqliteDatabaseDriver restarted = new Armada.Core.Database.Sqlite.SqliteDatabaseDriver(testDb.ConnectionString, logging))
                    {
                        await restarted.InitializeAsync().ConfigureAwait(false);
                        JudgeFollowUpHandledItem? stored = await restarted.JudgeFollowUpHandledItems.ReadAsync(twoItems.Id, 1).ConfigureAwait(false);
                        AssertNotNull(stored, "the marker persists across a restart");
                        AssertEqual(Armada.Core.Enums.JudgeFollowUpDispositionKindEnum.Note, stored!.Kind);
                        AssertEqual("covered by the retry fix", stored.Disposition);
                        AssertEqual("Test operator", stored.HandledBy, "the marker records who handled the item");
                        AssertNull(await restarted.JudgeFollowUpHandledItems.ReadAsync(twoItems.Id, 2).ConfigureAwait(false), "an unmarked item has no marker");
                    }
                }
            });

            await RunTest("Recording a handled marker refuses an unknown follow-up, item number, kind or objective with a named reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    JudgeFollowUp followUp = await StoreFollowUpAsync(testDb.Driver, "msn_judge-refuse", "vsl_refuse", "- One.\n- Two.", DateTime.UtcNow.AddHours(-1)).ConfigureAwait(false);
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "fix home" }).ConfigureAwait(false);
                    Func<System.Text.Json.JsonElement?, Task<object>>? raw = null;
                    Armada.Server.Mcp.Tools.McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == Armada.Server.Mcp.Tools.McpAuditTools.RecordJudgeFollowUpHandledToolName) raw = handler;
                    }, testDb.Driver);
                    AssertNotNull(raw, "the handled-marker tool is registered");
                    Func<System.Text.Json.JsonElement?, Task<object>> record = McpTestCaller.Wrap(raw!);
                    async Task<string> CallAsync(object args)
                    {
                        return System.Text.Json.JsonSerializer.Serialize(await record(System.Text.Json.JsonSerializer.SerializeToElement(args)).ConfigureAwait(false));
                    }

                    AssertContains("follow_up_not_found", await CallAsync(new { followUpId = "jfu_missing", itemNumber = 1, kind = "Note", disposition = "x" }).ConfigureAwait(false));
                    AssertContains("item_out_of_range", await CallAsync(new { followUpId = followUp.Id, itemNumber = 3, kind = "Note", disposition = "x" }).ConfigureAwait(false), "the review has two items");
                    AssertContains("item_out_of_range", await CallAsync(new { followUpId = followUp.Id, itemNumber = 0, kind = "Note", disposition = "x" }).ConfigureAwait(false), "item numbers start at one");
                    AssertContains("kind_invalid", await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Maybe", disposition = "x" }).ConfigureAwait(false));
                    AssertContains("disposition_required", await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Note", disposition = " " }).ConfigureAwait(false));
                    AssertContains("objective_not_found", await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Objective", disposition = "obj_missing" }).ConfigureAwait(false));

                    string recorded = await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Objective", disposition = objective.Id }).ConfigureAwait(false);
                    AssertContains(objective.Id, recorded, "an existing objective is a valid home");
                    AssertContains("item_already_handled", await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Note", disposition = "again" }).ConfigureAwait(false), "a second marker for one item is refused");
                    string replaced = await CallAsync(new { followUpId = followUp.Id, itemNumber = 1, kind = "Note", disposition = "superseded by a note", replace = true }).ConfigureAwait(false);
                    AssertContains("superseded by a note", replaced, "replace records the corrected disposition");
                    JudgeFollowUpHandledItem? stored = await testDb.Driver.JudgeFollowUpHandledItems.ReadAsync(followUp.Id, 1).ConfigureAwait(false);
                    AssertEqual("superseded by a note", stored!.Disposition, "the replacement is the one stored");

                    AuthContext captain = McpTestCaller.Operator;
                    captain.MissionId = "msn_captain";
                    using (McpCallerContext.Begin(captain))
                    {
                        string refused = System.Text.Json.JsonSerializer.Serialize(await raw!(System.Text.Json.JsonSerializer.SerializeToElement(new { followUpId = followUp.Id, itemNumber = 2, kind = "Note", disposition = "x" })).ConfigureAwait(false));
                        AssertContains("operator_required", refused, "a mission credential cannot record a marker");
                    }
                }
            });

            await RunTest("Bounded backfill records actionable and explicit-none sections exactly once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DateTime fromUtc = DateTime.UtcNow.AddHours(-2);
                    Mission actionable = new Mission("Backfill actionable", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n## Suggested Follow-ups\n- Add the missed guard.",
                        CreatedUtc = DateTime.UtcNow.AddHours(-1)
                    };
                    Mission explicitNone = new Mission("Backfill none", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n## Suggested Follow-ups\n(none)",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-50)
                    };
                    Mission compatibleLabel = new Mission("Backfill compatible label", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS\n\n**Tracked follow-ups (non-blocking):** - Preserve this real-world variant.",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-45)
                    };
                    Mission missingSection = new Mission("Backfill missing", "Review")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Complete,
                        AgentOutput = "[ARMADA:VERDICT] PASS",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-40)
                    };
                    actionable = await testDb.Driver.Missions.CreateAsync(actionable).ConfigureAwait(false);
                    explicitNone = await testDb.Driver.Missions.CreateAsync(explicitNone).ConfigureAwait(false);
                    compatibleLabel = await testDb.Driver.Missions.CreateAsync(compatibleLabel).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(missingSection).ConfigureAwait(false);

                    JudgeFollowUpBackfillService service = new JudgeFollowUpBackfillService(testDb.Driver, logging);
                    JudgeFollowUpBackfillService.Result preview = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), true, 10).ConfigureAwait(false);
                    AssertEqual(3, preview.WouldCreate, "Dry run must report every recognized missing durable row");
                    AssertEqual(0, preview.Created, "Dry run must not claim that it wrote rows");
                    AssertEqual(2, preview.Actionable);
                    AssertEqual(1, preview.ExplicitNone);
                    AssertEqual(0, (await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync().ConfigureAwait(false)).Count);

                    JudgeFollowUpBackfillService.Result first = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), false, 10).ConfigureAwait(false);
                    AssertEqual(3, first.Created);
                    AssertEqual(0, first.Errors);
                    AssertFalse(first.Incomplete);
                    AssertNotNull(await testDb.Driver.JudgeFollowUps.ReadByJudgeMissionAsync(actionable.Id).ConfigureAwait(false));
                    AssertNotNull(await testDb.Driver.JudgeFollowUps.ReadByJudgeMissionAsync(compatibleLabel.Id).ConfigureAwait(false));
                    JudgeFollowUp? noneRow = await testDb.Driver.JudgeFollowUps
                        .ReadByJudgeMissionAsync(explicitNone.Id).ConfigureAwait(false);
                    AssertNotNull(noneRow, "Explicit (none) is durable reconciliation evidence");
                    AssertNull(noneRow!.SuggestedFollowUps);

                    JudgeFollowUpBackfillService.Result second = await service.RunAsync(
                        fromUtc, DateTime.UtcNow.AddMinutes(1), false, 10).ConfigureAwait(false);
                    AssertEqual(0, second.Created, "A repeat pass must create no rows");
                    AssertEqual(3, second.AlreadyPresent);
                }
            });

            await RunTest("Post-work replay recovers a failed FAIL follow-up write", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("replay-captain")).ConfigureAwait(false);
                    Mission judge = new Mission("Judge replay", "Review the work")
                    {
                        Persona = "Judge",
                        Status = Armada.Core.Enums.MissionStatusEnum.Failed,
                        AgentOutput = "[ARMADA:VERDICT] FAIL\n\n## Suggested Follow-ups\n- Preserve this failed-review action.",
                        FailureReason = "Judge verdict: FAIL"
                    };
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    IJudgeFollowUpMethods inner = testDb.Driver.JudgeFollowUps;
                    FailOnceJudgeFollowUps failOnce = new FailOnceJudgeFollowUps(inner);
                    typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.JudgeFollowUps))!
                        .SetValue(testDb.Driver, failOnce);

                    await AssertThrowsAsync<InvalidOperationException>(
                        () => missionService.HandleCompletionAsync(captain, judge.Id),
                        "The first durable write is intentionally faulted").ConfigureAwait(false);
                    await missionService.HandleCompletionAsync(captain, judge.Id).ConfigureAwait(false);

                    List<JudgeFollowUp> pending = await inner.EnumeratePendingAsync().ConfigureAwait(false);
                    AssertEqual(1, pending.Count, "The post-work idempotency path must replay the durable write");
                    AssertEqual("FAIL", pending[0].JudgeVerdict);
                    AssertContains("failed-review action", pending[0].SuggestedFollowUps ?? String.Empty);
                }
            });

            await RunTest("Capture associates immediately when the reviewed merge entry exists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-existing", "vsl_follow-up-two");
                    MergeEntry entry = new MergeEntry("reviewed-branch", "main")
                    {
                        MissionId = judge.DependsOnMissionId,
                        VesselId = judge.VesselId
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    JudgeFollowUp followUp = await service.CaptureAsync(
                        judge, "NEEDS_REVISION", null).ConfigureAwait(false);

                    AssertEqual(entry.Id, followUp.MergeEntryId, "Capture should associate after the durable write");
                    MergeEntry? reloaded = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(true, reloaded!.AuditDeepPicked);
                    AssertEqual("Pending", reloaded.AuditDeepVerdict);
                    AssertContains("NEEDS_REVISION", reloaded.AuditDeepNotes ?? String.Empty);

                    MergeEntry newer = new MergeEntry("newer-judge-branch", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow.AddMinutes(1)
                    };
                    newer = await testDb.Driver.MergeEntries.CreateAsync(newer).ConfigureAwait(false);
                    JudgeFollowUp repeated = await service.CaptureAsync(judge, "NEEDS_REVISION", null).ConfigureAwait(false);
                    AssertEqual(entry.Id, repeated.MergeEntryId, "Repeated capture must not move an existing association");
                    MergeEntry? unflaggedNewer = await testDb.Driver.MergeEntries.ReadAsync(newer.Id).ConfigureAwait(false);
                    AssertFalse(unflaggedNewer!.AuditDeepPicked == true, "Repeated capture must not flag a second merge entry");
                }
            });

            await RunTest("Reviewed mission delivery takes precedence over a newer Judge delivery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-preferred", "vsl_preferred");
                    MergeEntry reviewed = new MergeEntry("reviewed", "main")
                    {
                        MissionId = judge.DependsOnMissionId,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                    };
                    reviewed = await testDb.Driver.MergeEntries.CreateAsync(reviewed).ConfigureAwait(false);
                    MergeEntry judgeDelivery = new MergeEntry("judge", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId,
                        CreatedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.MergeEntries.CreateAsync(judgeDelivery).ConfigureAwait(false);

                    JudgeFollowUp followUp = await service.CaptureAsync(judge, "PASS", "- Verify preference.").ConfigureAwait(false);

                    AssertEqual(reviewed.Id, followUp.MergeEntryId, "Worker delivery is authoritative when both delivery records exist");
                }
            });

            await RunTest("A later merge enqueue associates the durable follow-up", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService followUpService = new JudgeFollowUpService(testDb.Driver, logging);
                    Mission judge = BuildJudgeMission("msn_reviewed-late", "vsl_follow-up-three");
                    JudgeFollowUp followUp = await followUpService.CaptureAsync(
                        judge, "PASS", "- Preserve accepted work on rescue.").ConfigureAwait(false);
                    AssertNull(followUp.MergeEntryId);
                    followUp.AuditVerdict = "Concern";
                    followUp.AuditNotes = "Verified before delivery metadata arrived.";
                    followUp.AuditCompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.JudgeFollowUps.UpdateAsync(followUp).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    MergeQueueService mergeQueue = new MergeQueueService(
                        logging,
                        testDb.Driver,
                        settings,
                        new StubGitService(),
                        new MergeFailureClassifier());
                    MergeEntry entry = new MergeEntry("judge-branch", "main")
                    {
                        MissionId = judge.Id,
                        VesselId = judge.VesselId
                    };
                    entry = await mergeQueue.EnqueueAsync(entry).ConfigureAwait(false);

                    JudgeFollowUp? reloaded = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual(entry.Id, reloaded!.MergeEntryId, "The merge queue must reconcile a Judge-first race");
                    MergeEntry? mirrored = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Concern", mirrored!.AuditDeepVerdict, "Late association must preserve a completed audit verdict");
                    AssertEqual("Verified before delivery metadata arrived.", mirrored.AuditDeepNotes);
                    AssertNotNull(mirrored.AuditDeepCompletedUtc);
                }
            });

            await RunTest("A merge entry of another tenant never takes a follow-up", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    foreach (string tenantId in new[] { "ten_followup_owner", "ten_followup_other" })
                    {
                        if (await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false) == null)
                            await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = tenantId, Name = tenantId }).ConfigureAwait(false);
                    }

                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    JudgeFollowUp followUp = BuildFollowUp(
                        "msn_judge-cross", "msn_reviewed-cross", "vsl_cross", DateTime.UtcNow);
                    followUp.TenantId = "ten_followup_owner";
                    followUp = await testDb.Driver.JudgeFollowUps.UpsertAsync(followUp).ConfigureAwait(false);
                    followUp.AuditVerdict = "Concern";
                    followUp.AuditNotes = "Owner-only audit notes.";
                    followUp.AuditCompletedUtc = DateTime.UtcNow;
                    followUp = await testDb.Driver.JudgeFollowUps.UpdateAsync(followUp).ConfigureAwait(false);
                    AssertEqual("ten_followup_owner", followUp.TenantId, "The follow-up keeps its tenant");

                    MergeEntry foreign = new MergeEntry("cross-branch", "main")
                    {
                        TenantId = "ten_followup_other",
                        MissionId = followUp.ReviewedMissionId,
                        VesselId = followUp.VesselId
                    };
                    foreign = await testDb.Driver.MergeEntries.CreateAsync(foreign).ConfigureAwait(false);

                    AssertEqual(0, await service.AssociateForMergeEntryAsync(foreign).ConfigureAwait(false), "Enqueue association stays inside the tenant");
                    AssertEqual(0, await service.ReconcilePendingAssociationsAsync(null).ConfigureAwait(false), "Late reconciliation stays inside the tenant");

                    JudgeFollowUp? reloaded = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertNull(reloaded!.MergeEntryId, "The follow-up is not attached to another tenant's entry");
                    MergeEntry? untouched = await testDb.Driver.MergeEntries.ReadAsync(foreign.Id).ConfigureAwait(false);
                    AssertNull(untouched!.AuditDeepVerdict, "Another tenant's entry does not receive the audit verdict");
                    AssertNull(untouched.AuditDeepNotes, "Another tenant's entry does not receive the audit notes");

                    MergeEntry own = new MergeEntry("own-branch", "main")
                    {
                        TenantId = "ten_followup_owner",
                        MissionId = followUp.ReviewedMissionId,
                        VesselId = followUp.VesselId
                    };
                    own = await testDb.Driver.MergeEntries.CreateAsync(own).ConfigureAwait(false);
                    AssertEqual(1, await service.AssociateForMergeEntryAsync(own).ConfigureAwait(false), "The owner's entry takes the follow-up");
                }
            });

            await RunTest("Concurrent audit completion and association preserve both fields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    JudgeFollowUpService service = new JudgeFollowUpService(testDb.Driver, logging);
                    JudgeFollowUp followUp = BuildFollowUp(
                        "msn_judge-race", "msn_reviewed-race", "vsl_race", DateTime.UtcNow);
                    followUp = await testDb.Driver.JudgeFollowUps.UpsertAsync(followUp).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("race-branch", "main")
                    {
                        MissionId = followUp.ReviewedMissionId,
                        VesselId = followUp.VesselId
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Task associate = service.AssociateForMergeEntryAsync(entry);
                    Task complete = service.CompleteAuditAsync(
                        followUp.Id, "Concern", "Concurrent audit.", null, DateTime.UtcNow);
                    await Task.WhenAll(associate, complete).ConfigureAwait(false);

                    JudgeFollowUp? canonical = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual(entry.Id, canonical!.MergeEntryId, "Atomic association must survive audit completion");
                    AssertEqual("Concern", canonical.AuditVerdict, "Field-specific audit completion must survive association");
                    AssertEqual("Concurrent audit.", canonical.AuditNotes);
                    MergeEntry? mirror = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Concern", mirror!.AuditDeepVerdict, "The item lock must keep the merge mirror current");
                    AssertEqual("Concurrent audit.", mirror.AuditDeepNotes);
                    AssertNotNull(mirror.AuditDeepCompletedUtc);
                }
            });

            await RunTest("Pending enumeration is oldest first and filters by vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    JudgeFollowUp older = BuildFollowUp("msn_judge-old", "msn_reviewed-old", "vsl_filter", DateTime.UtcNow.AddHours(-2));
                    JudgeFollowUp newer = BuildFollowUp("msn_judge-new", "msn_reviewed-new", "vsl_filter", DateTime.UtcNow.AddHours(-1));
                    JudgeFollowUp other = BuildFollowUp("msn_judge-other", "msn_reviewed-other", "vsl_other", DateTime.UtcNow.AddHours(-3));
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(newer).ConfigureAwait(false);
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(other).ConfigureAwait(false);
                    await testDb.Driver.JudgeFollowUps.UpsertAsync(older).ConfigureAwait(false);

                    List<JudgeFollowUp> pending = await testDb.Driver.JudgeFollowUps
                        .EnumeratePendingAsync("vsl_filter")
                        .ConfigureAwait(false);

                    AssertEqual(2, pending.Count);
                    AssertEqual(older.JudgeMissionId, pending[0].JudgeMissionId);
                    AssertEqual(newer.JudgeMissionId, pending[1].JudgeMissionId);
                }
            });
        }

        private void AssertMigration(List<SchemaMigration> migrations, int version, string provider)
        {
            SchemaMigration? migration = migrations.Find(candidate => candidate.Version == version);
            AssertNotNull(migration, provider + " must register the durable follow-up migration");
            AssertTrue(migration!.Statements.Any(statement => statement.Contains("judge_follow_ups", StringComparison.Ordinal)),
                provider + " migration must create the durable follow-up table");
        }

        private void AssertHandledItemMigration(List<SchemaMigration> migrations, int version, string provider)
        {
            SchemaMigration? migration = migrations.Find(candidate => candidate.Version == version);
            AssertNotNull(migration, provider + " must register the follow-up handled-item migration at v" + version);
            AssertTrue(migration!.Statements.Any(statement => statement.Contains("judge_follow_up_handled_items", StringComparison.Ordinal)),
                provider + " migration must create the handled-item table");
        }

        private static Mission BuildJudgeMission(string reviewedMissionId, string vesselId)
        {
            Mission judge = new Mission("Judge the change", "Review the accepted work");
            judge.Persona = "Judge";
            judge.DependsOnMissionId = reviewedMissionId;
            judge.VesselId = vesselId;
            judge.VoyageId = "vyg_follow-up";
            return judge;
        }

        private static JudgeFollowUp BuildFollowUp(
            string judgeMissionId,
            string reviewedMissionId,
            string vesselId,
            DateTime createdUtc)
        {
            return new JudgeFollowUp
            {
                JudgeMissionId = judgeMissionId,
                ReviewedMissionId = reviewedMissionId,
                VesselId = vesselId,
                JudgeVerdict = "PASS",
                SuggestedFollowUps = "- Follow up.",
                AuditVerdict = "Pending",
                CreatedUtc = createdUtc,
                LastUpdateUtc = createdUtc
            };
        }

        private static Task<JudgeFollowUp> StoreFollowUpAsync(DatabaseDriver database, string judgeMissionId, string vesselId, string text, DateTime createdUtc)
        {
            return database.JudgeFollowUps.UpsertAsync(new JudgeFollowUp
            {
                JudgeMissionId = judgeMissionId,
                ReviewedMissionId = judgeMissionId + "-reviewed",
                VesselId = vesselId,
                SuggestedFollowUps = text,
                CreatedUtc = createdUtc
            });
        }

        private sealed class FailOnceJudgeFollowUps : IJudgeFollowUpMethods
        {
            private readonly IJudgeFollowUpMethods _Inner;
            private bool _ShouldFail = true;

            public FailOnceJudgeFollowUps(IJudgeFollowUpMethods inner) => _Inner = inner;

            public Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp followUp, CancellationToken token = default)
            {
                if (_ShouldFail)
                {
                    _ShouldFail = false;
                    throw new InvalidOperationException("simulated durable follow-up write failure");
                }
                return _Inner.UpsertAsync(followUp, token);
            }

            public Task<JudgeFollowUp?> ReadAsync(string id, CancellationToken token = default) => _Inner.ReadAsync(id, token);
            public Task<JudgeFollowUp?> ReadByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default) => _Inner.ReadByJudgeMissionAsync(judgeMissionId, token);
            public Task<JudgeFollowUp?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default) => _Inner.ReadByMergeEntryAsync(mergeEntryId, token);
            public Task<JudgeFollowUp> UpdateAsync(JudgeFollowUp followUp, CancellationToken token = default) => _Inner.UpdateAsync(followUp, token);
            public Task<bool> TryAssociateAsync(string followUpId, string mergeEntryId, CancellationToken token = default) => _Inner.TryAssociateAsync(followUpId, mergeEntryId, token);
            public Task<JudgeFollowUp> CompleteAuditAsync(string id, string verdict, string notes, string? recommendedAction, DateTime completedUtc, CancellationToken token = default) =>
                _Inner.CompleteAuditAsync(id, verdict, notes, recommendedAction, completedUtc, token);
            public Task<List<JudgeFollowUp>> EnumeratePendingAsync(string? vesselId = null, CancellationToken token = default) => _Inner.EnumeratePendingAsync(vesselId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedAsync(string? vesselId = null, CancellationToken token = default) => _Inner.EnumerateUnassociatedAsync(vesselId, token);
            public Task<List<JudgeFollowUp>> EnumerateCreatedSinceAsync(DateTime sinceUtc, string? vesselId = null, CancellationToken token = default) => _Inner.EnumerateCreatedSinceAsync(sinceUtc, vesselId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedByReviewedMissionAsync(string reviewedMissionId, CancellationToken token = default) => _Inner.EnumerateUnassociatedByReviewedMissionAsync(reviewedMissionId, token);
            public Task<List<JudgeFollowUp>> EnumerateUnassociatedByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default) => _Inner.EnumerateUnassociatedByJudgeMissionAsync(judgeMissionId, token);
        }
    }
}
