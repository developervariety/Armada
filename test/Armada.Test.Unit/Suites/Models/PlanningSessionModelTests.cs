namespace Armada.Test.Unit.Suites.Models
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class PlanningSessionModelTests : TestSuite
    {
        public override string Name => "Planning Session Model";

        protected override async Task RunTestsAsync()
        {
            await RunTest("PlanningSession DefaultConstructor GeneratesIdWithPrefix", () =>
            {
                PlanningSession session = new PlanningSession();
                AssertStartsWith(Constants.PlanningSessionIdPrefix, session.Id);
                AssertEqual(PlanningSessionStatusEnum.Created, session.Status);
            });

            await RunTest("PlanningSession SerializeSelectedPlaybooks RoundTrips", () =>
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
            });

            await RunTest("PlanningSessionMessage Serialization RoundTrip", () =>
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
            });
        }
    }
}
