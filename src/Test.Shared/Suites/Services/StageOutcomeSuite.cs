namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for stage outcomes and failure-cause rules (migration 92, MODEL_HETEROGENEITY.md Part 4): the pure
    /// classifier (rule order, disabled rules, Unknown fallback, a Judge's own rejection, invalid and timed-out patterns,
    /// rule validation), the disposition decided from the mission before and after an exit, built-in rule seeding,
    /// recording an attempt with its provenance, manual classification, and re-applying rules to past outcomes.
    /// </summary>
    public sealed class StageOutcomeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.StageOutcomes";
        private static readonly DateTime _Fixed = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("classify_clean_completion_is_none", "A completed stage with no failure and a passed check is not classified", TestTags.Positive, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Completed);
                outcome.StageCheckStatus = StageCheckStatusEnum.Passed;
                FailureCauseClassification result = FailureCauseClassifier.Classify(outcome, FailureCauseRuleCatalog.Build());
                AssertEqual(FailureCauseEnum.None, result.Cause);
                AssertEqual(FailureCauseSourceEnum.None, result.Source);
                AssertNull(result.RuleId, "rule id");
            }));

            cases.Add(Case("classify_skipped_check_is_not_shortfall", "A completed stage whose check was skipped is not a shortfall", TestTags.Positive, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Completed);
                outcome.StageCheckStatus = StageCheckStatusEnum.Skipped;
                AssertFalse(FailureCauseClassifier.IsShortfall(outcome), "skipped check");
            }));

            cases.Add(Case("classify_could_not_run_is_shortfall", "A completed stage whose check could not run is a shortfall", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Completed);
                outcome.StageCheckStatus = StageCheckStatusEnum.CouldNotRun;
                AssertTrue(FailureCauseClassifier.IsShortfall(outcome), "could not run");
            }));

            cases.Add(Case("classify_pending_is_not_shortfall", "A pending disposition is never classified", TestTags.Positive, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Pending);
                outcome.FailureKind = MissionFailureKindEnum.Compile;
                AssertFalse(FailureCauseClassifier.IsShortfall(outcome), "pending");
            }));

            cases.Add(Case("classify_builtin_compile_is_capability", "A compile failure maps to Capability through the built-in rule", TestTags.Positive, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureKind = MissionFailureKindEnum.Compile;
                List<FailureCauseRule> rules = FailureCauseRuleCatalog.Build();
                FailureCauseClassification result = FailureCauseClassifier.Classify(outcome, rules);
                AssertEqual(FailureCauseEnum.Capability, result.Cause);
                AssertEqual(FailureCauseSourceEnum.Rule, result.Source);
                AssertEqual(rules.Single(r => r.Name == "Build or tests failed").Id, result.RuleId);
            }));

            cases.Add(Case("classify_lowest_priority_wins", "When several rules match, the lowest priority rule is used", TestTags.Positive, () =>
            {
                // Infra failure that is also Interrupted: priority 10 (NotTheModel by kind) wins over 30.
                StageOutcome outcome = Outcome(StageDispositionEnum.Interrupted);
                outcome.FailureKind = MissionFailureKindEnum.Infra;
                FailureCauseRule late = CustomRule("late", 5000, FailureCauseEnum.Capability, m => m.FailureKinds.Add(MissionFailureKindEnum.Infra));
                FailureCauseRule early = CustomRule("early", 1, FailureCauseEnum.Mixed, m => m.Dispositions.Add(StageDispositionEnum.Interrupted));
                FailureCauseClassification result = FailureCauseClassifier.Classify(outcome, new[] { late, early });
                AssertEqual(FailureCauseEnum.Mixed, result.Cause);
                AssertEqual(early.Id, result.RuleId);
            }));

            cases.Add(Case("classify_priority_tie_breaks_by_name", "Rules with the same priority are ordered by name", TestTags.Positive, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureKind = MissionFailureKindEnum.TestFail;
                FailureCauseRule b = CustomRule("b rule", 10, FailureCauseEnum.Context, m => m.FailureKinds.Add(MissionFailureKindEnum.TestFail));
                FailureCauseRule a = CustomRule("A rule", 10, FailureCauseEnum.ToolUse, m => m.FailureKinds.Add(MissionFailureKindEnum.TestFail));
                AssertEqual(FailureCauseEnum.ToolUse, FailureCauseClassifier.Classify(outcome, new[] { b, a }).Cause);
            }));

            cases.Add(Case("classify_disabled_rule_ignored_falls_back_to_unknown", "A disabled rule is ignored and an unmatched shortfall is Unknown", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureKind = MissionFailureKindEnum.Compile;
                FailureCauseRule disabled = CustomRule("off", 1, FailureCauseEnum.Capability, m => m.FailureKinds.Add(MissionFailureKindEnum.Compile));
                disabled.Enabled = false;
                FailureCauseClassification result = FailureCauseClassifier.Classify(outcome, new[] { disabled });
                AssertEqual(FailureCauseEnum.Unknown, result.Cause);
                AssertEqual(FailureCauseSourceEnum.None, result.Source);
                AssertNull(result.RuleId, "rule id");
            }));

            cases.Add(Case("classify_no_rules_is_unknown", "A shortfall with no rules at all is Unknown", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Escalated);
                AssertEqual(FailureCauseEnum.Unknown, FailureCauseClassifier.Classify(outcome, new List<FailureCauseRule>()).Cause);
            }));

            cases.Add(Case("classify_null_arguments_throw", "Classify rejects a null outcome or rule list", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => FailureCauseClassifier.Classify(null!, new List<FailureCauseRule>()), "null outcome");
                AssertThrows<ArgumentNullException>(() => FailureCauseClassifier.Classify(Outcome(StageDispositionEnum.Failed), null!), "null rules");
                AssertThrows<ArgumentNullException>(() => FailureCauseClassifier.IsShortfall(null!), "null shortfall");
            }));

            cases.Add(Case("classify_judge_rejection_is_not_judge_shortfall", "A Judge that returned FAIL or NEEDS_REVISION did not fall short", TestTags.Positive, () =>
            {
                foreach (string verdict in new[] { nameof(JudgeVerdictEnum.Fail), nameof(JudgeVerdictEnum.NeedsRevision) })
                {
                    StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                    outcome.Persona = PersonaCatalog.Judge;
                    outcome.FailureKind = MissionFailureKindEnum.JudgeRejected;
                    outcome.JudgeVerdict = verdict;
                    AssertFalse(FailureCauseClassifier.IsShortfall(outcome), verdict);
                }
            }));

            cases.Add(Case("classify_judge_rejected_without_verdict_is_shortfall", "JudgeRejected with no parsed verdict is still a shortfall", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.Persona = PersonaCatalog.Judge;
                outcome.FailureKind = MissionFailureKindEnum.JudgeRejected;
                outcome.JudgeVerdict = nameof(JudgeVerdictEnum.None);
                AssertTrue(FailureCauseClassifier.IsShortfall(outcome), "no verdict");
            }));

            cases.Add(Case("match_every_criterion_must_hold", "A rule matches only when every criterion it sets matches", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureKind = MissionFailureKindEnum.Compile;
                outcome.Runtime = AgentRuntimeEnum.Codex;
                outcome.Persona = "Worker";
                FailureCauseRuleMatch match = new FailureCauseRuleMatch();
                match.FailureKinds.Add(MissionFailureKindEnum.Compile);
                match.Personas.Add(" worker ");
                AssertTrue(FailureCauseClassifier.Matches(match, outcome), "kind and persona (trimmed, any case)");
                match.Runtimes.Add(AgentRuntimeEnum.ClaudeCode);
                AssertFalse(FailureCauseClassifier.Matches(match, outcome), "runtime differs");
                outcome.Runtime = null;
                AssertFalse(FailureCauseClassifier.Matches(match, outcome), "runtime missing");
            }));

            cases.Add(Case("match_empty_or_null_never_matches", "A match with no criteria, or a null match or outcome, matches nothing", TestTags.Negative, () =>
            {
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                AssertFalse(FailureCauseClassifier.Matches(new FailureCauseRuleMatch(), outcome), "empty");
                AssertFalse(FailureCauseClassifier.Matches(null, outcome), "null match");
                FailureCauseRuleMatch match = new FailureCauseRuleMatch();
                match.Dispositions.Add(StageDispositionEnum.Failed);
                AssertFalse(FailureCauseClassifier.Matches(match, null), "null outcome");
                match.TextPattern = "   ";
                AssertTrue(FailureCauseClassifier.Matches(match, outcome), "whitespace pattern is no criterion");
            }));

            cases.Add(Case("match_text_pattern", "A text pattern matches the failure reason case-insensitively and needs a reason", TestTags.Positive, () =>
            {
                FailureCauseRuleMatch match = new FailureCauseRuleMatch();
                match.TextPattern = "maximum context length";
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                AssertFalse(FailureCauseClassifier.Matches(match, outcome), "no reason");
                outcome.FailureReason = "Error: This model's MAXIMUM CONTEXT LENGTH is 32768 tokens";
                AssertTrue(FailureCauseClassifier.Matches(match, outcome), "matches");
                AssertEqual(FailureCauseEnum.Context, FailureCauseClassifier.Classify(outcome, FailureCauseRuleCatalog.Build()).Cause);
            }));

            cases.Add(Case("match_invalid_pattern_never_matches", "An invalid stored pattern matches nothing instead of throwing", TestTags.Negative, () =>
            {
                FailureCauseRuleMatch match = new FailureCauseRuleMatch();
                match.TextPattern = "([unclosed";
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureReason = "([unclosed";
                AssertFalse(FailureCauseClassifier.Matches(match, outcome), "invalid pattern");
            }));

            cases.Add(Case("match_pattern_timeout_never_matches", "A catastrophic pattern times out and matches nothing", TestTags.Negative, () =>
            {
                FailureCauseRuleMatch match = new FailureCauseRuleMatch();
                match.TextPattern = "^(a+)+$";
                StageOutcome outcome = Outcome(StageDispositionEnum.Failed);
                outcome.FailureReason = new string('a', 5000) + "!";
                DateTime start = DateTime.UtcNow;
                AssertFalse(FailureCauseClassifier.Matches(match, outcome), "timed out");
                AssertTrue(DateTime.UtcNow - start < TimeSpan.FromSeconds(10), "bounded by the pattern timeout");
            }));

            cases.Add(Case("validate_rules", "Validate accepts a good rule and names what is wrong with a bad one", TestTags.Negative, () =>
            {
                FailureCauseRule good = CustomRule("ok", 1, FailureCauseEnum.Protocol, m => m.TextPattern = "marker");
                AssertNull(FailureCauseClassifier.Validate(good), "valid");
                AssertNotNull(FailureCauseClassifier.Validate(null), "null rule");

                FailureCauseRule noName = CustomRule(" ", 1, FailureCauseEnum.Protocol, m => m.TextPattern = "x");
                AssertContains("Name", FailureCauseClassifier.Validate(noName)!);

                FailureCauseRule longName = CustomRule(new string('n', 201), 1, FailureCauseEnum.Protocol, m => m.TextPattern = "x");
                AssertContains("200", FailureCauseClassifier.Validate(longName)!);

                FailureCauseRule noCriteria = CustomRule("empty", 1, FailureCauseEnum.Protocol, m => { });
                AssertContains("criterion", FailureCauseClassifier.Validate(noCriteria)!);

                FailureCauseRule none = CustomRule("none", 1, FailureCauseEnum.None, m => m.TextPattern = "x");
                AssertContains("None", FailureCauseClassifier.Validate(none)!);

                FailureCauseRule undefinedCause = CustomRule("undefined", 1, (FailureCauseEnum)999, m => m.TextPattern = "x");
                AssertContains("known", FailureCauseClassifier.Validate(undefinedCause)!);

                FailureCauseRule badPattern = CustomRule("bad", 1, FailureCauseEnum.Protocol, m => m.TextPattern = "(");
                AssertContains("regular expression", FailureCauseClassifier.Validate(badPattern)!);

                FailureCauseRule longPattern = CustomRule("long", 1, FailureCauseEnum.Protocol, m => m.TextPattern = new string('a', 1001));
                AssertContains("1000", FailureCauseClassifier.Validate(longPattern)!);
            }));

            cases.Add(Case("catalog_rules_are_valid_and_unique", "Every built-in rule validates, is built in, and has a unique name", TestTags.Positive, () =>
            {
                List<FailureCauseRule> rules = FailureCauseRuleCatalog.Build();
                AssertTrue(rules.Count > 0, "has rules");
                foreach (FailureCauseRule rule in rules)
                {
                    AssertNull(FailureCauseClassifier.Validate(rule), rule.Name);
                    AssertTrue(rule.IsBuiltIn, rule.Name + " built in");
                    AssertNull(rule.TenantId, rule.Name + " tenant");
                }
                AssertEqual(rules.Count, rules.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique names");
            }));

            cases.Add(Case("disposition_from_mission_state", "The disposition reflects what Armada did with the attempt", TestTags.Positive, () =>
            {
                AssertEqual(StageDispositionEnum.Cancelled, Decide(MissionStatusEnum.Cancelled));
                AssertEqual(StageDispositionEnum.Failed, Decide(MissionStatusEnum.Failed));
                AssertEqual(StageDispositionEnum.Failed, Decide(MissionStatusEnum.LandingFailed));
                AssertEqual(StageDispositionEnum.Completed, Decide(MissionStatusEnum.WorkProduced));
                AssertEqual(StageDispositionEnum.Completed, Decide(MissionStatusEnum.Complete));
                AssertEqual(StageDispositionEnum.Relaunched, Decide(MissionStatusEnum.Pending, exitCode: 1));
                AssertEqual(StageDispositionEnum.Interrupted, Decide(MissionStatusEnum.Pending, exitCode: -1));
                AssertEqual(StageDispositionEnum.OutputRepair, Decide(MissionStatusEnum.InProgress, outputRepairs: 1));
                AssertEqual(StageDispositionEnum.Interrupted, Decide(MissionStatusEnum.Assigned, exitCode: -1, redispatches: 1));
            }));

            cases.Add(Case("disposition_failed_wins_over_repair_and_escalation", "A failed mission is Failed even when other counters moved", TestTags.Negative, () =>
            {
                AssertEqual(StageDispositionEnum.Failed, Decide(MissionStatusEnum.Failed, outputRepairs: 1, nextCaptain: "cpt_other"));
            }));

            cases.Add(Case("disposition_escalated_records_next_captain", "A mission now held by another captain is Escalated with that captain", TestTags.Positive, () =>
            {
                StageAttemptCapture capture = Capture(exitCode: 1);
                Mission after = new Mission("m", "d") { Status = MissionStatusEnum.Assigned, CaptainId = "cpt_strong" };
                StageOutcome outcome = new StageOutcome();
                StageOutcomeService.DecideDisposition(capture, after, outcome);
                AssertEqual(StageDispositionEnum.Escalated, outcome.Disposition);
                AssertEqual("cpt_strong", outcome.NextCaptainId);
            }));

            cases.Add(Case("disposition_failure_reason_from_blocking_check", "The failure reason falls back to a blocking stage check message, truncated", TestTags.Negative, () =>
            {
                StageAttemptCapture capture = Capture(exitCode: 0);
                capture.StageCheck = new StageCheckResult { Status = StageCheckStatusEnum.Failed, FailureMessage = new string('x', StageOutcomeService.MaxFailureReasonLength + 50) };
                Mission after = new Mission("m", "d") { Status = MissionStatusEnum.InProgress, CaptainId = "cpt_self" };
                StageOutcome outcome = new StageOutcome();
                StageOutcomeService.DecideDisposition(capture, after, outcome);
                if (capture.StageCheck.IsBlocking)
                {
                    AssertNotNull(outcome.FailureReason, "reason");
                    AssertEqual(StageOutcomeService.MaxFailureReasonLength, outcome.FailureReason!.Length, "truncated");
                }

                after.FailureReason = "mission reason";
                StageOutcomeService.DecideDisposition(capture, after, outcome);
                AssertEqual("mission reason", outcome.FailureReason, "mission reason preferred");
            }));

            cases.Add(CaseAsync("seed_builtin_rules_is_idempotent_and_keeps_edits", "Seeding adds missing built-in rules once and leaves edited ones alone", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StageOutcomeService service = new StageOutcomeService(db, CreateLogging());
                int expected = FailureCauseRuleCatalog.Build().Count;

                AssertEqual(expected, await service.EnsureBuiltInRulesAsync().ConfigureAwait(false), "first seed");
                AssertEqual(0, await service.EnsureBuiltInRulesAsync().ConfigureAwait(false), "second seed");

                List<FailureCauseRule> stored = await db.FailureCauseRules.EnumerateAsync(null).ConfigureAwait(false);
                FailureCauseRule edited = stored.Single(r => r.Name == "Build or tests failed");
                edited.Cause = FailureCauseEnum.Mixed;
                edited.Enabled = false;
                await db.FailureCauseRules.UpdateAsync(edited).ConfigureAwait(false);

                FailureCauseRule deleted = stored.Single(r => r.Name == "Context exhausted");
                AssertTrue(await db.FailureCauseRules.DeleteAsync(deleted.Id).ConfigureAwait(false), "deleted");

                AssertEqual(1, await service.EnsureBuiltInRulesAsync().ConfigureAwait(false), "restores the deleted rule only");
                FailureCauseRule? reread = await db.FailureCauseRules.ReadAsync(edited.Id).ConfigureAwait(false);
                AssertNotNull(reread, "edited rule");
                AssertEqual(FailureCauseEnum.Mixed, reread!.Cause, "edit kept");
                AssertFalse(reread.Enabled, "disable kept");
                AssertEqual(expected, (await db.FailureCauseRules.EnumerateAsync(null).ConfigureAwait(false)).Count, "no duplicates");
            }));

            cases.Add(CaseAsync("rule_roundtrip_and_tenant_listing", "A rule round-trips its match criteria and tenant listing includes global rules", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                FailureCauseRule rule = CustomRule("tenant rule", 7, FailureCauseEnum.ToolUse, m =>
                {
                    m.Personas.Add("Worker");
                    m.Runtimes.Add(AgentRuntimeEnum.Codex);
                    m.FailureKinds.Add(MissionFailureKindEnum.Compile);
                    m.RuntimeFailureKinds.Add(RuntimeFailureKindEnum.UsageLimit);
                    m.StageCheckStatuses.Add(StageCheckStatusEnum.Failed);
                    m.StageCheckReasons.Add(StageCheckReasonEnum.HarborDisconnected);
                    m.Dispositions.Add(StageDispositionEnum.Escalated);
                    m.TextPattern = "tool";
                });
                rule.TenantId = "ten_a";
                rule.Description = "desc";
                rule.CreatedByUserId = "usr_a";
                await db.FailureCauseRules.CreateAsync(rule).ConfigureAwait(false);
                FailureCauseRule global = CustomRule("global rule", 8, FailureCauseEnum.Context, m => m.TextPattern = "ctx");
                await db.FailureCauseRules.CreateAsync(global).ConfigureAwait(false);
                FailureCauseRule other = CustomRule("other tenant", 9, FailureCauseEnum.Context, m => m.TextPattern = "ctx");
                other.TenantId = "ten_b";
                await db.FailureCauseRules.CreateAsync(other).ConfigureAwait(false);

                FailureCauseRule? read = await db.FailureCauseRules.ReadAsync(rule.Id).ConfigureAwait(false);
                AssertNotNull(read, "read");
                AssertEqual("ten_a", read!.TenantId);
                AssertEqual("tenant rule", read.Name);
                AssertEqual("desc", read.Description);
                AssertEqual(7, read.Priority);
                AssertEqual(FailureCauseEnum.ToolUse, read.Cause);
                AssertEqual("usr_a", read.CreatedByUserId);
                AssertEqual("Worker", read.Match.Personas.Single());
                AssertEqual(AgentRuntimeEnum.Codex, read.Match.Runtimes.Single());
                AssertEqual(MissionFailureKindEnum.Compile, read.Match.FailureKinds.Single());
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, read.Match.RuntimeFailureKinds.Single());
                AssertEqual(StageCheckStatusEnum.Failed, read.Match.StageCheckStatuses.Single());
                AssertEqual(StageCheckReasonEnum.HarborDisconnected, read.Match.StageCheckReasons.Single());
                AssertEqual(StageDispositionEnum.Escalated, read.Match.Dispositions.Single());
                AssertEqual("tool", read.Match.TextPattern);

                List<string> forA = (await db.FailureCauseRules.EnumerateAsync("ten_a").ConfigureAwait(false)).Select(r => r.Name).ToList();
                AssertTrue(forA.Contains("tenant rule") && forA.Contains("global rule"), "tenant and global");
                AssertFalse(forA.Contains("other tenant"), "other tenant excluded");
                AssertNull(await db.FailureCauseRules.ReadAsync("fcr_missing").ConfigureAwait(false), "missing rule");
                AssertFalse(await db.FailureCauseRules.DeleteAsync("fcr_missing").ConfigureAwait(false), "delete missing");
            }));

            cases.Add(CaseAsync("record_attempt_provenance_and_classification", "Recording an attempt stores provenance, disposition, attempt number and a rule cause", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StageOutcomeService service = new StageOutcomeService(db, CreateLogging());
                await service.EnsureBuiltInRulesAsync().ConfigureAwait(false);

                Captain captain = new Captain("qwen worker", AgentRuntimeEnum.Codex);
                captain.Model = "qwen3-coder:30b";
                await db.Captains.CreateAsync(captain).ConfigureAwait(false);
                Mission mission = await CreateMissionAsync(db, MissionStatusEnum.Failed, MissionFailureKindEnum.Compile, "error CS1026: ) expected").ConfigureAwait(false);

                StageAttemptCapture capture = Capture(exitCode: 1);
                capture.MissionId = mission.Id;
                capture.Captain = captain;
                capture.StartedUtc = _Fixed;
                capture.EndedUtc = _Fixed.AddMinutes(3);
                capture.StageCheck = new StageCheckResult { Status = StageCheckStatusEnum.Failed, Reason = StageCheckReasonEnum.None };

                StageOutcome? first = await service.RecordAttemptAsync(capture).ConfigureAwait(false);
                AssertNotNull(first, "recorded");
                AssertEqual(1, first!.Attempt, "first attempt");
                AssertEqual(StageDispositionEnum.Failed, first.Disposition);
                AssertEqual(FailureCauseEnum.Capability, first.SuspectedCause);
                AssertEqual(FailureCauseSourceEnum.Rule, first.CauseSource);
                AssertNotNull(first.CauseRuleId, "rule id");
                AssertEqual(180000L, first.DurationMs, "duration");
                AssertEqual("Worker", first.Persona, "default persona");
                AssertNotNull(first.PromptVersion, "prompt version");
                AssertNotNull(first.ArmadaVersion, "armada version");

                StageOutcome? read = await db.StageOutcomes.ReadAsync(first.Id).ConfigureAwait(false);
                AssertNotNull(read, "read back");
                AssertEqual(mission.Id, read!.MissionId);
                AssertEqual(mission.VesselId, read.VesselId);
                AssertEqual(mission.VoyageId, read.VoyageId);
                AssertEqual(captain.Id, read.CaptainId);
                AssertEqual("qwen worker", read.CaptainName);
                AssertEqual(AgentRuntimeEnum.Codex, read.Runtime);
                AssertEqual("qwen3-coder:30b", read.Model);
                AssertEqual(1, read.ExitCode);
                AssertEqual(StageCheckStatusEnum.Failed, read.StageCheckStatus);
                AssertEqual(MissionFailureKindEnum.Compile, read.FailureKind);
                AssertEqual("error CS1026: ) expected", read.FailureReason);
                AssertEqual(FailureCauseEnum.Capability, read.SuspectedCause);
                AssertEqual(first.CauseRuleId, read.CauseRuleId);
                AssertFalse(read.IsEvaluation, "not an evaluation");

                StageOutcome? second = await service.RecordAttemptAsync(capture).ConfigureAwait(false);
                AssertEqual(2, second!.Attempt, "second attempt");
                AssertEqual(2, await db.StageOutcomes.CountByMissionAsync(mission.Id).ConfigureAwait(false), "count");

                EnumerationResult<StageOutcome> byCause = await db.StageOutcomes.EnumerateAsync(new StageOutcomeQuery { MissionId = mission.Id, SuspectedCause = FailureCauseEnum.Capability }).ConfigureAwait(false);
                AssertEqual(2, byCause.Objects.Count, "filter by cause");
                EnumerationResult<StageOutcome> none = await db.StageOutcomes.EnumerateAsync(new StageOutcomeQuery { MissionId = mission.Id, SuspectedCause = FailureCauseEnum.Context }).ConfigureAwait(false);
                AssertEqual(0, none.Objects.Count, "filter excludes");

                AssertEqual(2, await db.StageOutcomes.DeleteByMissionAsync(mission.Id).ConfigureAwait(false), "delete by mission");
                AssertEqual(0, await db.StageOutcomes.CountByMissionAsync(mission.Id).ConfigureAwait(false), "deleted");
            }));

            cases.Add(CaseAsync("record_attempt_skips_unclaimed_and_missing", "Unclaimed captures, missing captains and missing missions record nothing", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StageOutcomeService service = new StageOutcomeService(db, CreateLogging());
                Captain captain = new Captain("c");

                StageAttemptCapture unclaimed = Capture(exitCode: 0);
                unclaimed.Claimed = false;
                unclaimed.MissionId = "msn_x";
                unclaimed.Captain = captain;
                AssertNull(await service.RecordAttemptAsync(unclaimed).ConfigureAwait(false), "unclaimed");

                StageAttemptCapture noCaptain = Capture(exitCode: 0);
                noCaptain.MissionId = "msn_x";
                AssertNull(await service.RecordAttemptAsync(noCaptain).ConfigureAwait(false), "no captain");

                StageAttemptCapture missing = Capture(exitCode: 0);
                missing.MissionId = "msn_does_not_exist";
                missing.Captain = captain;
                AssertNull(await service.RecordAttemptAsync(missing).ConfigureAwait(false), "missing mission");

                await AssertThrowsAsync<ArgumentNullException>(() => service.RecordAttemptAsync(null!), "null capture").ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("manual_classification_survives_reclassify", "A manual cause is kept by re-application, and clearing it hands it back to the rules", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StageOutcomeService service = new StageOutcomeService(db, CreateLogging());
                await service.EnsureBuiltInRulesAsync().ConfigureAwait(false);
                StageOutcome stored = await db.StageOutcomes.CreateAsync(StoredOutcome("msn_manual", MissionFailureKindEnum.Compile)).ConfigureAwait(false);

                StageOutcome? manual = await service.ClassifyManuallyAsync(stored.Id, FailureCauseEnum.Context, new string('n', 2100), "usr_admin").ConfigureAwait(false);
                AssertNotNull(manual, "classified");
                AssertEqual(FailureCauseEnum.Context, manual!.SuspectedCause);
                AssertEqual(FailureCauseSourceEnum.Manual, manual.CauseSource);
                AssertNull(manual.CauseRuleId, "no rule");
                AssertEqual("usr_admin", manual.ClassifiedByUserId);
                AssertEqual(StageOutcomeService.MaxFailureReasonLength, manual.CauseNote!.Length, "note truncated");

                StageOutcomeReclassifyResult result = await service.ReclassifyAsync(new StageOutcomeQuery { MissionId = "msn_manual" }).ConfigureAwait(false);
                AssertEqual(1, result.Examined);
                AssertEqual(1, result.SkippedManual);
                AssertEqual(0, result.Changed);
                AssertEqual(FailureCauseEnum.Context, (await db.StageOutcomes.ReadAsync(stored.Id).ConfigureAwait(false))!.SuspectedCause, "kept");

                StageOutcome? cleared = await service.ClassifyManuallyAsync(stored.Id, null, null, "usr_admin").ConfigureAwait(false);
                AssertEqual(FailureCauseEnum.Capability, cleared!.SuspectedCause, "rules re-applied");
                AssertEqual(FailureCauseSourceEnum.Rule, cleared.CauseSource);
                AssertNull(cleared.ClassifiedByUserId, "user cleared");

                AssertNull(await service.ClassifyManuallyAsync("sto_missing", FailureCauseEnum.Context, null, null).ConfigureAwait(false), "missing outcome");
                await AssertThrowsAsync<ArgumentNullException>(() => service.ClassifyManuallyAsync("", FailureCauseEnum.Context, null, null), "empty id").ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("reclassify_applies_new_rule_to_past_outcomes", "A new rule re-applied to past Unknown outcomes changes them, and a second pass changes nothing", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StageOutcomeService service = new StageOutcomeService(db, CreateLogging());

                for (int i = 0; i < 3; i++)
                {
                    StageOutcome o = StoredOutcome("msn_re", null);
                    o.Disposition = StageDispositionEnum.Escalated;
                    o.FailureReason = "lost track of the task after compaction";
                    o.SuspectedCause = FailureCauseEnum.Unknown;
                    await db.StageOutcomes.CreateAsync(o).ConfigureAwait(false);
                }

                FailureCauseRule rule = CustomRule("compaction", 5, FailureCauseEnum.Context, m => m.TextPattern = "compaction");
                await db.FailureCauseRules.CreateAsync(rule).ConfigureAwait(false);

                StageOutcomeReclassifyResult first = await service.ReclassifyAsync(new StageOutcomeQuery { MissionId = "msn_re", SuspectedCause = FailureCauseEnum.Unknown }).ConfigureAwait(false);
                AssertEqual(3, first.Examined, "examined all, despite the filter on the cause being changed");
                AssertEqual(3, first.Changed);
                EnumerationResult<StageOutcome> after = await db.StageOutcomes.EnumerateAsync(new StageOutcomeQuery { MissionId = "msn_re" }).ConfigureAwait(false);
                AssertTrue(after.Objects.All(o => o.SuspectedCause == FailureCauseEnum.Context && o.CauseRuleId == rule.Id), "all Context");

                StageOutcomeReclassifyResult second = await service.ReclassifyAsync(new StageOutcomeQuery { MissionId = "msn_re" }).ConfigureAwait(false);
                AssertEqual(0, second.Changed, "idempotent");
                await AssertThrowsAsync<ArgumentNullException>(() => service.ReclassifyAsync(null!), "null filter").ConfigureAwait(false);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Stage Outcomes and Failure Causes",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static StageOutcome Outcome(StageDispositionEnum disposition)
        {
            return new StageOutcome { MissionId = "msn_test", Persona = "Worker", Disposition = disposition };
        }

        private static StageOutcome StoredOutcome(string missionId, MissionFailureKindEnum? kind)
        {
            return new StageOutcome
            {
                MissionId = missionId,
                Persona = "Worker",
                Attempt = 1,
                Disposition = StageDispositionEnum.Failed,
                FailureKind = kind,
                StartedUtc = _Fixed,
                EndedUtc = _Fixed.AddMinutes(1)
            };
        }

        private static FailureCauseRule CustomRule(string name, int priority, FailureCauseEnum cause, Action<FailureCauseRuleMatch> configure)
        {
            FailureCauseRule rule = new FailureCauseRule { Name = name, Priority = priority, Cause = cause, Enabled = true };
            configure(rule.Match);
            return rule;
        }

        private static StageAttemptCapture Capture(int? exitCode)
        {
            return new StageAttemptCapture
            {
                Claimed = true,
                Captain = new Captain("self") { Id = "cpt_self" },
                ExitCode = exitCode,
                OutputRepairAttemptsBefore = 0,
                RedispatchAttemptsBefore = 0
            };
        }

        private static StageDispositionEnum Decide(MissionStatusEnum status, int? exitCode = 0, int outputRepairs = 0, int redispatches = 0, string? nextCaptain = null)
        {
            StageAttemptCapture capture = Capture(exitCode);
            Mission after = new Mission("m", "d")
            {
                Status = status,
                CaptainId = nextCaptain ?? "cpt_self",
                OutputRepairAttempts = outputRepairs,
                RedispatchAttempts = redispatches
            };
            StageOutcome outcome = new StageOutcome();
            StageOutcomeService.DecideDisposition(capture, after, outcome);
            return outcome.Disposition;
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver db, MissionStatusEnum status, MissionFailureKindEnum? kind, string? reason)
        {
            Fleet fleet = new Fleet("Outcome Fleet");
            await db.Fleets.CreateAsync(fleet).ConfigureAwait(false);
            Vessel vessel = new Vessel("Outcome Vessel", "https://github.com/test/outcomes");
            vessel.FleetId = fleet.Id;
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            Voyage voyage = new Voyage("Outcome Voyage", "desc");
            await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("Outcome Mission", "desc");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Status = status;
            mission.FailureKind = kind;
            mission.FailureReason = reason;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return CaseAsync(caseId, displayName, tag, () =>
            {
                body();
                return Task.CompletedTask;
            });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
