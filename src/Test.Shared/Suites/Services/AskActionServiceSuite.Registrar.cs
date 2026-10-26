namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskActionServiceSuite
    {
        private static async Task RegistrarNormalizesBeforeProposalGateAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Scope vessel", "https://example.invalid/scope.git")
                {
                    TenantId = fixture.Owner.TenantId,
                    UserId = fixture.Owner.UserId
                }).ConfigureAwait(false);
                string abbreviatedId = vessel.Id.Substring(0, 12);
                Armada.Core.Services.Interfaces.IAdmiralService admiral = DispatchProxy.Create<Armada.Core.Services.Interfaces.IAdmiralService, RecordingAdmiral>();
                RecordingAdmiral proxy = (RecordingAdmiral)(object)admiral;
                LongRunningJobService jobs = new LongRunningJobService();
                AskActionService actions = CreateService(testDb.Driver, jobs);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);

                McpToolRegistrar.RegisterAll(
                    (name, description, schema, handler) => handlers[name] = handler,
                    testDb.Driver,
                    admiral,
                    settings: new ArmadaSettings(),
                    logging: CreateLogging(),
                    longRunningJobs: jobs,
                    askActions: actions);

                AssertTrue(handlers.TryGetValue("armada_create_mission", out Func<JsonElement?, Task<object>>? registered));
                object result = await CallAsync(registered!, ScopedCaller(fixture), Args("{\"title\":\"normalized\",\"description\":\"test\",\"vesselId\":\"" + abbreviatedId + "\"}")).ConfigureAwait(false);
                AssertContains("ask_action_proposed", JsonSerializer.Serialize(result));
                AssertEqual(0, proxy.DispatchMissionCalls, "the registered mutation handler does not run before owner approval");

                List<AskActionProposal> proposals = await actions.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false) ?? new List<AskActionProposal>();
                AssertEqual(1, proposals.Count);
                SavedMissionArguments? savedArguments = JsonSerializer.Deserialize<SavedMissionArguments>(proposals[0].ArgumentsJson);
                AssertEqual(vessel.Id, savedArguments!.VesselId, "the proposal stores the full id resolved by the registrar wrapper");
                AssertEqual(AskProposalStateEnum.Pending, proposals[0].State);
            }
        }

        /// <summary>Captures any dispatch that escapes the proposal gate.</summary>
        public class RecordingAdmiral : DispatchProxy
        {
            /// <summary>Number of mission dispatches seen by this test double.</summary>
            public int DispatchMissionCalls { get; private set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == "DispatchMissionAsync")
                {
                    DispatchMissionCalls++;
                    return Task.FromResult((Mission)args![0]);
                }
                throw new NotSupportedException("The saved-action proposal must not call the admiral.");
            }
        }

        private sealed class SavedMissionArguments
        {
            [JsonPropertyName("vesselId")]
            public string VesselId { get; set; } = String.Empty;
        }
    }
}
