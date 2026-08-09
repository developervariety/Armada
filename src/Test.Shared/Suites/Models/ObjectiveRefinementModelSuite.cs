namespace Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ObjectiveRefinementSession"/> and
    /// <see cref="ObjectiveRefinementMessage"/>: constructor defaults and JSON round-tripping.
    /// These models expose plain get/set properties with no validation, so coverage is
    /// entirely positive.
    /// </summary>
    public sealed class ObjectiveRefinementModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the objective refinement models suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("session_default_constructor_sets_expected_defaults", "ObjectiveRefinementSession default constructor sets expected defaults", TestTags.Positive, () =>
            {
                ObjectiveRefinementSession session = new ObjectiveRefinementSession();

                AssertStartsWith("ors_", session.Id);
                AssertEqual(String.Empty, session.ObjectiveId);
                AssertEqual(String.Empty, session.CaptainId);
                AssertEqual("Objective Refinement", session.Title);
                AssertEqual(ObjectiveRefinementSessionStatusEnum.Created, session.Status);
                AssertNull(session.ProcessId);
                AssertNull(session.StartedUtc);
                AssertNull(session.CompletedUtc);
            }));

            cases.Add(Case("message_default_constructor_sets_expected_defaults", "ObjectiveRefinementMessage default constructor sets expected defaults", TestTags.Positive, () =>
            {
                ObjectiveRefinementMessage message = new ObjectiveRefinementMessage();

                AssertStartsWith("orm_", message.Id);
                AssertEqual(String.Empty, message.ObjectiveRefinementSessionId);
                AssertEqual(String.Empty, message.ObjectiveId);
                AssertEqual("User", message.Role);
                AssertEqual(1, message.Sequence);
                AssertEqual(String.Empty, message.Content);
                AssertFalse(message.IsSelected);
            }));

            cases.Add(Case("serialize_and_deserialize", "ObjectiveRefinementModels serialize and deserialize", TestTags.Positive, () =>
            {
                ObjectiveRefinementSession session = new ObjectiveRefinementSession
                {
                    Id = "ors_roundtrip",
                    ObjectiveId = "obj_roundtrip",
                    TenantId = "ten_roundtrip",
                    UserId = "usr_roundtrip",
                    CaptainId = "cpt_roundtrip",
                    FleetId = "flt_roundtrip",
                    VesselId = "ves_roundtrip",
                    Title = "Refine backlog item",
                    Status = ObjectiveRefinementSessionStatusEnum.Completed,
                    ProcessId = 42,
                    FailureReason = "none"
                };

                ObjectiveRefinementMessage message = new ObjectiveRefinementMessage
                {
                    Id = "orm_roundtrip",
                    ObjectiveRefinementSessionId = session.Id,
                    ObjectiveId = session.ObjectiveId,
                    TenantId = session.TenantId,
                    UserId = session.UserId,
                    Role = "Assistant",
                    Sequence = 2,
                    Content = "Refined response",
                    IsSelected = true
                };

                ObjectiveRefinementSession sessionRoundTrip = JsonSerializer.Deserialize<ObjectiveRefinementSession>(JsonSerializer.Serialize(session))!;
                ObjectiveRefinementMessage messageRoundTrip = JsonSerializer.Deserialize<ObjectiveRefinementMessage>(JsonSerializer.Serialize(message))!;

                AssertEqual(session.Id, sessionRoundTrip.Id);
                AssertEqual(session.ObjectiveId, sessionRoundTrip.ObjectiveId);
                AssertEqual(session.CaptainId, sessionRoundTrip.CaptainId);
                AssertEqual(session.Status, sessionRoundTrip.Status);
                AssertEqual(session.ProcessId, sessionRoundTrip.ProcessId);
                AssertEqual(message.Id, messageRoundTrip.Id);
                AssertEqual(message.ObjectiveRefinementSessionId, messageRoundTrip.ObjectiveRefinementSessionId);
                AssertEqual(message.Role, messageRoundTrip.Role);
                AssertEqual(message.Sequence, messageRoundTrip.Sequence);
                AssertEqual(message.Content, messageRoundTrip.Content);
                AssertEqual(message.IsSelected, messageRoundTrip.IsSelected);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.ObjectiveRefinementModel",
                displayName: "Objective Refinement Models",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.ObjectiveRefinementModel",
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
                suiteId: "Models.ObjectiveRefinementModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
