namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ProgressParser"/>, which extracts <c>[ARMADA:*]</c> control
    /// signals from agent stdout. Balanced positive and negative coverage: valid signals of
    /// every type, plus rejection of embedded, malformed, and non-signal input.
    /// </summary>
    public sealed class ProgressParserSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ----- Positive: well-formed signals of each type -----

            cases.Add(Case("progress_parses_percentage", "PROGRESS signal parses percentage", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 75");
                AssertNotNull(result);
                AssertEqual("progress", result!.Type);
                AssertEqual("75", result.Value);
                AssertEqual(75, result.Percentage);
            }));

            cases.Add(Case("progress_with_percent_sign", "PROGRESS signal tolerates a percent sign", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 50%");
                AssertNotNull(result);
                AssertEqual(50, result!.Percentage);
            }));

            cases.Add(Case("progress_clamps_low", "PROGRESS below 0 clamps to 0", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] -10");
                AssertNotNull(result);
                AssertEqual(0, result!.Percentage);
            }));

            cases.Add(Case("progress_clamps_high", "PROGRESS above 100 clamps to 100", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] 150");
                AssertNotNull(result);
                AssertEqual(100, result!.Percentage);
            }));

            cases.Add(Case("progress_leading_trailing_whitespace", "PROGRESS parses with surrounding whitespace", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("  [ARMADA:PROGRESS] 30  ");
                AssertNotNull(result);
                AssertEqual(30, result!.Percentage);
            }));

            cases.Add(Case("status_parses_enum", "STATUS signal parses a mission status", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] Testing");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertEqual(MissionStatusEnum.Testing, result.MissionStatus);
            }));

            cases.Add(Case("status_review", "STATUS Review parses", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] Review");
                AssertNotNull(result);
                AssertEqual(MissionStatusEnum.Review, result!.MissionStatus);
            }));

            cases.Add(Case("status_case_insensitive", "STATUS parsing is case-insensitive", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[armada:status] testing");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertEqual(MissionStatusEnum.Testing, result.MissionStatus);
            }));

            cases.Add(Case("message_parses_value", "MESSAGE signal preserves its text", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:MESSAGE] Running unit tests now");
                AssertNotNull(result);
                AssertEqual("message", result!.Type);
                AssertEqual("Running unit tests now", result.Value);
                AssertNull(result.Percentage);
                AssertNull(result.MissionStatus);
            }));

            cases.Add(Case("result_preserves_value", "RESULT signal preserves its value", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:RESULT] COMPLETE");
                AssertNotNull(result);
                AssertEqual("result", result!.Type);
                AssertEqual("COMPLETE", result.Value);
            }));

            cases.Add(Case("verdict_preserves_value", "VERDICT signal preserves its value", TestTags.Positive, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:VERDICT] PASS");
                AssertNotNull(result);
                AssertEqual("verdict", result!.Type);
                AssertEqual("PASS", result.Value);
            }));

            // ----- Negative: input that must NOT yield a signal (or must not over-parse) -----

            cases.Add(Case("null_returns_null", "null input returns null", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse(null!));
            }));

            cases.Add(Case("empty_returns_null", "empty input returns null", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse(""));
            }));

            cases.Add(Case("plain_line_returns_null", "a plain log line returns null", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse("Just a regular log line"));
            }));

            cases.Add(Case("embedded_signal_returns_null", "a signal embedded mid-line is not parsed", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse("some prefix [ARMADA:PROGRESS] 30"));
            }));

            cases.Add(Case("instruction_example_returns_null", "a documented example line is not parsed", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse("- `[ARMADA:PROGRESS] 50` -- report completion percentage (0-100)"));
            }));

            cases.Add(Case("status_invalid_enum_no_status", "STATUS with an unknown value yields a signal but no status", TestTags.Negative, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:STATUS] InvalidState");
                AssertNotNull(result);
                AssertEqual("status", result!.Type);
                AssertNull(result.MissionStatus);
            }));

            cases.Add(Case("progress_non_numeric_no_percentage", "PROGRESS with a non-numeric value yields no percentage", TestTags.Negative, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:PROGRESS] almost");
                AssertNotNull(result);
                AssertEqual("progress", result!.Type);
                AssertNull(result.Percentage);
            }));

            cases.Add(Case("tag_without_value_returns_null", "a tag with no value does not match", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse("[ARMADA:PROGRESS]"));
            }));

            cases.Add(Case("message_with_only_whitespace_yields_empty_value", "a tag followed by only whitespace yields a signal with an empty value", TestTags.Negative, () =>
            {
                // The lazy (.+?) group matches a single whitespace char, so the tag does parse,
                // but the value trims to empty. Documents a degenerate-but-accepted edge.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse("[ARMADA:MESSAGE]    ");
                AssertNotNull(result);
                AssertEqual("message", result!.Type);
                AssertEqual("", result.Value);
            }));

            cases.Add(Case("malformed_bracket_returns_null", "a malformed bracket does not match", TestTags.Negative, () =>
            {
                AssertNull(ProgressParser.TryParse("[ARMADA PROGRESS] 40"));
            }));

            // ----- Multi-line records: a runtime record can carry several physical lines -----

            cases.Add(Case("multi_line_record_finds_signal_on_own_line", "a signal on its own line in a multi-line record is found", TestTags.Positive, () =>
            {
                // A runtime record is not always one physical line: a Codex agent_message or Claude
                // assistant text block can carry a marker followed by prose in a single record.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "[ARMADA:RESULT] COMPLETE\nWired the recovery rows, passed 25 focused tests, committed d152a300.");
                AssertNotNull(result);
                AssertEqual("result", result!.Type);
                AssertEqual("COMPLETE", result.Value);
            }));

            cases.Add(Case("multi_line_record_papercut_wins", "a papercut wins over other markers in the same record", TestTags.Positive, () =>
            {
                // A papercut is a report that must reach the store, so it wins over the other
                // markers in the same record. This is the exact shape that was silently dropped
                // before the fix: a final answer that opens with a papercut and ends with RESULT.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "[ARMADA:PAPERCUT] {\"category\":\"RepoFriction\",\"severity\":\"Low\",\"title\":\"Worker branch lagged landed M2\"}\n\n[ARMADA:RESULT] COMPLETE\nWired the rows.");
                AssertNotNull(result);
                AssertEqual("papercut", result!.Type);
            }));

            cases.Add(Case("multi_line_record_prose_prefix_still_null", "a marker mid-prose in a multi-line record is not a signal", TestTags.Negative, () =>
            {
                // A marker in the MIDDLE of a prose line is not a signal, in a single-line or a
                // multi-line record alike: an instruction file documents the format this way.
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "Some prefix text [ARMADA:PROGRESS] 30\ncontinued prose");
                AssertNull(result);
            }));

            cases.Add(Case("multi_line_record_blank_and_prose_only_null", "a multi-line record of only prose returns null", TestTags.Negative, () =>
            {
                ProgressParser.ProgressSignal? result = ProgressParser.TryParse(
                    "Wired the recovery rows into TryGet.\n\nPassed 25 focused tests.");
                AssertNull(result);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.ProgressParser",
                displayName: "Progress Parser",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.ProgressParser",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
