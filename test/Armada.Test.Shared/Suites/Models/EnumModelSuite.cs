namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors verifying that Armada enums serialize as their string names and round-trip
    /// back through <see cref="JsonSerializer"/> (driven by <c>JsonStringEnumConverter</c>).
    /// </summary>
    public sealed class EnumModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the enum serialization suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // MissionStatusEnum
            cases.Add(Case("mission_status_pending_serializes_as_string", "MissionStatusEnum Pending serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Pending);
                AssertEqual("\"Pending\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Pending, deserialized);
            }));

            cases.Add(Case("mission_status_assigned_serializes_as_string", "MissionStatusEnum Assigned serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Assigned);
                AssertEqual("\"Assigned\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Assigned, deserialized);
            }));

            cases.Add(Case("mission_status_in_progress_serializes_as_string", "MissionStatusEnum InProgress serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.InProgress);
                AssertEqual("\"InProgress\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.InProgress, deserialized);
            }));

            cases.Add(Case("mission_status_testing_serializes_as_string", "MissionStatusEnum Testing serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Testing);
                AssertEqual("\"Testing\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Testing, deserialized);
            }));

            cases.Add(Case("mission_status_review_serializes_as_string", "MissionStatusEnum Review serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Review);
                AssertEqual("\"Review\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Review, deserialized);
            }));

            cases.Add(Case("mission_status_complete_serializes_as_string", "MissionStatusEnum Complete serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Complete);
                AssertEqual("\"Complete\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Complete, deserialized);
            }));

            cases.Add(Case("mission_status_failed_serializes_as_string", "MissionStatusEnum Failed serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Failed);
                AssertEqual("\"Failed\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Failed, deserialized);
            }));

            cases.Add(Case("mission_status_cancelled_serializes_as_string", "MissionStatusEnum Cancelled serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.Cancelled);
                AssertEqual("\"Cancelled\"", json);
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.Cancelled, deserialized);
            }));

            // CaptainStateEnum
            cases.Add(Case("captain_state_idle_serializes_as_string", "CaptainStateEnum Idle serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(CaptainStateEnum.Idle);
                AssertEqual("\"Idle\"", json);
                CaptainStateEnum deserialized = JsonSerializer.Deserialize<CaptainStateEnum>(json);
                AssertEqual(CaptainStateEnum.Idle, deserialized);
            }));

            cases.Add(Case("captain_state_working_serializes_as_string", "CaptainStateEnum Working serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(CaptainStateEnum.Working);
                AssertEqual("\"Working\"", json);
                CaptainStateEnum deserialized = JsonSerializer.Deserialize<CaptainStateEnum>(json);
                AssertEqual(CaptainStateEnum.Working, deserialized);
            }));

            cases.Add(Case("captain_state_stalled_serializes_as_string", "CaptainStateEnum Stalled serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(CaptainStateEnum.Stalled);
                AssertEqual("\"Stalled\"", json);
                CaptainStateEnum deserialized = JsonSerializer.Deserialize<CaptainStateEnum>(json);
                AssertEqual(CaptainStateEnum.Stalled, deserialized);
            }));

            cases.Add(Case("captain_state_stopping_serializes_as_string", "CaptainStateEnum Stopping serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(CaptainStateEnum.Stopping);
                AssertEqual("\"Stopping\"", json);
                CaptainStateEnum deserialized = JsonSerializer.Deserialize<CaptainStateEnum>(json);
                AssertEqual(CaptainStateEnum.Stopping, deserialized);
            }));

            // SignalTypeEnum
            cases.Add(Case("signal_type_assignment_serializes_as_string", "SignalTypeEnum Assignment serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Assignment);
                AssertEqual("\"Assignment\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Assignment, deserialized);
            }));

            cases.Add(Case("signal_type_progress_serializes_as_string", "SignalTypeEnum Progress serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Progress);
                AssertEqual("\"Progress\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Progress, deserialized);
            }));

            cases.Add(Case("signal_type_completion_serializes_as_string", "SignalTypeEnum Completion serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Completion);
                AssertEqual("\"Completion\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Completion, deserialized);
            }));

            cases.Add(Case("signal_type_error_serializes_as_string", "SignalTypeEnum Error serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Error);
                AssertEqual("\"Error\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Error, deserialized);
            }));

            cases.Add(Case("signal_type_heartbeat_serializes_as_string", "SignalTypeEnum Heartbeat serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Heartbeat);
                AssertEqual("\"Heartbeat\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Heartbeat, deserialized);
            }));

            cases.Add(Case("signal_type_nudge_serializes_as_string", "SignalTypeEnum Nudge serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Nudge);
                AssertEqual("\"Nudge\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Nudge, deserialized);
            }));

            cases.Add(Case("signal_type_mail_serializes_as_string", "SignalTypeEnum Mail serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(SignalTypeEnum.Mail);
                AssertEqual("\"Mail\"", json);
                SignalTypeEnum deserialized = JsonSerializer.Deserialize<SignalTypeEnum>(json);
                AssertEqual(SignalTypeEnum.Mail, deserialized);
            }));

            // AgentRuntimeEnum
            cases.Add(Case("agent_runtime_claude_code_serializes_as_string", "AgentRuntimeEnum ClaudeCode serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(AgentRuntimeEnum.ClaudeCode);
                AssertEqual("\"ClaudeCode\"", json);
                AgentRuntimeEnum deserialized = JsonSerializer.Deserialize<AgentRuntimeEnum>(json);
                AssertEqual(AgentRuntimeEnum.ClaudeCode, deserialized);
            }));

            cases.Add(Case("agent_runtime_codex_serializes_as_string", "AgentRuntimeEnum Codex serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(AgentRuntimeEnum.Codex);
                AssertEqual("\"Codex\"", json);
                AgentRuntimeEnum deserialized = JsonSerializer.Deserialize<AgentRuntimeEnum>(json);
                AssertEqual(AgentRuntimeEnum.Codex, deserialized);
            }));

            cases.Add(Case("agent_runtime_custom_serializes_as_string", "AgentRuntimeEnum Custom serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(AgentRuntimeEnum.Custom);
                AssertEqual("\"Custom\"", json);
                AgentRuntimeEnum deserialized = JsonSerializer.Deserialize<AgentRuntimeEnum>(json);
                AssertEqual(AgentRuntimeEnum.Custom, deserialized);
            }));

            // VoyageStatusEnum
            cases.Add(Case("voyage_status_open_serializes_as_string", "VoyageStatusEnum Open serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(VoyageStatusEnum.Open);
                AssertEqual("\"Open\"", json);
                VoyageStatusEnum deserialized = JsonSerializer.Deserialize<VoyageStatusEnum>(json);
                AssertEqual(VoyageStatusEnum.Open, deserialized);
            }));

            cases.Add(Case("voyage_status_in_progress_serializes_as_string", "VoyageStatusEnum InProgress serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(VoyageStatusEnum.InProgress);
                AssertEqual("\"InProgress\"", json);
                VoyageStatusEnum deserialized = JsonSerializer.Deserialize<VoyageStatusEnum>(json);
                AssertEqual(VoyageStatusEnum.InProgress, deserialized);
            }));

            cases.Add(Case("voyage_status_complete_serializes_as_string", "VoyageStatusEnum Complete serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(VoyageStatusEnum.Complete);
                AssertEqual("\"Complete\"", json);
                VoyageStatusEnum deserialized = JsonSerializer.Deserialize<VoyageStatusEnum>(json);
                AssertEqual(VoyageStatusEnum.Complete, deserialized);
            }));

            cases.Add(Case("voyage_status_failed_serializes_as_string", "VoyageStatusEnum Failed serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(VoyageStatusEnum.Failed);
                AssertEqual("\"Failed\"", json);
                VoyageStatusEnum deserialized = JsonSerializer.Deserialize<VoyageStatusEnum>(json);
                AssertEqual(VoyageStatusEnum.Failed, deserialized);
            }));

            cases.Add(Case("voyage_status_cancelled_serializes_as_string", "VoyageStatusEnum Cancelled serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(VoyageStatusEnum.Cancelled);
                AssertEqual("\"Cancelled\"", json);
                VoyageStatusEnum deserialized = JsonSerializer.Deserialize<VoyageStatusEnum>(json);
                AssertEqual(VoyageStatusEnum.Cancelled, deserialized);
            }));

            // PlaybookDeliveryModeEnum
            cases.Add(Case("playbook_delivery_mode_inline_full_content_serializes_as_string", "PlaybookDeliveryModeEnum InlineFullContent serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(PlaybookDeliveryModeEnum.InlineFullContent);
                AssertEqual("\"InlineFullContent\"", json);
                PlaybookDeliveryModeEnum deserialized = JsonSerializer.Deserialize<PlaybookDeliveryModeEnum>(json);
                AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, deserialized);
            }));

            cases.Add(Case("playbook_delivery_mode_instruction_with_reference_serializes_as_string", "PlaybookDeliveryModeEnum InstructionWithReference serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(PlaybookDeliveryModeEnum.InstructionWithReference);
                AssertEqual("\"InstructionWithReference\"", json);
                PlaybookDeliveryModeEnum deserialized = JsonSerializer.Deserialize<PlaybookDeliveryModeEnum>(json);
                AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, deserialized);
            }));

            cases.Add(Case("playbook_delivery_mode_attach_into_worktree_serializes_as_string", "PlaybookDeliveryModeEnum AttachIntoWorktree serializes as string", TestTags.Positive, () =>
            {
                string json = JsonSerializer.Serialize(PlaybookDeliveryModeEnum.AttachIntoWorktree);
                AssertEqual("\"AttachIntoWorktree\"", json);
                PlaybookDeliveryModeEnum deserialized = JsonSerializer.Deserialize<PlaybookDeliveryModeEnum>(json);
                AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, deserialized);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.EnumModel",
                displayName: "Enum Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.EnumModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.EnumModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
