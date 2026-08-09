namespace Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="PlanningSession"/> and <see cref="PlanningSessionMessage"/>:
    /// identity generation, selected-playbook serialization helpers, and message round-tripping.
    /// Ported from the retired unit suite plus added Id-validation negatives.
    /// </summary>
    public sealed class PlanningSessionModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the PlanningSession model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "PlanningSession default constructor generates id with prefix", TestTags.Positive, () =>
            {
                PlanningSession session = new PlanningSession();
                AssertStartsWith(Constants.PlanningSessionIdPrefix, session.Id);
                AssertEqual(PlanningSessionStatusEnum.Created, session.Status);
            }));

            cases.Add(Case("serialize_selected_playbooks_round_trips", "PlanningSession serialize selected playbooks round trips", TestTags.Positive, () =>
            {
                PlanningSession session = new PlanningSession();
                session.SelectedPlaybooks.Add(new SelectedPlaybook
                {
                    PlaybookId = "plb_123",
                    DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree
                });

                string json = session.SerializeSelectedPlaybooks();

                PlanningSession rehydrated = new PlanningSession();
                rehydrated.DeserializeSelectedPlaybooks(json);

                AssertEqual(1, rehydrated.SelectedPlaybooks.Count);
                AssertEqual("plb_123", rehydrated.SelectedPlaybooks[0].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, rehydrated.SelectedPlaybooks[0].DeliveryMode);
            }));

            cases.Add(Case("message_serialization_round_trip", "PlanningSessionMessage serialization round trip", TestTags.Positive, () =>
            {
                PlanningSessionMessage message = new PlanningSessionMessage
                {
                    PlanningSessionId = "psn_test",
                    Role = "Assistant",
                    Sequence = 2,
                    Content = "Dispatch draft",
                    IsSelectedForDispatch = true
                };

                string json = JsonSerializer.Serialize(message);
                PlanningSessionMessage deserialized = JsonSerializer.Deserialize<PlanningSessionMessage>(json)!;

                AssertStartsWith(Constants.PlanningSessionMessageIdPrefix, deserialized.Id);
                AssertEqual(message.PlanningSessionId, deserialized.PlanningSessionId);
                AssertEqual(message.Role, deserialized.Role);
                AssertEqual(message.Sequence, deserialized.Sequence);
                AssertEqual(message.Content, deserialized.Content);
                AssertTrue(deserialized.IsSelectedForDispatch);
            }));

            // Added audit coverage: both Id setters reject null/empty, never exercised by the legacy suite.
            cases.Add(Case("set_id_null_throws", "PlanningSession set id null throws", TestTags.Negative, () =>
            {
                PlanningSession session = new PlanningSession();
                AssertThrows<ArgumentNullException>(() => session.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "PlanningSession set id empty throws", TestTags.Negative, () =>
            {
                PlanningSession session = new PlanningSession();
                AssertThrows<ArgumentNullException>(() => session.Id = "");
            }));

            cases.Add(Case("message_set_id_null_throws", "PlanningSessionMessage set id null throws", TestTags.Negative, () =>
            {
                PlanningSessionMessage message = new PlanningSessionMessage();
                AssertThrows<ArgumentNullException>(() => message.Id = null!);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.PlanningSessionModel",
                displayName: "Planning Session Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.PlanningSessionModel",
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
                suiteId: "Models.PlanningSessionModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
