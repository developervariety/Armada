namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the papercut pipeline: <see cref="PapercutParser"/> line/value parsing and
    /// sanitizing, and <see cref="PapercutService"/> event round-trip, title normalization, and
    /// grouping. The parser accepts JSON, pipe-delimited, and bare-text forms; drops truncated JSON;
    /// falls back to Other/Low on unknown category/severity; strips control characters; redacts
    /// credential-shaped text; and rejects the brief's literal example. The service round-trips a
    /// papercut through an event and collapses repeated friction into groups that carry the worst
    /// severity and distinct-captain evidence.
    /// </summary>
    public sealed class PapercutSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.Papercut";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Papercut suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ----- Parser: marker gating -----

            cases.Add(Case("try_parse_line_non_papercut_marker_returns_null", "TryParseLine ignores a non-papercut marker", TestTags.Negative, () =>
            {
                AssertNull(PapercutParser.TryParseLine("[ARMADA:PROGRESS] 50"));
            }));

            cases.Add(Case("try_parse_line_plain_line_returns_null", "TryParseLine ignores a plain log line", TestTags.Negative, () =>
            {
                AssertNull(PapercutParser.TryParseLine("just a log line"));
            }));

            cases.Add(Case("try_parse_line_json_parses_every_field", "TryParseLine parses every JSON field", TestTags.Positive, () =>
            {
                Papercut? papercut = PapercutParser.TryParseLine(
                    "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Medium\",\"title\":\"README build command is wrong\",\"detail\":\"It names make, the repo uses dotnet\",\"path\":\"README.md\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.Medium, papercut.Severity);
                AssertEqual("README build command is wrong", papercut.Title);
                AssertEqual("README.md", papercut.Path);
            }));

            // ----- Parser: category / severity fallbacks -----

            cases.Add(Case("try_parse_value_unknown_category_falls_back_to_other", "TryParseValue falls back to Other for an unknown category", TestTags.Negative, () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("{\"category\":\"Nonsense\",\"title\":\"something\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.Other, papercut!.Category);
            }));

            cases.Add(Case("try_parse_value_unknown_severity_falls_back_to_low", "TryParseValue falls back to Low for an unknown severity", TestTags.Negative, () =>
            {
                // A formatting slip must never outrank a real defect, so an unreadable severity is Low.
                Papercut? papercut = PapercutParser.TryParseValue("{\"severity\":\"Catastrophic\",\"title\":\"something\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutSeverityEnum.Low, papercut!.Severity);
            }));

            cases.Add(Case("try_parse_value_category_with_spacing_resolves", "TryParseValue resolves a category with spacing", TestTags.Positive, () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("{\"category\":\"broken link\",\"title\":\"dead link in docs\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.BrokenLink, papercut!.Category);
            }));

            // ----- Parser: pipe and bare-text forms -----

            cases.Add(Case("try_parse_value_pipe_form_parses", "TryParseValue parses the pipe-delimited form", TestTags.Positive, () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("ToolFailure|High|dotnet test hangs on the Api project");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.ToolFailure, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.High, papercut.Severity);
                AssertEqual("dotnet test hangs on the Api project", papercut.Title);
            }));

            cases.Add(Case("try_parse_value_bare_text_becomes_other_low", "TryParseValue treats bare text as Other/Low", TestTags.Positive, () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue("the sibling repo was missing");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.Other, papercut!.Category);
                AssertEqual(PapercutSeverityEnum.Low, papercut.Severity);
                AssertEqual("the sibling repo was missing", papercut.Title);
            }));

            // ----- Parser: rejection of unusable input -----

            cases.Add(Case("try_parse_value_truncated_json_returns_null", "TryParseValue drops truncated JSON", TestTags.Negative, () =>
            {
                // A line that opens as JSON and does not close is a truncated report, not a title.
                AssertNull(PapercutParser.TryParseValue("{\"category\":\"ToolFailure\",\"title\":\"dotnet te"));
            }));

            cases.Add(Case("try_parse_value_empty_title_returns_null", "TryParseValue rejects an empty title", TestTags.Negative, () =>
            {
                AssertNull(PapercutParser.TryParseValue("{\"category\":\"ToolFailure\",\"title\":\"   \"}"));
            }));

            cases.Add(Case("try_parse_value_template_example_returns_null", "TryParseValue rejects the brief's literal example", TestTags.Negative, () =>
            {
                // The brief instruction template shows this literal example so captains can see the
                // report shape. It is not a report: a scan that walks the log instructions directory
                // counts it many times as a phantom MissingDoc, and a captain may echo it verbatim
                // while testing the format. Neither may reach the store.
                AssertNull(PapercutParser.TryParseValue(
                    "{\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line\",\"detail\":\"optional\",\"path\":\"optional/file.cs\"}"));

                AssertNull(PapercutParser.TryParseLine(
                    "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line\",\"detail\":\"optional\",\"path\":\"optional/file.cs\"}"));
            }));

            cases.Add(Case("try_parse_value_real_report_still_parses", "TryParseValue keeps a real report that resembles the example", TestTags.Positive, () =>
            {
                // The placeholder rejection must not catch a genuine report whose wording merely
                // resembles the example.
                Papercut? papercut = PapercutParser.TryParseValue(
                    "{\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line README is missing\",\"detail\":\"the real detail\",\"path\":\"README.md\"}");

                AssertNotNull(papercut);
                AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
            }));

            // ----- Parser: sanitizing -----

            cases.Add(Case("try_parse_value_redacts_credentials", "TryParseValue redacts credential-shaped detail", TestTags.Positive, () =>
            {
                Papercut? papercut = PapercutParser.TryParseValue(
                    "{\"title\":\"auth failed\",\"detail\":\"ran curl -H token=abc123secretvalue against the api\"}");

                AssertNotNull(papercut);
                AssertContains("[redacted]", papercut!.Detail!);
                AssertFalse(papercut.Detail!.Contains("abc123secretvalue"));
            }));

            cases.Add(Case("sanitize_strips_control_chars", "Sanitize strips control characters", TestTags.Positive, () =>
            {
                // Embedded control characters (bell, null) must never survive into a stored record;
                // they collapse to whitespace and the surrounding words remain.
                string? cleaned = PapercutParser.Sanitize("line one\u0007\u0000 line two");

                AssertNotNull(cleaned);
                AssertFalse(cleaned!.Contains('\u0007'), "Bell control char must be stripped");
                AssertFalse(cleaned.Contains('\u0000'), "Null control char must be stripped");
                AssertContains("line one", cleaned);
                AssertContains("line two", cleaned);
            }));

            cases.Add(Case("title_is_clamped_to_limit", "TryParseValue clamps an oversized title", TestTags.Positive, () =>
            {
                string longTitle = new string('x', Papercut.MaxTitleChars + 200);
                Papercut? papercut = PapercutParser.TryParseValue("{\"title\":\"" + longTitle + "\"}");

                AssertNotNull(papercut);
                AssertTrue(papercut!.Title.Length <= Papercut.MaxTitleChars + 3, "Title should be clamped to the limit plus an ellipsis");
            }));

            // ----- Service: event round-trip -----

            cases.Add(Case("to_event_from_event_round_trips", "ToEvent and TryFromEvent round-trip a papercut", TestTags.Positive, () =>
            {
                Papercut original = new Papercut();
                original.Category = PapercutCategoryEnum.EnvSetup;
                original.Severity = PapercutSeverityEnum.High;
                original.Title = "sibling repo EcuLink was not provisioned";
                original.Detail = "the build needs it one level up";
                original.Path = "src/Thing.csproj";
                original.CaptainId = "cpt_one";
                original.MissionId = "msn_one";
                original.VesselId = "vsl_one";
                original.VoyageId = "vyg_one";
                original.Runtime = "ClaudeCode";

                ArmadaEvent evt = PapercutService.ToEvent(original);
                AssertEqual("papercut", evt.EventType);
                AssertEqual("vsl_one", evt.VesselId);

                Papercut? restored = PapercutService.TryFromEvent(evt);
                AssertNotNull(restored);
                AssertEqual(PapercutCategoryEnum.EnvSetup, restored!.Category);
                AssertEqual(PapercutSeverityEnum.High, restored.Severity);
                AssertEqual(original.Title, restored.Title);
                AssertEqual("msn_one", restored.MissionId);
                AssertEqual("ClaudeCode", restored.Runtime);
            }));

            cases.Add(Case("try_from_event_other_event_type_returns_null", "TryFromEvent ignores a non-papercut event", TestTags.Negative, () =>
            {
                ArmadaEvent evt = new ArmadaEvent("mission.completed", "not a papercut");
                evt.Payload = "{\"title\":\"something\"}";
                AssertNull(PapercutService.TryFromEvent(evt));
            }));

            // ----- Service: normalization and grouping -----

            cases.Add(Case("normalize_title_strips_ids_and_numbers", "NormalizeTitle collapses ids and numbers", TestTags.Positive, () =>
            {
                string first = PapercutService.NormalizeTitle("Mission msn_abc123 failed after 42 seconds");
                string second = PapercutService.NormalizeTitle("Mission msn_zzz999 failed after 91 seconds");
                AssertEqual(first, second);
            }));

            cases.Add(Case("group_collapses_same_friction_across_captains", "Group collapses the same friction across captains", TestTags.Positive, () =>
            {
                List<Papercut> papercuts = new List<Papercut>
                {
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "Build command in README is stale (line 12)", "cpt_one", "msn_one", DateTime.UtcNow.AddHours(-3)),
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Medium,
                        "Build command in README is stale (line 87)", "cpt_two", "msn_two", DateTime.UtcNow.AddHours(-1)),
                    BuildPapercut("vsl_two", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "Build command in README is stale (line 12)", "cpt_one", "msn_three", DateTime.UtcNow)
                };

                List<PapercutGroup> groups = PapercutService.Group(papercuts);

                AssertEqual(2, groups.Count);
                AssertEqual(2, groups[0].Count);
                AssertEqual(2, groups[0].DistinctCaptainCount);
                AssertEqual("vsl_one", groups[0].VesselId);

                // The group carries the worst severity anyone reported, not the first one seen.
                AssertEqual(PapercutSeverityEnum.Medium, groups[0].HighestSeverity);
                AssertEqual(2, groups[0].SampleMissionIds.Count);
            }));

            cases.Add(Case("group_separates_different_categories", "Group separates different categories with the same words", TestTags.Positive, () =>
            {
                List<Papercut> papercuts = new List<Papercut>
                {
                    BuildPapercut("vsl_one", PapercutCategoryEnum.MissingDoc, PapercutSeverityEnum.Low,
                        "same words entirely", "cpt_one", "msn_one", DateTime.UtcNow),
                    BuildPapercut("vsl_one", PapercutCategoryEnum.ToolFailure, PapercutSeverityEnum.Low,
                        "same words entirely", "cpt_one", "msn_two", DateTime.UtcNow)
                };

                AssertEqual(2, PapercutService.Group(papercuts).Count);
            }));

            cases.Add(Case("group_empty_input_returns_empty", "Group returns empty for null or empty input", TestTags.Negative, () =>
            {
                AssertEqual(0, PapercutService.Group(null).Count);
                AssertEqual(0, PapercutService.Group(new List<Papercut>()).Count);
            }));

            cases.Add(Case("brief_section_names_the_marker_and_the_limit", "Brief section names the marker, categories, and the limit", TestTags.Positive, () =>
            {
                string section = MissionService.BuildPapercutsSection();
                AssertTrue(section.Contains("[ARMADA:PAPERCUT]"), "The brief must name the marker captains emit");
                AssertTrue(section.Contains("BriefContradiction"), "The brief must list the categories");
                AssertTrue(section.Contains("Report it, do not fix it"), "The brief must tell captains not to fix the friction");
                AssertTrue(section.Contains("Ten per mission"), "The brief must state the per-mission limit");
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Papercut",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Papercut BuildPapercut(
            string vesselId,
            PapercutCategoryEnum category,
            PapercutSeverityEnum severity,
            string title,
            string captainId,
            string missionId,
            DateTime reportedUtc)
        {
            Papercut papercut = new Papercut();
            papercut.VesselId = vesselId;
            papercut.Category = category;
            papercut.Severity = severity;
            papercut.Title = title;
            papercut.CaptainId = captainId;
            papercut.MissionId = missionId;
            papercut.ReportedUtc = reportedUtc;
            return papercut;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
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
