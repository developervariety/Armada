namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Touchstone.Core;

    /// <summary>
    /// The one registry of shared cases that do not execute in the shared runner. Every entry names why and,
    /// for a duplicate, the executed legacy case that owns the behaviour. Discovery applies it to every
    /// runner, so each such case reports as a named, counted skip with that reason. An entry that names no
    /// discovered case, or a legacy owner that no longer exists, fails discovery instead of hiding a case.
    /// </summary>
    public static class SharedCaseDispositions
    {
        #region Public-Members

        /// <summary>
        /// Every recorded disposition.
        /// </summary>
        public static IReadOnlyList<SharedCaseDisposition> All
        {
            get { return _All; }
        }

        #endregion

        #region Private-Members

        private const string AutomatedSuites = "test/Armada.Test.Automated/Suites/";
        private const string RuntimeSuites = "test/Armada.Test.Runtimes/Suites/";
        private const string UnitServiceSuites = "test/Armada.Test.Unit/Suites/Services/";

        private const string ManualCompletionProof = "manual completion now requires landing proof, so the copy's unlanded Complete is refused with 409";
        private const string CommittedCheckFixture = "check runs use an isolated checkout, so the fixture must commit its files and send a real commit";
        private const string VerifiedCompletion = "stage completion now requires a structured result and a green voyage Check";
        private const string PromptFinalArgument = "the fork passes the prompt as the final argument instead of stdin";
        private const string ForkKeepsSeededPersonas = "kept because renaming or re-ordering seeded personas and pipelines on upgraded databases would break operator pipelines and prompt templates for no functional gain";
        private const string ForkKeepsInstructionSections = "kept because the fork's generated instruction sections are deliberate";

        private static readonly IReadOnlyList<SharedCaseDisposition> _All = new List<SharedCaseDisposition>
        {
            SharedCaseDisposition.DuplicateOf("E2E.LandingPipeline.pull_request_open_transitions_to_complete",
                AutomatedSuites + "LandingPipelineTests.cs", "PullRequestOpen_RejectsCompleteWithoutLandingProof", ManualCompletionProof),
            SharedCaseDisposition.DuplicateOf("E2E.LandingPipeline.manual_complete_no_dock_emits_audit_event",
                AutomatedSuites + "LandingPipelineTests.cs", "ManualComplete_NoDock_RejectsUnlandedCode", ManualCompletionProof),
            SharedCaseDisposition.DuplicateOf("E2E.McpTool.armada_enumerate_missions_with_status_filter",
                AutomatedSuites + "McpToolTests.cs", "ArmadaEnumerate_Missions_WithStatusFilter", "idle captains claim the copy's assignable missions before the Pending filter runs"),
            SharedCaseDisposition.DuplicateOf("E2E.McpTool.armada_purge_voyage_deletes_voyage_and_missions",
                AutomatedSuites + "McpToolTests.cs", "ArmadaPurgeVoyage_DeletesVoyageAndMissions", "voyage status returns slim mission records whose description is a truncation envelope"),
            SharedCaseDisposition.DuplicateOf("E2E.Objective.objectives_voyage_and_release_creation_link_back_to_objective",
                AutomatedSuites + "ObjectiveTests.cs", "Objectives_VoyageAndReleaseCreationLinkBackToObjective", "linkage needs a real local Git vessel, a workflow profile and verified cleanup"),
            SharedCaseDisposition.DuplicateOf("E2E.Release.create_list_read_update_refresh_and_delete",
                AutomatedSuites + "ReleaseTests.cs", "Releases_CreateListReadUpdateRefreshAndDelete", "release creation needs a real local Git repository URL"),
            SharedCaseDisposition.DuplicateOf("E2E.WebSocket.transition_mission_status_to_complete_sets_completed_utc",
                AutomatedSuites + "WebSocketTests.cs", "TransitionMissionStatus_ToComplete_SetsCompletedUtc", "manual completion now requires landing proof, which a report-only mission satisfies"),
            SharedCaseDisposition.DuplicateOf("E2E.Workflow.voyage_workflow_create_and_cancel",
                AutomatedSuites + "WorkflowTests.cs", "VoyageWorkflow_CreateAndCancel", "a dispatched voyage runs as Open, not InProgress"),
            SharedCaseDisposition.DuplicateOf("E2E.WorkflowProfileCheckRun.check_runs_run_read_retry_list_and_delete",
                AutomatedSuites + "WorkflowProfileCheckRunTests.cs", "CheckRuns_RunReadRetryListAndDelete", CommittedCheckFixture),
            SharedCaseDisposition.DuplicateOf("E2E.WorkflowProfileCheckRun.check_runs_run_parses_structured_summaries",
                AutomatedSuites + "WorkflowProfileCheckRunTests.cs", "CheckRuns_RunParsesStructuredSummaries", CommittedCheckFixture),

            SharedCaseDisposition.DuplicateOf("Runtimes.ClaudeCodeRuntime.delivers_prompt_via_stdin_not_argument",
                RuntimeSuites + "ClaudeCodeRuntimeTests.cs", "BuildArguments_PromptContainsRolePreamble", PromptFinalArgument),
            SharedCaseDisposition.DuplicateOf("Runtimes.ClaudeCodeRuntime.build_arguments_omits_stream_json_by_default",
                RuntimeSuites + "ClaudeCodeRuntimeTests.cs", "BuildArguments Includes Model When Supplied", "the fork always requests stream-json output"),
            SharedCaseDisposition.DuplicateOf("Runtimes.CodexRuntime.build_arguments_uses_exec_with_platform_appropriate_auto_mode",
                RuntimeSuites + "CodexRuntimeTests.cs", "BuildArguments Uses Exec With Platform Appropriate Auto Mode", "the sandbox bypass applies on Linux as well as Windows, and the prompt is the final argument"),
            SharedCaseDisposition.DuplicateOf("Runtimes.CodexRuntime.build_arguments_dangerous_uses_dangerous_flag",
                RuntimeSuites + "CodexRuntimeTests.cs", "BuildArguments Dangerous Uses Dangerous Flag", PromptFinalArgument),
            SharedCaseDisposition.DuplicateOf("Runtimes.CodexRuntime.delivers_prompt_via_stdin_not_argument",
                RuntimeSuites + "CodexRuntimeTests.cs", "BuildArguments_PromptContainsRolePreamble", PromptFinalArgument),
            SharedCaseDisposition.DuplicateOf("Runtimes.CursorRuntime.build_arguments_uses_non_interactive_text_output",
                RuntimeSuites + "CursorRuntimeTests.cs", "BuildArguments Uses NonInteractiveStructuredOutput", "the fork runs cursor-agent with --print and stream-json output"),
            SharedCaseDisposition.DuplicateOf("Runtimes.GeminiRuntime.build_arguments_uses_approval_mode_and_stdin_prompt",
                RuntimeSuites + "GeminiRuntimeTests.cs", "BuildArguments Uses Prompt And ApprovalMode", "the fork passes the prompt with -p and requests stream-json output"),
            SharedCaseDisposition.DuplicateOf("Runtimes.MuxRuntime.build_arguments_includes_endpoint_config_and_final_message_artifact",
                RuntimeSuites + "MuxRuntimeTests.cs", "BuildArguments Uses Current Mux Run Contract", "the fork targets the current Mux run contract"),

            SharedCaseDisposition.DuplicateOf("Services.AdmiralService.handle_process_exit_async_stalls_captain_on_non_retryable_runtime_failure",
                UnitServiceSuites + "AdmiralServiceTests.cs", "HandleProcessExitAsync RateLimitFailure RequeuesMissionWithoutHaltingVoyage", "a provider limit now requeues the mission and quarantines the captain instead of failing the voyage"),
            SharedCaseDisposition.DuplicateOf("Services.AuthenticationService.apikey_valid_key_authenticated",
                UnitServiceSuites + "AuthenticationServiceTests.cs", "AuthenticateAsync ApiKey ValidKey ReturnsAuthenticated", "API-key authentication resolves to the default tenant, not the system tenant"),
            SharedCaseDisposition.DuplicateOf("Services.GitService.create_worktree_async_dirty_tracked_files_throws_and_cleans_up",
                UnitServiceSuites + "GitServiceTests.cs", "CreateWorktreeAsync DirtyTrackedFiles Throws And Cleans Up", "git runs the fixture hook only when it is executable on Unix"),
            SharedCaseDisposition.DuplicateOf("Services.MissionPrompt.template_resolved_claude_md_de_duplicates_shared_context_sections",
                UnitServiceSuites + "MissionPromptTests.cs", "Template-resolved CLAUDE.md de-duplicates shared context sections", "generated instructions no longer carry a Model Context section in this template"),
            SharedCaseDisposition.DuplicateOf("Services.MissionPrompt.template_resolved_persona_prompts_require_structured_test_and_judge_analysis",
                UnitServiceSuites + "MissionPromptTests.cs", "Template-resolved persona prompts require structured test and judge analysis", "generated instructions are written under .armada/instructions"),
            SharedCaseDisposition.DuplicateOf("Services.MissionPrompt.generate_claude_md_async_strips_stale_armada_mission_blocks_from_existing_instructions",
                UnitServiceSuites + "MissionPromptTests.cs", "GenerateClaudeMdAsync strips stale Armada mission blocks from existing instructions", "generated instructions are written under .armada/instructions"),
            SharedCaseDisposition.DuplicateOf("Services.MissionStatusTransition.all_expected_statuses_defined",
                UnitServiceSuites + "MissionStatusTransitionTests.cs", "All expected statuses defined", "the mission status set includes WaitingForInput"),
            SharedCaseDisposition.DuplicateOf("Services.PipelineDispatch.architect_fan_out_clones_full_downstream_chain_and_lands_only_terminal_stage",
                UnitServiceSuites + "PipelineDispatchTests.cs", "Architect fan-out clones full downstream chain and lands only terminal stage", "Architect-derived briefs carry the plan-block label rule and stages need verified completion"),
            SharedCaseDisposition.DuplicateOf("Services.PipelineDispatch.architect_fan_out_honors_explicit_mission_dependencies_across_worker_chains",
                UnitServiceSuites + "PipelineDispatchTests.cs", "Architect fan-out honors explicit mission dependencies across worker chains", VerifiedCompletion),
            SharedCaseDisposition.DuplicateOf("Services.PipelineDispatch.judge_parser_accepts_structured_armada_verdict_signal",
                UnitServiceSuites + "PipelineDispatchTests.cs", "Judge parser accepts structured ARMADA verdict signal", VerifiedCompletion),
            SharedCaseDisposition.DuplicateOf("Services.PipelineDispatch.judge_parser_accepts_markdown_verdict_heading_emitted_by_claude",
                UnitServiceSuites + "PipelineDispatchTests.cs", "Judge parser accepts markdown verdict heading emitted by Claude", VerifiedCompletion),
            SharedCaseDisposition.DuplicateOf("Services.PipelineDispatch.judge_parser_accepts_inline_sentence_verdict_emitted_by_claude",
                UnitServiceSuites + "PipelineDispatchTests.cs", "Judge parser accepts inline sentence verdict emitted by Claude", VerifiedCompletion),
            SharedCaseDisposition.DuplicateOf("Services.ReleaseVersion.source_mcp_helpers_use_net10_framework",
                UnitServiceSuites + "ReleaseVersionTests.cs", "Source MCP Helpers Use Net10 Framework", "the MCP helper scripts call armada_resolve_framework"),
            SharedCaseDisposition.DuplicateOf("Services.WorkflowProfileCheckRunService.run_async_executes_workflow_profile_command_and_collects_artifacts",
                UnitServiceSuites + "WorkflowProfileCheckRunServiceTests.cs", "RunAsync executes workflow profile command and collects artifacts", CommittedCheckFixture),
            SharedCaseDisposition.DuplicateOf("Services.WorkflowProfileCheckRunService.retry_async_re_executes_prior_check_run",
                UnitServiceSuites + "WorkflowProfileCheckRunServiceTests.cs", "RetryAsync re-executes prior check run", CommittedCheckFixture),
            SharedCaseDisposition.DuplicateOf("Services.WorkflowProfileCheckRunService.run_async_parses_structured_test_and_coverage_summaries",
                UnitServiceSuites + "WorkflowProfileCheckRunServiceTests.cs", "RunAsync parses structured test and coverage summaries", CommittedCheckFixture),

            SharedCaseDisposition.ForkDifference("Services.MissionPrompt.generate_claude_md_async_includes_model_context_when_enabled_and_set",
                "generated instructions are written under .armada/instructions and carry no Model Context Updates section; " + ForkKeepsInstructionSections),
            SharedCaseDisposition.ForkDifference("Services.MissionPrompt.generate_claude_md_async_includes_update_instructions_even_when_model_context_is_empty",
                "generated instructions carry no Model Context Updates section; " + ForkKeepsInstructionSections),
            SharedCaseDisposition.ForkDifference("Services.MissionPrompt.template_resolved_claude_md_contains_model_context_updates_when_enabled",
                "generated instructions carry no Model Context Updates section; " + ForkKeepsInstructionSections),
            SharedCaseDisposition.ForkDifference("Services.PersonaSeedService.seed_async_creates_new_built_in_personas_and_expanded_full_pipeline",
                "the fork seeds FullPipeline as Architect, Worker, TestEngineer, Judge and seeds the persona as TestEngineer; " + ForkKeepsSeededPersonas),
            SharedCaseDisposition.ForkDifference("Services.PersonaSeedService.seed_async_upgrades_the_legacy_built_in_full_pipeline_order",
                "the fork does not upgrade FullPipeline to the expanded product persona order; " + ForkKeepsSeededPersonas),
            SharedCaseDisposition.ForkDifference("Services.PersonaSeedService.seed_async_renames_the_legacy_built_in_test_engineer_persona",
                "the fork does not rename the built-in TestEngineer persona to Test Engineer; " + ForkKeepsSeededPersonas),
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Apply dispositions to discovered suites. A case named by a disposition becomes a skip carrying the
        /// disposition's reason; every other case is unchanged.
        /// </summary>
        /// <param name="suites">Discovered suites.</param>
        /// <param name="dispositions">Dispositions to apply.</param>
        /// <param name="repositoryRoot">Repository root used to verify legacy owners, or null when the sources are not present.</param>
        /// <returns>The suites with dispositions applied.</returns>
        /// <exception cref="InvalidOperationException">A disposition is repeated, names no discovered case, or names a legacy owner that does not exist.</exception>
        public static IReadOnlyList<TestSuiteDescriptor> Apply(
            IReadOnlyList<TestSuiteDescriptor> suites,
            IReadOnlyList<SharedCaseDisposition> dispositions,
            string? repositoryRoot)
        {
            if (suites == null) throw new ArgumentNullException(nameof(suites));
            if (dispositions == null) throw new ArgumentNullException(nameof(dispositions));

            List<string> problems = new List<string>();
            Dictionary<string, SharedCaseDisposition> byTestId = new Dictionary<string, SharedCaseDisposition>(StringComparer.Ordinal);
            foreach (SharedCaseDisposition disposition in dispositions)
            {
                if (!byTestId.TryAdd(disposition.TestId, disposition))
                    problems.Add(disposition.TestId + " is recorded more than once");
            }

            HashSet<string> matched = new HashSet<string>(StringComparer.Ordinal);
            List<TestSuiteDescriptor> result = new List<TestSuiteDescriptor>();
            foreach (TestSuiteDescriptor suite in suites)
            {
                List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
                foreach (TestCaseDescriptor testCase in suite.Cases)
                {
                    if (!byTestId.TryGetValue(testCase.TestId, out SharedCaseDisposition? disposition) || testCase.Skip)
                    {
                        cases.Add(testCase);
                        continue;
                    }

                    matched.Add(testCase.TestId);
                    cases.Add(new TestCaseDescriptor(
                        suiteId: testCase.SuiteId,
                        caseId: testCase.CaseId,
                        displayName: testCase.DisplayName,
                        executeAsync: testCase.ExecuteAsync,
                        tags: testCase.Tags,
                        skip: true,
                        skipReason: disposition.SkipReason));
                }

                result.Add(new TestSuiteDescriptor(suite.SuiteId, suite.DisplayName, cases, suite.BeforeSuiteAsync, suite.AfterSuiteAsync));
            }

            foreach (SharedCaseDisposition disposition in byTestId.Values)
            {
                if (!matched.Contains(disposition.TestId)
                    && !suites.Any(suite => suite.Cases.Any(testCase => testCase.TestId == disposition.TestId)))
                {
                    problems.Add(disposition.TestId + " names no discovered case");
                }

                if (disposition.Kind != SharedCaseDispositionKindEnum.DuplicateOfLegacyCase || repositoryRoot == null) continue;

                string ownerPath = Path.Combine(repositoryRoot, disposition.OwnerFile!);
                if (!File.Exists(ownerPath))
                    problems.Add(disposition.TestId + " names legacy owner file " + disposition.OwnerFile + ", which does not exist");
                else if (!File.ReadAllText(ownerPath).Contains("\"" + disposition.OwnerCase + "\"", StringComparison.Ordinal))
                    problems.Add(disposition.TestId + " names legacy owner case '" + disposition.OwnerCase + "', which " + disposition.OwnerFile + " does not register");
            }

            if (problems.Count > 0)
                throw new InvalidOperationException("Shared case dispositions are stale: " + String.Join("; ", problems));

            return result;
        }

        /// <summary>
        /// Locate the repository root by walking up from the runner's base directory to the directory holding
        /// <c>src/Armada.sln</c>.
        /// </summary>
        /// <returns>The repository root, or null when the runner executes outside a source checkout.</returns>
        public static string? FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Armada.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            return null;
        }

        #endregion
    }
}
