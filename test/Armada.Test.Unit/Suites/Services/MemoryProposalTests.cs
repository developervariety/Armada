namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Behavioural tests for the database-backed memory proposal store and its writers: the SQLite
    /// migration and round trip, the D18 writer that stores proposals without touching the AI-Memory
    /// folder, the weekly papercut sweep, the D23 seam B Recorder review that only lowers salience and
    /// links duplicates, and the operator-only MCP tools.
    /// </summary>
    public class MemoryProposalTests : TestSuite
    {
        public override string Name => "Memory Proposals (D18 store, D23 seam B)";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Store_SqliteMigrationCreatesTableAtVersion104", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 104;";
                        long applied = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
                        AssertEqual(1L, applied, "migration 104 is recorded as applied");
                    }

                    HashSet<string> columns = new HashSet<string>(StringComparer.Ordinal);
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT name FROM pragma_table_info('memory_proposals');";
                        using (SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync().ConfigureAwait(false)) columns.Add(reader.GetString(0));
                        }
                    }

                    foreach (string column in new[] { "id", "tenant_id", "user_id", "source", "source_key", "title", "body", "target_hint", "confidence", "related_record_ids_json", "state", "dismissed_by", "dismissed_reason", "dismissed_utc", "created_utc", "last_update_utc" })
                        AssertTrue(columns.Contains(column), "memory_proposals has column " + column);
                }

                AssertEqual(0, testDb.Driver.FindUnwiredMethodSets().Count, "the SQLite driver wires every method set, including memory proposals");
            });

            await RunTest("Store_CreateListDismiss_RoundTrips", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                DateTime created = new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc);

                MemoryProposal first = await db.MemoryProposals.CreateAsync(new MemoryProposal
                {
                    TenantId = Constants.DefaultTenantId,
                    Source = MemoryProposal.SourcePapercutSweep,
                    SourceKey = "key-first",
                    Title = "Brace variables before a colon",
                    Body = "zsh reads a colon after a bare variable as a modifier.",
                    TargetHint = "shared",
                    Confidence = 0.93,
                    RelatedRecordIds = new List<string> { "msn_one", "msn_two" },
                    CreatedUtc = created,
                    LastUpdateUtc = created
                }).ConfigureAwait(false);
                MemoryProposal second = await db.MemoryProposals.CreateAsync(new MemoryProposal
                {
                    TenantId = Constants.DefaultTenantId,
                    Source = MemoryProposal.SourceRecorderSeam,
                    SourceKey = "key-second",
                    Title = "A later lesson",
                    TargetHint = "repos/<vessel>",
                    Confidence = 0.97,
                    CreatedUtc = created.AddMinutes(5),
                    LastUpdateUtc = created.AddMinutes(5)
                }).ConfigureAwait(false);

                AssertStartsWith(Constants.MemoryProposalIdPrefix, first.Id, "the id carries the mpr_ prefix");

                MemoryProposal? read = await db.MemoryProposals.ReadAsync(first.Id).ConfigureAwait(false);
                AssertNotNull(read, "the proposal reads back");
                AssertEqual("papercut_sweep", read!.Source);
                AssertEqual("Brace variables before a colon", read.Title);
                AssertEqual("shared", read.TargetHint);
                AssertTrue(Math.Abs(read.Confidence - 0.93) < 0.0001, "confidence round-trips");
                AssertEqual(2, read.RelatedRecordIds.Count, "related record ids round-trip");
                AssertEqual("msn_two", read.RelatedRecordIds[1]);
                AssertEqual(MemoryProposalStateEnum.Open, read.State);
                AssertEqual(created, read.CreatedUtc, "created time round-trips");

                MemoryProposal? byKey = await db.MemoryProposals.ReadBySourceKeyAsync("key-second").ConfigureAwait(false);
                AssertEqual(second.Id, byKey?.Id, "the proposal reads back by its subject fingerprint");

                List<MemoryProposal> open = await db.MemoryProposals.EnumerateAsync(null, MemoryProposalStateEnum.Open, 10).ConfigureAwait(false);
                AssertEqual(2, open.Count, "both proposals are open");
                AssertEqual(second.Id, open[0].Id, "newest first");

                DateTime dismissedAt = created.AddHours(1);
                AssertTrue(await db.MemoryProposals.DismissAsync(first.Id, "operator-a", "promoted to shared memory", dismissedAt).ConfigureAwait(false), "an open proposal is dismissed");
                AssertFalse(await db.MemoryProposals.DismissAsync(first.Id, "operator-b", "again", dismissedAt.AddHours(1)).ConfigureAwait(false), "a second dismissal changes nothing");

                MemoryProposal? dismissed = await db.MemoryProposals.ReadAsync(first.Id).ConfigureAwait(false);
                AssertEqual(MemoryProposalStateEnum.Dismissed, dismissed!.State);
                AssertEqual("operator-a", dismissed.DismissedBy, "the first dismissal is kept");
                AssertEqual("promoted to shared memory", dismissed.DismissedReason);
                AssertEqual(dismissedAt, dismissed.DismissedUtc);
                AssertEqual("Brace variables before a colon", dismissed.Title, "dismissal does not change the text");

                AssertEqual(1, (await db.MemoryProposals.EnumerateAsync(null, MemoryProposalStateEnum.Open, 10).ConfigureAwait(false)).Count, "one proposal stays open");
                AssertEqual(1, (await db.MemoryProposals.EnumerateAsync(null, MemoryProposalStateEnum.Dismissed, 10).ConfigureAwait(false)).Count, "one proposal is dismissed");
                AssertEqual(2, (await db.MemoryProposals.EnumerateAsync(Constants.DefaultTenantId, null, 10).ConfigureAwait(false)).Count, "no state filter lists both");
            });

            await RunTest("Writer_StoresProposalInDatabase_ReadOnlyAiMemoryFolderUnchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string root = Path.Combine(Path.GetTempPath(), "aimem_ro_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "shared"));
                Directory.CreateDirectory(Path.Combine(root, "corpus"));
                File.WriteAllText(Path.Combine(root, "shared", "rule.md"), "# A rule\n");
                File.WriteAllText(Path.Combine(root, "corpus", "decisions.jsonl"), "{}\n");
                string before = SnapshotTree(root);
                MakeReadOnly(root);
                try
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.97, "scope", "shared"));
                    MemoryCandidateAdapter adapter = BuildCandidateAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate);

                    List<MemoryCandidateProposal> nominated = await adapter.NominateAsync(
                        new List<PapercutGroup> { Group("k1", "mission msn_abc123 hit a stale sibling", 5) },
                        CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(1, nominated.Count, "the gated candidate is nominated");
                    AssertNotNull(nominated[0].ProposalId, "the nomination carries the stored proposal id");

                    List<MemoryProposal> stored = await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false);
                    AssertEqual(1, stored.Count, "exactly one proposal row is stored");
                    AssertEqual(nominated[0].ProposalId, stored[0].Id);
                    AssertEqual(MemoryProposal.SourcePapercutSweep, stored[0].Source);
                    AssertEqual("shared", stored[0].TargetHint);
                    AssertFalse(stored[0].Title.Contains("msn_abc123", StringComparison.Ordinal), "the stored title is redacted");
                    AssertFalse(stored[0].SourceKey.Contains("k1", StringComparison.Ordinal), "only a fingerprint of the subject key is stored");

                    AssertEqual(before, SnapshotTree(root), "the read-only AI-Memory folder is byte-for-byte unchanged");
                }
                finally
                {
                    MakeWritable(root);
                    Directory.Delete(root, true);
                }
            });

            await RunTest("Writer_SameSubjectIsProposedOnce_DismissedIsNotRevived", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseMemoryCandidateProposalWriter writer = new DatabaseMemoryCandidateProposalWriter(testDb.Driver, Quiet());
                MemoryCandidateProposal candidate = new MemoryCandidateProposal { GroupKey = "vsl_example|ToolFailure|x", Title = "x", Scope = "shared", DurableLesson = 0.95 };

                string? firstId = await writer.WriteAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                string? secondId = await writer.WriteAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                AssertNotNull(firstId);
                AssertEqual(firstId, secondId, "a recurring subject returns the existing proposal");

                await testDb.Driver.MemoryProposals.DismissAsync(firstId!, "operator", "not durable", DateTime.UtcNow).ConfigureAwait(false);
                string? thirdId = await writer.WriteAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(firstId, thirdId, "a dismissed subject is not proposed again");

                List<MemoryProposal> all = await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false);
                AssertEqual(1, all.Count, "one row for the subject");
                AssertEqual(MemoryProposalStateEnum.Dismissed, all[0].State, "the dismissal stands");
            });

            await RunTest("Sweep_DecisionOff_CallsNothingAndStoresNothing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_a", "cpt_a", now.AddDays(-1)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_b", "cpt_b", now.AddDays(-2)).ConfigureAwait(false);

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.99, "scope", "shared"));
                TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                settings.Decisions[MemoryCandidateAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Off;
                PapercutMemorySweepRunner runner = BuildSweep(testDb.Driver, client, settings, () => now);

                PapercutMemorySweepResult result = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertEqual("dormant", result.Reason);
                AssertEqual(0, client.Calls, "the model is never called while the decision is Off");
                AssertEqual(0, (await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false)).Count, "no proposal is stored");
                AssertEqual(0, await CountTypedEventsAsync(testDb.Driver).ConfigureAwait(false), "no typed-decision event is recorded");
            });

            await RunTest("Sweep_Gate_NominatesRepeatedGroupOncePerWeek", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_a", "cpt_a", now.AddDays(-1)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_b", "cpt_b", now.AddDays(-2)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "a one-off report", "msn_c", "cpt_c", now.AddDays(-1)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "an old repeated report", "msn_d", "cpt_d", now.AddDays(-20)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "an old repeated report", "msn_e", "cpt_e", now.AddDays(-21)).ConfigureAwait(false);

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.99, "scope", "shared"));
                TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                settings.Decisions[MemoryCandidateAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Gate;
                DateTime clock = now;
                PapercutMemorySweepRunner runner = BuildSweep(testDb.Driver, client, settings, () => clock);

                PapercutMemorySweepResult first = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, first.Considered, "only the repeated group from the last seven days is offered");
                AssertEqual(1, first.Nominated);
                AssertEqual(1, client.Calls);

                List<MemoryProposal> stored = await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false);
                AssertEqual(1, stored.Count);
                AssertEqual(2, stored[0].RelatedRecordIds.Count, "the sample mission ids are kept as related records");

                PapercutMemorySweepResult repeat = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                AssertEqual("already_ran_this_week", repeat.Reason);
                AssertEqual(1, client.Calls, "a second pass in the same week calls nothing");

                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_f", "cpt_f", now.AddDays(2)).ConfigureAwait(false);
                await AddPapercutAsync(testDb.Driver, "stale sibling not found", "msn_g", "cpt_g", now.AddDays(3)).ConfigureAwait(false);
                clock = now.AddDays(8);
                PapercutMemorySweepResult nextWeek = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                AssertEqual("swept", nextWeek.Reason, "the sweep runs again after seven days");
                AssertEqual(1, (await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false)).Count, "the recurring group is not proposed twice");
            });

            await RunTest("RecorderReview_DecisionOff_CallsNothingAndChangesNothing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = RecorderMission();
                Memory existing = await CreateMemoryAsync(testDb.Driver, null, "Existing fact", "The build runs through dotnet run.", 0.8).ConfigureAwait(false);
                Memory written = await CreateMemoryAsync(testDb.Driver, mission.Id, "Same fact", "The build runs through dotnet run.", 0.9).ConfigureAwait(false);

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(_ => DuplicateStaleResult(0.99, 0.99, 0.99));
                TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                settings.Decisions[RecorderMemoryReviewAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Off;
                AssertTrue(Math.Abs(settings.Decisions[RecorderMemoryReviewAdapter.DecisionPoint].GateThreshold - 0.90) < 0.0001, "memory_review carries the default threshold");
                RecorderMemoryReviewAdapter adapter = BuildReview(testDb.Driver, client, settings);

                RecorderMemoryReviewResult result = await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("dormant", result.Reason);
                AssertEqual(0, client.Calls, "the model is never called while the decision is Off");
                Memory? after = await testDb.Driver.Memories.ReadAsync(written.Id).ConfigureAwait(false);
                AssertEqual(written.Version, after!.Version, "the record is not written");
                AssertTrue(Math.Abs(after.Salience - 0.9) < 0.0001, "salience is unchanged");
                AssertEqual(0, after.Tags.Count, "no link is added");
                AssertEqual(0, await CountTypedEventsAsync(testDb.Driver).ConfigureAwait(false), "no event is recorded");
                AssertNotNull(await testDb.Driver.Memories.ReadAsync(existing.Id).ConfigureAwait(false));
            });

            await RunTest("RecorderReview_DuplicateAndObsoleteAtHighConfidence_NeverDeletes_OnlySalienceAndLink", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = RecorderMission();
                Memory existing = await CreateMemoryAsync(testDb.Driver, null, "Existing fact", "The build runs through dotnet run.", 0.8).ConfigureAwait(false);
                Memory written = await CreateMemoryAsync(testDb.Driver, mission.Id, "Same fact", "The build runs through dotnet run, not dotnet test.", 0.9).ConfigureAwait(false);
                int countBefore = (await testDb.Driver.Memories.EnumerateAsync(Constants.DefaultTenantId).ConfigureAwait(false)).Count;

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(_ => DuplicateStaleResult(0.99, 0.99, 0.05));
                RecorderMemoryReviewAdapter adapter = BuildReview(testDb.Driver, client, GateSettings(RecorderMemoryReviewAdapter.DecisionPoint));

                RecorderMemoryReviewResult result = await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Calls, "one call for the one record the mission wrote");
                AssertEqual(1, result.SalienceLowered);
                AssertEqual(1, result.DuplicatesLinked);
                AssertEqual(0, result.Proposed);

                List<Memory> afterAll = await testDb.Driver.Memories.EnumerateAsync(Constants.DefaultTenantId).ConfigureAwait(false);
                AssertEqual(countBefore, afterAll.Count, "no record is deleted");

                Memory? after = await testDb.Driver.Memories.ReadAsync(written.Id).ConfigureAwait(false);
                AssertNotNull(after, "the duplicate record still exists");
                AssertEqual(written.Content, after!.Content, "content is not rewritten");
                AssertEqual(written.Summary, after.Summary, "summary is not rewritten");
                AssertEqual(written.Type, after.Type, "type is not changed");
                AssertEqual(written.Topic, after.Topic, "topic is not changed");
                AssertTrue(Math.Abs(after.Salience - RecorderMemoryReviewAdapter.DuplicateSalienceCeiling) < 0.0001, "salience is lowered to the duplicate ceiling");
                AssertTrue(after.Tags.Contains(RecorderMemoryReviewAdapter.DuplicateTagPrefix + existing.Id), "the duplicate is linked to the record it repeats");

                Memory? existingAfter = await testDb.Driver.Memories.ReadAsync(existing.Id).ConfigureAwait(false);
                AssertEqual(existing.Version, existingAfter!.Version, "the existing record is not written");
                AssertTrue(Math.Abs(existingAfter.Salience - 0.8) < 0.0001, "the existing record keeps its salience");

                List<ArmadaEvent> gated = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 10).ConfigureAwait(false);
                AssertEqual(1, gated.Count, "one gated event is recorded");
                AssertEqual(mission.Id, gated[0].MissionId, "the event is scoped to the Recorder mission");
            });

            await RunTest("RecorderReview_BelongsInAiMemory_StoresRecorderProposal", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = RecorderMission();
                Memory written = await CreateMemoryAsync(testDb.Driver, mission.Id, "Push only fast-forwards", "Never force-push; mission msn_zz9 proved it at /tmp/example-dock/work.", 0.7).ConfigureAwait(false);

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(_ => DuplicateStaleResult(0.0, 0.05, 0.96));
                RecorderMemoryReviewAdapter adapter = BuildReview(testDb.Driver, client, GateSettings(RecorderMemoryReviewAdapter.DecisionPoint));

                RecorderMemoryReviewResult result = await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, result.Proposed);
                AssertEqual(0, result.SalienceLowered, "a fleet rule is not demoted");
                List<MemoryProposal> stored = await testDb.Driver.MemoryProposals.EnumerateAsync(null, MemoryProposalStateEnum.Open, 10).ConfigureAwait(false);
                AssertEqual(1, stored.Count);
                AssertEqual(MemoryProposal.SourceRecorderSeam, stored[0].Source);
                AssertTrue(stored[0].RelatedRecordIds.Contains(written.Id), "the memory record is related");
                AssertTrue(stored[0].RelatedRecordIds.Contains(mission.Id), "the mission is related");
                AssertFalse(stored[0].Body.Contains("msn_zz9", StringComparison.Ordinal), "the body is redacted of ids");
                AssertFalse(stored[0].Body.Contains("/tmp/example-dock", StringComparison.Ordinal), "the body is redacted of paths");

                Memory? after = await testDb.Driver.Memories.ReadAsync(written.Id).ConfigureAwait(false);
                AssertEqual(written.Version, after!.Version, "proposing does not write the memory record");
            });

            await RunTest("RecorderReview_Unavailable_ChangesNothing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = RecorderMission();
                await CreateMemoryAsync(testDb.Driver, null, "Existing fact", "The build runs through dotnet run.", 0.8).ConfigureAwait(false);
                Memory written = await CreateMemoryAsync(testDb.Driver, mission.Id, "Same fact", "The build runs through dotnet run.", 0.9).ConfigureAwait(false);
                await CreateMemoryAsync(testDb.Driver, mission.Id, "Second fact", "Another record.", 0.9).ConfigureAwait(false);

                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                RecorderMemoryReviewAdapter adapter = BuildReview(testDb.Driver, client, GateSettings(RecorderMemoryReviewAdapter.DecisionPoint));

                RecorderMemoryReviewResult result = await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("unavailable", result.Reason);
                AssertEqual(1, client.Calls, "the pass stops at the first unavailable answer");
                Memory? after = await testDb.Driver.Memories.ReadAsync(written.Id).ConfigureAwait(false);
                AssertEqual(written.Version, after!.Version, "the record is not written");
                AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 10).ConfigureAwait(false)).Count, "one unavailable event");
                AssertEqual(0, (await testDb.Driver.MemoryProposals.EnumerateAsync(null, null, 10).ConfigureAwait(false)).Count, "no proposal");
            });

            await RunTest("Mcp_OperatorListsAndDismisses_CaptainIsRefused", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                MemoryProposal proposal = await testDb.Driver.MemoryProposals.CreateAsync(new MemoryProposal
                {
                    TenantId = Constants.DefaultTenantId,
                    SourceKey = "mcp-key",
                    Title = "A durable lesson",
                    TargetHint = "shared",
                    Confidence = 0.95
                }).ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpMemoryProposalTools.Register((name, _, _, handler) => handlers[name] = handler, testDb.Driver);
                AssertTrue(handlers.ContainsKey(McpMemoryProposalTools.ListToolName));
                AssertTrue(handlers.ContainsKey(McpMemoryProposalTools.DismissToolName));

                AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, "usr_captain_mission", false, false, "Test");
                foreach (string tool in new[] { McpMemoryProposalTools.ListToolName, McpMemoryProposalTools.DismissToolName })
                    AssertFalse(McpToolAccessPolicy.IsAllowed(captain, tool), tool + " is not in mission scope");

                string refused;
                using (McpCallerContext.Begin(captain))
                {
                    refused = JsonSerializer.Serialize(await handlers[McpMemoryProposalTools.DismissToolName](
                        JsonSerializer.SerializeToElement(new { id = proposal.Id, reason = "r", @operator = "o" })).ConfigureAwait(false));
                }
                AssertContains(McpMemoryProposalTools.GlobalAdministratorRequiredReason, refused, "a non-administrator is refused");
                AssertEqual(MemoryProposalStateEnum.Open, (await testDb.Driver.MemoryProposals.ReadAsync(proposal.Id).ConfigureAwait(false))!.State, "a refused dismissal changes nothing");

                Func<JsonElement?, Task<object>> list = McpTestCaller.Wrap(handlers[McpMemoryProposalTools.ListToolName]);
                Func<JsonElement?, Task<object>> dismiss = McpTestCaller.Wrap(handlers[McpMemoryProposalTools.DismissToolName]);

                string listed = JsonSerializer.Serialize(await list(JsonSerializer.SerializeToElement(new { state = "Open" })).ConfigureAwait(false));
                AssertContains(proposal.Id, listed, "the operator lists the open proposal");

                string missingReason = JsonSerializer.Serialize(await dismiss(JsonSerializer.SerializeToElement(new { id = proposal.Id, @operator = "operator-a" })).ConfigureAwait(false));
                AssertContains("reason is required", missingReason);

                string done = JsonSerializer.Serialize(await dismiss(JsonSerializer.SerializeToElement(new { id = proposal.Id, reason = "promoted by the owner", @operator = "operator-a" })).ConfigureAwait(false));
                AssertContains("\"Dismissed\":true", done, "the dismissal succeeds");

                MemoryProposal? after = await testDb.Driver.MemoryProposals.ReadAsync(proposal.Id).ConfigureAwait(false);
                AssertEqual(MemoryProposalStateEnum.Dismissed, after!.State);
                AssertEqual("operator-a", after.DismissedBy);
                AssertEqual("promoted by the owner", after.DismissedReason);

                string openAfter = JsonSerializer.Serialize(await list(null).ConfigureAwait(false));
                AssertFalse(openAfter.Contains(proposal.Id, StringComparison.Ordinal), "a dismissed proposal leaves the default open listing");
                string all = JsonSerializer.Serialize(await list(JsonSerializer.SerializeToElement(new { state = "All" })).ConfigureAwait(false));
                AssertContains(proposal.Id, all, "the All listing keeps the dismissed proposal");
            });
        }

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TypedDecisionSettings GateSettings(string decisionPoint)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[decisionPoint].Mode = TypedDecisionModeEnum.Gate;
            settings.Decisions[decisionPoint].GateThreshold = 0.90;
            return settings;
        }

        private static MemoryCandidateAdapter BuildCandidateAdapter(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionModeEnum mode)
        {
            TypedDecisionSettings settings = GateSettings(MemoryCandidateAdapter.DecisionPoint);
            settings.Decisions[MemoryCandidateAdapter.DecisionPoint].Mode = mode;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, Quiet());
            return new MemoryCandidateAdapter(settings, client, recorder, new DatabaseMemoryCandidateProposalWriter(database, Quiet()), Quiet());
        }

        private static PapercutMemorySweepRunner BuildSweep(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionSettings settings, Func<DateTime> clock)
        {
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, Quiet());
            MemoryCandidateAdapter adapter = new MemoryCandidateAdapter(settings, client, recorder, new DatabaseMemoryCandidateProposalWriter(database, Quiet()), Quiet());
            return new PapercutMemorySweepRunner(settings, adapter, null, database, clock, Quiet());
        }

        private static RecorderMemoryReviewAdapter BuildReview(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, Quiet());
            return new RecorderMemoryReviewAdapter(settings, client, recorder, new DatabaseMemoryCandidateProposalWriter(database, Quiet()), database, Quiet());
        }

        private static TypedDecisionResult DuplicateStaleResult(double duplicateConfidence, double stale, double aiMemory)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["type_ok"] = new TypedAnswer { Type = "noul", Noul = 0.95, Confidence = 0.95 },
                ["duplicate_of"] = new TypedAnswer { Type = "choice", Choice = duplicateConfidence > 0.0 ? "existing_1" : "none", Confidence = duplicateConfidence > 0.0 ? duplicateConfidence : 0.9 },
                ["will_go_stale"] = new TypedAnswer { Type = "noul", Noul = stale, Confidence = stale },
                ["belongs_in_ai_memory"] = new TypedAnswer { Type = "noul", Noul = aiMemory, Confidence = aiMemory }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static Mission RecorderMission()
        {
            Mission mission = new Mission("Record lessons [Recorder]");
            mission.TenantId = Constants.DefaultTenantId;
            mission.Persona = "Recorder";
            mission.VesselId = "vsl_example";
            return mission;
        }

        private static async Task<Memory> CreateMemoryAsync(DatabaseDriver database, string? sourceMissionId, string summary, string content, double salience)
        {
            Memory memory = new Memory
            {
                TenantId = Constants.DefaultTenantId,
                Type = MemoryTypeEnum.Semantic,
                Topic = "build",
                Summary = summary,
                Content = content,
                Salience = salience,
                SourceMissionId = sourceMissionId,
                SourceKind = sourceMissionId == null ? MemorySourceKindEnum.Manual : MemorySourceKindEnum.Mission,
                VesselId = "vsl_example"
            };
            return await database.Memories.CreateAsync(memory).ConfigureAwait(false);
        }

        private static PapercutGroup Group(string suffix, string title, int count)
        {
            return new PapercutGroup
            {
                Key = "vsl_example|BriefContradiction|" + suffix,
                VesselId = "vsl_example",
                Category = PapercutCategoryEnum.BriefContradiction,
                HighestSeverity = PapercutSeverityEnum.High,
                SampleTitle = title,
                SampleDetail = "detail " + suffix,
                Count = count,
                DistinctCaptainCount = count
            };
        }

        private static async Task AddPapercutAsync(DatabaseDriver database, string title, string missionId, string captainId, DateTime reportedUtc)
        {
            Papercut papercut = new Papercut
            {
                Category = PapercutCategoryEnum.RepoFriction,
                Severity = PapercutSeverityEnum.Medium,
                Title = title,
                Detail = "detail for " + title,
                VesselId = "vsl_example",
                MissionId = missionId,
                CaptainId = captainId,
                ReportedUtc = reportedUtc
            };
            ArmadaEvent evt = PapercutService.ToEvent(papercut);
            evt.TenantId = Constants.DefaultTenantId;
            await database.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        private static async Task<int> CountTypedEventsAsync(DatabaseDriver database)
        {
            int total = 0;
            foreach (string type in new[] { TypedDecisionRecorder.EventTypeGated, TypedDecisionRecorder.EventTypeShadow, TypedDecisionRecorder.EventTypeUnavailable })
                total += (await database.Events.EnumerateByTypeAsync(type, 50).ConfigureAwait(false)).Count;
            return total;
        }

        private static string SnapshotTree(string root)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
                sb.Append("D ").Append(Path.GetRelativePath(root, directory)).Append('\n');
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(file);
                sb.Append("F ").Append(Path.GetRelativePath(root, file)).Append(' ')
                  .Append(Convert.ToHexString(SHA256.HashData(bytes))).Append(' ')
                  .Append(File.GetLastWriteTimeUtc(file).Ticks).Append('\n');
            }
            return sb.ToString();
        }

        private static void MakeReadOnly(string root)
        {
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows())
            {
                foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Append(root))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }

        private static void MakeWritable(string root)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
