namespace Armada.Test.Unit.Suites.Models
{
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class ObjectiveRefinementModelTests : TestSuite
    {
        public override string Name => "Objective Refinement Models";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ObjectiveRefinementSession DefaultConstructor SetsExpectedDefaults", () =>
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
            });

            await RunTest("ObjectiveRefinementMessage DefaultConstructor SetsExpectedDefaults", () =>
            {
                ObjectiveRefinementMessage message = new ObjectiveRefinementMessage();

                AssertStartsWith("orm_", message.Id);
                AssertEqual(String.Empty, message.ObjectiveRefinementSessionId);
                AssertEqual(String.Empty, message.ObjectiveId);
                AssertEqual("User", message.Role);
                AssertEqual(1, message.Sequence);
                AssertEqual(String.Empty, message.Content);
                AssertFalse(message.IsSelected);
            });

            await RunTest("ObjectiveRefinementModels SerializeAndDeserialize", () =>
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
            });
        }
    }
}
