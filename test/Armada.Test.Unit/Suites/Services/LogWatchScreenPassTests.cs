namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the D8 <c>log_watch</c> screening pass and its adapter. They prove the skeleton (Off
    /// makes no call, unavailable and below-threshold report nothing and record their own event, a
    /// gated drift reports exactly one finding and one course-flag event), that the pass writes
    /// nothing else and never throws into the host, that the tail is redacted before it egresses while
    /// the structured markers survive, and that an authorized authentication log reads as on track.
    /// </summary>
    public sealed class LogWatchScreenPassTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Log Watch Screen Pass (D8)";

        private const string _Decision = "log_watch";

        // The key-shaped fixture is composed at run time rather than written as a literal: the shape
        // the redactor strips must not itself appear as a committed token.
        private static readonly string _KeyShapedToken = "sk" + "-" + "abcdefghijklmnopqrstuvwxyz0123";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static TypedLogWatchAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedLogWatchAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule())
            {
                EventDatabase = db.Driver
            };
        }

        private static LogScreenContext Context(params string[] lines)
        {
            return new LogScreenContext
            {
                MissionId = "msn_screened",
                VoyageId = "vyg_screened",
                CaptainId = "cpt_screened",
                Tail = String.Join("\n", lines)
            };
        }

        private static TypedDecisionResult Answer(string driftClass, double confidence, double correctableNow = 1.0)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    [TypedLogWatchAdapter.QuestionOffCourse] = new TypedAnswer { Type = "choice", Choice = driftClass, Confidence = confidence },
                    [TypedLogWatchAdapter.QuestionCorrectableNow] = new TypedAnswer { Type = "noul", Noul = correctableNow }
                }
            };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            List<ArmadaEvent> events = await db.Driver.Events.EnumerateByTypeAsync(eventType, 100).ConfigureAwait(false);
            return events.Count;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_MakesNoDecisionCallAndReportsNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Working the decoder against the stated base."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "an Off decision reports nothing");
                AssertEqual(0, client.CallCount, "an Off decision never calls the client");
                AssertEqual(0, await CountEventsAsync(db, TypedLogWatchAdapter.CourseFlagEventType).ConfigureAwait(false), "no course flag");
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReportsNothingAndRecordsUnavailableEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Working the decoder."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "an unavailable provider reports nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false), "one unavailable event");
                AssertEqual(0, await CountEventsAsync(db, TypedLogWatchAdapter.CourseFlagEventType).ConfigureAwait(false), "no course flag");
            }).ConfigureAwait(false);

            await RunTest("ClientThrows_ReportsNothingAndNeverThrowsIntoTheHost", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("provider fault"));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Working the decoder."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "a client fault reports nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false), "one unavailable event");
            }).ConfigureAwait(false);

            await RunTest("BelowThreshold_ReportsNothingAndRecordsShadowEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongBase, 0.60));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Rebased onto the tip and continued."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "below threshold reports nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false), "one shadow event");
                AssertEqual(0, await CountEventsAsync(db, TypedLogWatchAdapter.CourseFlagEventType).ConfigureAwait(false), "no course flag below threshold");
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_AtHighConfidence_ReportsNothingAndRecordsShadowEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Assuming the parser already handles the frame."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "shadow mode reports nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false), "one shadow event");
                AssertEqual(0, await CountEventsAsync(db, TypedLogWatchAdapter.CourseFlagEventType).ConfigureAwait(false), "no course flag in shadow mode");
            }).ConfigureAwait(false);

            await RunTest("GateOnTrack_AtHighConfidence_ReportsNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassOnTrack, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Read the handler and the two call sites, then wrote the failing test."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "an on-track reading is never a finding");
                AssertEqual(0, await CountEventsAsync(db, TypedLogWatchAdapter.CourseFlagEventType).ConfigureAwait(false), "no course flag when on track");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false),
                    "on track proposes no action, so it records as below threshold rather than gating");
            }).ConfigureAwait(false);

            await RunTest("GateDriftAtThreshold_ReportsOneFindingNamingTheDriftClassWithOneEvidenceLine", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.93));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(Context(
                    "The brief says the frame decoder is absent.",
                    "Extending the existing frame decoder instead of writing one."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, findings.Count, "exactly one finding");
                AssertEqual(TypedLogWatchAdapter.ClassWrongPremise, findings[0].RuleClass, "the finding names the drift class");
                AssertEqual(LogScreenFinding.SourceModel, findings[0].Source, "the finding is a model finding");
                AssertEqual("Extending the existing frame decoder instead of writing one.", findings[0].EvidenceLine,
                    "the evidence is the last content line of the tail");
                AssertFalse(findings[0].EvidenceLine.Contains('\n'), "one line of evidence");
                AssertTrue(findings[0].Confidence.HasValue && findings[0].Confidence.Value >= 0.90, "the finding carries the gated confidence");
            }).ConfigureAwait(false);

            await RunTest("GateDriftAtThreshold_EmitsOneCourseFlagEventCarryingMissionVoyageAndClass", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassMisreadStage, 0.97));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                await pass.EvaluateAsync(Context("Writing the release notes for the whole voyage."), CancellationToken.None).ConfigureAwait(false);

                List<ArmadaEvent> flags = await db.Driver.Events
                    .EnumerateByTypeAsync(TypedLogWatchAdapter.CourseFlagEventType, 100).ConfigureAwait(false);
                AssertEqual(1, flags.Count, "exactly one course flag");
                AssertEqual("msn_screened", flags[0].MissionId, "the flag carries the mission");
                AssertEqual("vyg_screened", flags[0].VoyageId, "the flag carries the voyage");
                AssertTrue(flags[0].Payload != null && flags[0].Payload!.Contains(TypedLogWatchAdapter.ClassMisreadStage, StringComparison.Ordinal),
                    "the flag payload names the drift class");

                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false), "one gated bookkeeping event");
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false), "no shadow event when gated");
            }).ConfigureAwait(false);

            await RunTest("CourseFlagEventType_IsDistinctFromTheTypedDecisionBookkeepingEvents", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongBase, 0.96));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                await pass.EvaluateAsync(Context("Branching from the release tag instead of the given base."), CancellationToken.None).ConfigureAwait(false);

                List<ArmadaEvent> flags = await db.Driver.Events
                    .EnumerateByTypeAsync(TypedLogWatchAdapter.CourseFlagEventType, 100).ConfigureAwait(false);
                AssertEqual(1, flags.Count, "an operator query for course flags returns exactly the flags");
                foreach (ArmadaEvent flag in flags)
                {
                    AssertFalse(String.Equals(flag.EventType, TypedDecisionRecorder.EventTypeGated, StringComparison.Ordinal), "not the gated event");
                    AssertFalse(String.Equals(flag.EventType, TypedDecisionRecorder.EventTypeShadow, StringComparison.Ordinal), "not the shadow event");
                    AssertFalse(String.Equals(flag.EventType, TypedDecisionRecorder.EventTypeUnavailable, StringComparison.Ordinal), "not the unavailable event");
                }
            }).ConfigureAwait(false);

            await RunTest("UnknownDriftClass_IsReadAsOnTrackAndReportsNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer("wandering_off", 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(
                    Context("Working the decoder."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "a class outside the catalogue never reaches the board");
            }).ConfigureAwait(false);

            await RunTest("EmptyTail_MakesNoDecisionCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                IReadOnlyList<LogScreenFinding> findings = await pass.EvaluateAsync(Context("   "), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, findings.Count, "an empty tail reports nothing");
                AssertEqual(0, client.CallCount, "an empty tail costs no model call");
            }).ConfigureAwait(false);

            await RunTest("OneEvaluation_MakesExactlyOneDecisionCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                await pass.EvaluateAsync(Context("Working the decoder."), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.CallCount, "one tail costs exactly one model call");
            }).ConfigureAwait(false);

            await RunTest("TransmittedTail_IsRedactedWhileTheStructuredMarkersSurvive", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassOnTrack, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                await pass.EvaluateAsync(Context(
                    "[ARMADA:NOTE] the ProtocolDecoder port is under way",
                    "Rebased mission msn_other42 onto 1a2b3c4d5e6f7a8b",
                    "Wrote /Volumes/Work/repo/src/ProtocolDecoder.cs on build.internal.example.com",
                    "Used token " + _KeyShapedToken,
                    "[ARMADA:RESULT] IN_PROGRESS"), CancellationToken.None).ConfigureAwait(false);

                string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                AssertContains("[ARMADA:NOTE]", state, "the note marker survives redaction");
                AssertContains("[ARMADA:RESULT]", state, "the result marker survives redaction");
                AssertContains("ProtocolDecoder", state, "the product identifier survives redaction");
                AssertFalse(state.Contains("msn_other42", StringComparison.Ordinal), "an Armada id is redacted");
                AssertFalse(state.Contains("1a2b3c4d5e6f7a8b", StringComparison.Ordinal), "a commit hash is redacted");
                AssertFalse(state.Contains("/Volumes/Work", StringComparison.Ordinal), "an absolute path is redacted");
                AssertFalse(state.Contains("build.internal.example.com", StringComparison.Ordinal), "a host is redacted");
                AssertFalse(state.Contains(_KeyShapedToken, StringComparison.Ordinal), "a key-shaped token is redacted");
            }).ConfigureAwait(false);

            await RunTest("RecordedState_IsAHashAndAByteCountNeverTheTail", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassWrongPremise, 0.99));
                LogWatchScreenPass pass = new LogWatchScreenPass(BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate)));

                await pass.EvaluateAsync(Context("A distinctive sentence the event must never carry."), CancellationToken.None).ConfigureAwait(false);

                List<ArmadaEvent> gated = await db.Driver.Events
                    .EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 100).ConfigureAwait(false);
                AssertEqual(1, gated.Count, "one gated event");
                AssertFalse(gated[0].Payload!.Contains("A distinctive sentence", StringComparison.Ordinal), "the tail is never on the event");
                AssertContains("state_sha256", gated[0].Payload!, "the state is recorded as a hash");
                AssertContains("state_bytes", gated[0].Payload!, "the state is recorded with a byte count");

                List<ArmadaEvent> flags = await db.Driver.Events
                    .EnumerateByTypeAsync(TypedLogWatchAdapter.CourseFlagEventType, 100).ConfigureAwait(false);
                AssertEqual(1, flags.Count, "one course flag");
                AssertFalse(flags[0].Payload!.Contains("A distinctive sentence", StringComparison.Ordinal), "the tail is never on the flag either");
            }).ConfigureAwait(false);

            await RunTest("AuthorizedAuthenticationWork_IsDeclaredOrdinaryEngineeringInTheQuestions", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(TypedLogWatchAdapter.ClassOnTrack, 0.99));
                TypedLogWatchAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                // A running log full of authorized authentication work. A reader not told the domain
                // reads this material as a policy problem and answers blocked_unstated or
                // wrong_premise, so the instructions that reach the provider must state the domain.
                LogWatchDecisionInput input = new LogWatchDecisionInput
                {
                    MissionId = "msn_authwork",
                    LogTail = String.Join("\n", new string[]
                    {
                        "Implemented the challenge-response authentication exchange for the owned device.",
                        "Captured the handshake between the authorized client and the device on the test bench.",
                        "Read the access-control routine in our own assembly to match the published table.",
                        "[ARMADA:NOTE] the authentication step now answers the challenge correctly."
                    })
                };

                TypedDecisionBatchItem? request = adapter.DescribeRequest(input);
                AssertNotNull(request, "the decision builds a request for this tail");

                string instructions = String.Join("\n", request!.Questions.Values.Select(q => q.Instructions));
                AssertContains("authorized engineering", instructions, "the instructions state the work is authorized");
                AssertContains("access-control", instructions, "the instructions name access-control work");
                AssertContains("ORDINARY ENGINEERING", instructions, "the instructions call that work ordinary engineering");
                AssertContains("on_track", instructions, "the instructions say that material is no reason to answer anything but on track");

                LogWatchVerdict verdict = await adapter
                    .DecideAsync(input, LogWatchVerdict.OnTrack(), CancellationToken.None).ConfigureAwait(false);
                AssertFalse(verdict.IsDrift, "an on-track answer over authorized authentication work flags nothing");
            }).ConfigureAwait(false);
        }
    }
}
