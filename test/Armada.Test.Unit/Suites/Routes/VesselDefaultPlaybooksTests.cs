namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for Vessel.DefaultPlaybooks -- round-trips, merge semantics, and MCP tool CRUD.
    /// </summary>
    public class VesselDefaultPlaybooksTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel DefaultPlaybooks";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("NoDefaultPlaybooks_CallerSupplies2_MissionGetsExactly2", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("dp-fleet-1");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("dp-vessel-1", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultPlaybooks = null;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    List<SelectedPlaybook> callerPlaybooks = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_a", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                        new SelectedPlaybook { PlaybookId = "pbk_b", DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference }
                    };

                    List<SelectedPlaybook> merged = MergePlaybooksTestHelper(vessel.GetDefaultPlaybooks(), callerPlaybooks);
                    AssertEqual(2, merged.Count, "Should get exactly 2 playbooks when vessel has no defaults");
                    AssertEqual("pbk_a", merged[0].PlaybookId);
                    AssertEqual("pbk_b", merged[1].PlaybookId);
                }
            });

            await RunTest("VesselWith3Defaults_CallerSuppliesNone_MissionGets3Defaults", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("dp-fleet-2");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_x", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                        new SelectedPlaybook { PlaybookId = "pbk_y", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                        new SelectedPlaybook { PlaybookId = "pbk_z", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree }
                    };

                    Vessel vessel = new Vessel("dp-vessel-2", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    List<SelectedPlaybook> merged = MergePlaybooksTestHelper(vessel.GetDefaultPlaybooks(), new List<SelectedPlaybook>());
                    AssertEqual(3, merged.Count, "Should get 3 defaults when caller supplies none");
                    AssertEqual("pbk_x", merged[0].PlaybookId);
                    AssertEqual("pbk_z", merged[2].PlaybookId);
                }
            });

            await RunTest("VesselWith3Defaults_Caller2OneOverlap_Gets4TotalWithCallerDeliveryMode", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("dp-fleet-3");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_1", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                        new SelectedPlaybook { PlaybookId = "pbk_2", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                        new SelectedPlaybook { PlaybookId = "pbk_3", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    };

                    Vessel vessel = new Vessel("dp-vessel-3", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // pbk_2 overlaps (caller changes delivery mode); pbk_4 is new
                    List<SelectedPlaybook> callerPlaybooks = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_2", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree },
                        new SelectedPlaybook { PlaybookId = "pbk_4", DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference }
                    };

                    List<SelectedPlaybook> merged = MergePlaybooksTestHelper(vessel.GetDefaultPlaybooks(), callerPlaybooks);
                    AssertEqual(4, merged.Count, "Should get 4 total: 3 defaults + 1 new caller entry");

                    SelectedPlaybook? pbk2 = null;
                    foreach (SelectedPlaybook sp in merged)
                    {
                        if (sp.PlaybookId == "pbk_2") pbk2 = sp;
                    }
                    AssertNotNull(pbk2, "pbk_2 must appear in merged list");
                    AssertTrue(pbk2!.DeliveryMode == PlaybookDeliveryModeEnum.AttachIntoWorktree,
                        "Overlapping playbook must use caller's deliveryMode");
                }
            });

            await RunTest("ArmadaGetVessel_ReturnsPersistedDefaultPlaybooks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("dp-fleet-4");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_abc", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    };
                    string defaultsJson = JsonSerializer.Serialize(defaults);

                    Vessel vessel = new Vessel("dp-vessel-4", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultPlaybooks = defaultsJson;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? getHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_get_vessel") getHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(getHandler, "armada_get_vessel handler must be registered");

                    JsonElement getArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    object result = await getHandler!(getArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("DefaultPlaybooks", resultJson, "armada_get_vessel must return DefaultPlaybooks field");
                    AssertContains("pbk_abc", resultJson, "Returned defaultPlaybooks must contain persisted playbook ID");
                }
            });

            await RunTest("ArmadaUpdateVessel_MutatesDefaultPlaybooks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("dp-fleet-5");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("dp-vessel-5", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.DefaultPlaybooks = null;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    // Add an entry
                    JsonElement addArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        defaultPlaybooks = new[]
                        {
                            new { playbookId = "pbk_new", deliveryMode = "InlineFullContent" }
                        }
                    });
                    await updateHandler!(addArgs).ConfigureAwait(false);

                    Vessel? afterAdd = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(afterAdd, "Vessel must exist after update");
                    AssertNotNull(afterAdd!.DefaultPlaybooks, "DefaultPlaybooks must be set after update");
                    AssertContains("pbk_new", afterAdd.DefaultPlaybooks!, "Added playbook ID must appear in DefaultPlaybooks");

                    // Clear with empty array
                    JsonElement clearArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        defaultPlaybooks = Array.Empty<object>()
                    });
                    await updateHandler!(clearArgs).ConfigureAwait(false);

                    Vessel? afterClear = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(afterClear, "Vessel must exist after clear");
                    AssertNull(afterClear!.DefaultPlaybooks, "DefaultPlaybooks must be null after clearing with empty array");
                }
            });
        }

        /// <summary>
        /// Replicates the merge logic from McpVoyageTools.MergePlaybooks for unit testing
        /// without having to instantiate the full voyage tool infrastructure.
        /// </summary>
        private static List<SelectedPlaybook> MergePlaybooksTestHelper(List<SelectedPlaybook>? defaults, List<SelectedPlaybook> callerEntries)
        {
            List<SelectedPlaybook> merged = new List<SelectedPlaybook>();
            if (defaults != null)
            {
                foreach (SelectedPlaybook d in defaults)
                    merged.Add(new SelectedPlaybook { PlaybookId = d.PlaybookId, DeliveryMode = d.DeliveryMode });
            }
            foreach (SelectedPlaybook caller in callerEntries)
            {
                SelectedPlaybook? existing = null;
                foreach (SelectedPlaybook m in merged)
                {
                    if (m.PlaybookId == caller.PlaybookId) { existing = m; break; }
                }
                if (existing != null)
                    existing.DeliveryMode = caller.DeliveryMode;
                else
                    merged.Add(new SelectedPlaybook { PlaybookId = caller.PlaybookId, DeliveryMode = caller.DeliveryMode });
            }
            return merged;
        }
    }
}
