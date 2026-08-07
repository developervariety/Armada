namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ArmadaEvent"/>: id generation and validation, constructor
    /// behavior, default values, and JSON serialization round-tripping.
    /// </summary>
    public sealed class ArmadaEventSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the ArmadaEvent model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix", "ArmadaEvent default constructor generates id with prefix", TestTags.Positive, () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertStartsWith("evt_", evt.Id);
            }));

            cases.Add(Case("type_message_constructor_sets_properties", "ArmadaEvent type/message constructor sets properties", TestTags.Positive, () =>
            {
                ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created");
                AssertEqual("mission.created", evt.EventType);
                AssertEqual("Mission created", evt.Message);
            }));

            cases.Add(Case("default_values_are_correct", "ArmadaEvent default values are correct", TestTags.Positive, () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertEqual("", evt.EventType);
                AssertEqual("", evt.Message);
                AssertNull(evt.EntityType);
                AssertNull(evt.EntityId);
                AssertNull(evt.CaptainId);
                AssertNull(evt.MissionId);
                AssertNull(evt.VesselId);
                AssertNull(evt.VoyageId);
                AssertNull(evt.Payload);
            }));

            cases.Add(Case("set_id_null_throws", "ArmadaEvent set id null throws", TestTags.Negative, () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertThrows<ArgumentNullException>(() => evt.Id = null!);
            }));

            cases.Add(Case("set_id_empty_throws", "ArmadaEvent set id empty throws", TestTags.Negative, () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertThrows<ArgumentNullException>(() => evt.Id = "");
            }));

            cases.Add(Case("serialization_round_trip", "ArmadaEvent serialization round trip", TestTags.Positive, () =>
            {
                ArmadaEvent evt = new ArmadaEvent("captain.launched", "Captain launched");
                evt.CaptainId = "cpt_test";
                evt.MissionId = "msn_test";
                evt.VesselId = "vsl_test";
                evt.VoyageId = "vyg_test";
                evt.EntityType = "captain";
                evt.EntityId = "cpt_test";
                evt.Payload = "{\"processId\":12345}";

                string json = JsonSerializer.Serialize(evt);
                ArmadaEvent deserialized = JsonSerializer.Deserialize<ArmadaEvent>(json)!;

                AssertEqual(evt.Id, deserialized.Id);
                AssertEqual(evt.EventType, deserialized.EventType);
                AssertEqual(evt.Message, deserialized.Message);
                AssertEqual(evt.CaptainId, deserialized.CaptainId);
                AssertEqual(evt.Payload, deserialized.Payload);
            }));

            cases.Add(Case("unique_ids_across_instances", "ArmadaEvent unique ids across instances", TestTags.Positive, () =>
            {
                ArmadaEvent e1 = new ArmadaEvent();
                ArmadaEvent e2 = new ArmadaEvent();
                AssertNotEqual(e1.Id, e2.Id);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.ArmadaEvent",
                displayName: "ArmadaEvent Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.ArmadaEvent",
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
                suiteId: "Models.ArmadaEvent",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
