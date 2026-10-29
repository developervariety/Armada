namespace Armada.Test.Unit
{
    using System.Text.Json;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for <see cref="McpSignalTools"/>: armada_nudge_voyage validation and creation, and the
    /// armada_mark_signal_read ownership rule for addressed and unaddressed Wake signals.
    /// </summary>
    public sealed class McpSignalToolsTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "McpSignalTools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MarkSignalRead_AnonymousCaller_AcknowledgesAnyWake", async () =>
            {
                Signal broadcast = new Signal(SignalTypeEnum.Wake, "[vsl=vsl_example] WorkProduced: mission msn_example");
                Signal addressed = new Signal(SignalTypeEnum.Wake, "[to=someone-else] note");
                Signal mail = new Signal(SignalTypeEnum.Mail, "{}");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal(null, broadcast, "example-lead"), "anonymous caller may acknowledge a broadcast Wake");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("", addressed, null), "anonymous caller may acknowledge an addressed Wake");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("  ", mail, null), "anonymous caller is unrestricted, unchanged");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("MarkSignalRead_AddressedWake_OwnerOnly", async () =>
            {
                Signal addressed = new Signal(SignalTypeEnum.Wake, "[to=helper-1] assignment");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("helper-1", addressed, "example-lead"), "the addressee acknowledges its own Wake");
                string? other = McpSignalTools.WakeAcknowledgementRefusal("example-lead", addressed, "example-lead");
                AssertNotNull(other, "the effective AgentWake owner must NOT acknowledge a Wake addressed to another key");
                AssertContains("only its own Wake", other!);
                string? prefixTrick = McpSignalTools.WakeAcknowledgementRefusal("helper", addressed, "helper");
                AssertNotNull(prefixTrick, "a key that is a prefix of the addressee must not match");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("MarkSignalRead_UnaddressedWake_EffectiveAgentWakeOwnerAcknowledges", async () =>
            {
                Signal outcome = new Signal(SignalTypeEnum.Wake, "[vsl=vsl_example] WorkProduced: mission msn_example");
                Signal critical = new Signal(SignalTypeEnum.Wake, "[CRITICAL] [vsl=vsl_example] mission failed");
                Signal bare = new Signal(SignalTypeEnum.Wake, "plain wake text");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", outcome, "example-lead"), "the effective owner acknowledges a mission-outcome Wake");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", critical, "example-lead"), "the effective owner acknowledges a critical Wake");
                AssertNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", bare, "example-lead"), "the effective owner acknowledges an unprefixed Wake");

                string? notOwner = McpSignalTools.WakeAcknowledgementRefusal("example-operator", outcome, "example-lead");
                AssertNotNull(notOwner, "a participant that is not the effective owner is refused");
                AssertContains("example-lead", notOwner!);

                string? noOwner = McpSignalTools.WakeAcknowledgementRefusal("example-lead", outcome, null);
                AssertNotNull(noOwner, "with no registered AgentWake owner, an authenticated caller is refused");
                AssertContains("none registered", noOwner!);
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("MarkSignalRead_AuthenticatedCaller_CannotAcknowledgeNonWake", async () =>
            {
                Signal mail = new Signal(SignalTypeEnum.Mail, "{\"voyageId\":\"vyg_example\"}");
                Signal nudge = new Signal(SignalTypeEnum.Nudge, "{}");
                Signal empty = new Signal(SignalTypeEnum.Wake, "");
                AssertNotNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", mail, "example-lead"), "Mail is consumed by the handoff, never acknowledged by a participant");
                AssertNotNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", nudge, "example-lead"), "Nudge likewise");
                AssertNotNull(McpSignalTools.WakeAcknowledgementRefusal("example-lead", empty, "example-lead"), "an empty payload cannot be classified and is refused");
                await Task.CompletedTask.ConfigureAwait(false);
            });

            await RunTest("MarkSignalRead_Handler_AnonymousAcknowledgesBroadcastWake_Marked", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Signal wake = new Signal(SignalTypeEnum.Wake, "[vsl=vsl_example] WorkProduced: mission msn_example");
                    wake.TenantId = ArmadaConstants.DefaultTenantId;
                    wake = await testDb.Driver.Signals.CreateAsync(wake).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_mark_signal_read") handler = h; },
                        testDb.Driver,
                        () => "example-lead");
                    AssertNotNull(handler, "armada_mark_signal_read handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { signalId = wake.Id }, _JsonOpts);
                    string first = JsonSerializer.Serialize(await handler!(args).ConfigureAwait(false));
                    AssertContains("\"Status\":\"marked\"", first);
                    string second = JsonSerializer.Serialize(await handler!(args).ConfigureAwait(false));
                    AssertContains("\"Status\":\"already_read\"", second);
                }
            });

            await RunTest("NudgeVoyage_MissingBothTargets_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);
                    AssertNotNull(handler, "armada_nudge_voyage handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { type = "Nudge", message = "hello" }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Exactly one of voyageId or missionId is required", resultJson);
                }
            });

            await RunTest("NudgeVoyage_BothTargetsProvided_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = "vyg_123",
                        missionId = "msn_456",
                        type = "Mail",
                        message = "both"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("both were provided", resultJson);
                }
            });

            await RunTest("NudgeVoyage_InvalidType_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("type-test-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Heartbeat",
                        message = "wrong type"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("type must be Nudge or Mail", resultJson);
                }
            });

            await RunTest("NudgeVoyage_EmptyMessage_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("empty-msg-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Nudge",
                        message = ""
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("message is required", resultJson);
                }
            });

            await RunTest("NudgeVoyage_VoyageNotFound_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = "vyg_doesnotexist",
                        type = "Nudge",
                        message = "test"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Voyage not found", resultJson);
                }
            });

            await RunTest("NudgeVoyage_MissionNotFound_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        missionId = "msn_doesnotexist",
                        type = "Mail",
                        message = "test"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Mission not found", resultJson);
                }
            });

            await RunTest("NudgeVoyage_WithVoyageId_CreatesNudgeSignal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("nudge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "Nudge",
                        message = "please focus on performance",
                        createdBy = "user_42"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("sig_", resultJson, "Result should contain a signal ID");

                    // Verify payload is stored correctly
                    Signal? readBack = await testDb.Driver.Signals.ReadAsync(
                        JsonSerializer.Deserialize<Signal>(resultJson)!.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual("Nudge", readBack!.Type.ToString());
                    AssertFalse(readBack.Read, "New signal should not be marked read");
                    AssertNotNull(readBack.Payload);
                    AssertContains("\"voyageId\"", readBack.Payload!, "Payload should include voyageId");
                    AssertContains(voyage.Id, readBack.Payload!);
                    AssertContains("please focus on performance", readBack.Payload);
                }
            });

            await RunTest("NudgeVoyage_WithMissionId_CreatesMailSignalAndIncludesVoyageId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("mail-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("mail-mission", "desc");
                    mission.VoyageId = voyage.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        missionId = mission.Id,
                        type = "Mail",
                        message = "check the tests please"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("sig_", resultJson);

                    Signal? readBack = await testDb.Driver.Signals.ReadAsync(
                        JsonSerializer.Deserialize<Signal>(resultJson)!.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual("Mail", readBack!.Type.ToString());
                    AssertNotNull(readBack.Payload);
                    AssertContains(mission.Id, readBack.Payload!);
                    AssertContains(voyage.Id, readBack.Payload!, "Payload should include voyageId resolved from mission");
                    AssertContains("check the tests please", readBack.Payload);
                }
            });

            await RunTest("SendSignal_ToMission_GoesToTheMissionsCurrentCaptainNotAStaleOne", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain stale = await testDb.Driver.Captains.CreateAsync(new Captain("stale-captain")).ConfigureAwait(false);
                    Captain current = await testDb.Driver.Captains.CreateAsync(new Captain("current-captain")).ConfigureAwait(false);
                    Mission mission = new Mission("Moved slice", "Implements the slice.");
                    mission.CaptainId = current.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_send_signal") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new { captainId = stale.Id, missionId = mission.Id, message = "fix list" }, _JsonOpts);
                    Signal sent = (Signal)await handler!(args).ConfigureAwait(false);

                    Signal? stored = await testDb.Driver.Signals.ReadAsync(sent.Id).ConfigureAwait(false);
                    AssertEqual(current.Id, stored!.ToCaptainId ?? "(null)", "Mail to a mission reaches the captain that holds the mission");
                    AssertEqual(mission.Id, stored.MissionId ?? "(null)", "The signal records the mission it is addressed to");
                }
            });

            await RunTest("SendSignal_ToMissionWithoutCaptain_IsStoredForItsNextCaptain_UnknownMissionIsRefused", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission waiting = await testDb.Driver.Missions.CreateAsync(new Mission("Waiting slice", "Implements the slice.")).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_send_signal") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    Signal sent = (Signal)await handler!(JsonSerializer.SerializeToElement(new { missionId = waiting.Id, message = "read this first" }, _JsonOpts)).ConfigureAwait(false);
                    Signal? stored = await testDb.Driver.Signals.ReadAsync(sent.Id).ConfigureAwait(false);
                    AssertNull(stored!.ToCaptainId, "A mission with no captain leaves the mail without a recipient");
                    AssertEqual(waiting.Id, stored.MissionId ?? "(null)", "The mail waits on the mission");

                    object refused = await handler(JsonSerializer.SerializeToElement(new { missionId = "msn_missing", message = "lost" }, _JsonOpts)).ConfigureAwait(false);
                    AssertContains("mission_not_found", JsonSerializer.Serialize(refused));
                }
            });

            await RunTest("NudgeVoyage_LowercaseType_AcceptedCaseInsensitively", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage("case-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_nudge_voyage") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        type = "mail",
                        message = "lowercase type should work"
                    }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("sig_", resultJson, "Lowercase type token must be accepted");

                    Signal? readBack = await testDb.Driver.Signals.ReadAsync(
                        JsonSerializer.Deserialize<Signal>(resultJson)!.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual("Mail", readBack!.Type.ToString(), "Lowercase 'mail' should map to the Mail signal type");
                    AssertContains("lowercase type should work", readBack.Payload!);
                }
            });

            await RunTest("SendSignal_RunningCaptainWithoutMidMissionMail_ReturnsStoredUntilNextLaunch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain running = await testDb.Driver.Captains.CreateAsync(
                        new Captain("running-cursor", AgentRuntimeEnum.Cursor) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                    Captain idle = await testDb.Driver.Captains.CreateAsync(
                        new Captain("idle-cursor", AgentRuntimeEnum.Cursor) { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpSignalTools.Register(
                        (name, _, _, h) => { if (name == "armada_send_signal") handler = McpTestCaller.Wrap(h); },
                        testDb.Driver);
                    AssertNotNull(handler, "armada_send_signal handler must be registered");

                    JsonElement runningArgs = JsonSerializer.SerializeToElement(new
                    {
                        captainId = running.Id,
                        message = "check the stored mail path"
                    }, _JsonOpts);
                    object runningResult = await handler!(runningArgs).ConfigureAwait(false);
                    string runningJson = JsonSerializer.Serialize(runningResult);
                    Signal? sent = JsonSerializer.Deserialize<Signal>(runningJson);
                    AssertNotNull(sent);
                    AssertEqual(SignalDelivery.StoredUntilNextLaunch, sent!.Delivery, runningJson);
                    AssertContains("Cursor", sent.DeliveryReason!, runningJson);
                    AssertContains("stored until the next launch", sent.DeliveryReason!, runningJson);

                    Signal? stored = await testDb.Driver.Signals.ReadAsync(sent.Id).ConfigureAwait(false);
                    AssertNotNull(stored);
                    AssertNull(stored!.Delivery, "Delivery is a send-response notice and is not stored");
                    AssertNull(stored.DeliveryReason);

                    JsonElement idleArgs = JsonSerializer.SerializeToElement(new
                    {
                        captainId = idle.Id,
                        message = "idle captain has no live process"
                    }, _JsonOpts);
                    object idleResult = await handler!(idleArgs).ConfigureAwait(false);
                    Signal? idleSent = JsonSerializer.Deserialize<Signal>(JsonSerializer.Serialize(idleResult));
                    AssertNotNull(idleSent);
                    AssertNull(idleSent!.Delivery, "an idle captain is not running, so the send has no mid-mission notice");
                    AssertNull(idleSent.DeliveryReason);
                }
            });

            await RunTest("SendSignal_RunningCaptainInAnotherTenant_ReturnsNoDeliveryNotice", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_other", Name = "ten_other" }).ConfigureAwait(false);
                    Captain foreign = await testDb.Driver.Captains.CreateAsync(
                        new Captain("foreign-cursor", AgentRuntimeEnum.Cursor) { State = CaptainStateEnum.Working, TenantId = "ten_other" }).ConfigureAwait(false);
                    Signal sent = new Signal(SignalTypeEnum.Mail, "cross-tenant probe") { TenantId = "ten_sender", ToCaptainId = foreign.Id };

                    Signal annotated = await SignalDelivery.AnnotateAsync(testDb.Driver, sent).ConfigureAwait(false);

                    AssertNull(annotated.Delivery, "a captain in another tenant reveals no running state");
                    AssertNull(annotated.DeliveryReason);
                }
            });
        }
    }
}
