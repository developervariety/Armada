namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for MissionAliasResolver.ResolveAndOrder.
    /// </summary>
    public class MissionAliasResolverTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionAliasResolver";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ResolveAndOrder_LegacyLiteralIds_ReturnsInputUnchanged", async () =>
            {
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "First") { DependsOnMissionId = null },
                    new MissionDescription("M2", "Second") { DependsOnMissionId = "msn_abc" },
                    new MissionDescription("M3", "Third") { DependsOnMissionId = null }
                };

                IReadOnlyList<MissionDescription> result = MissionAliasResolver.ResolveAndOrder(input);

                // No aliases: same reference returned, input order preserved.
                AssertTrue(Object.ReferenceEquals(input, result), "Should return original list instance when no aliases present");
                AssertEqual(3, result.Count, "Count must be unchanged");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_SimpleChain_TopoOrdersM1ThenM2ThenM3", async () =>
            {
                // M3 depends on M2 depends on M1 -- but supplied in reverse order.
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M3", "Third") { Alias = "M3", DependsOnMissionAlias = "M2" },
                    new MissionDescription("M2", "Second") { Alias = "M2", DependsOnMissionAlias = "M1" },
                    new MissionDescription("M1", "First")  { Alias = "M1" }
                };

                IReadOnlyList<MissionDescription> result = MissionAliasResolver.ResolveAndOrder(input);

                AssertEqual(3, result.Count, "All missions must be present");
                AssertEqual("M1", result[0].Title, "M1 must come first (no deps)");
                AssertEqual("M2", result[1].Title, "M2 must follow M1");
                AssertEqual("M3", result[2].Title, "M3 must follow M2");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_ForwardReference_StillTopoSorts", async () =>
            {
                // M1 listed first but depends on M2 which is listed second.
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "desc1") { Alias = "M1", DependsOnMissionAlias = "M2" },
                    new MissionDescription("M2", "desc2") { Alias = "M2" }
                };

                IReadOnlyList<MissionDescription> result = MissionAliasResolver.ResolveAndOrder(input);

                AssertEqual(2, result.Count);
                AssertEqual("M2", result[0].Title, "M2 (no deps) must come before M1");
                AssertEqual("M1", result[1].Title, "M1 (depends on M2) must come after");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_Cycle_ThrowsInvalidDataException", async () =>
            {
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "d1") { Alias = "M1", DependsOnMissionAlias = "M2" },
                    new MissionDescription("M2", "d2") { Alias = "M2", DependsOnMissionAlias = "M1" }
                };

                bool threw = false;
                try
                {
                    MissionAliasResolver.ResolveAndOrder(input);
                }
                catch (InvalidDataException)
                {
                    threw = true;
                }
                AssertTrue(threw, "Cycle must throw InvalidDataException");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_DuplicateAlias_ThrowsInvalidDataException", async () =>
            {
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "d1") { Alias = "shared" },
                    new MissionDescription("M2", "d2") { Alias = "shared" }
                };

                bool threw = false;
                try
                {
                    MissionAliasResolver.ResolveAndOrder(input);
                }
                catch (InvalidDataException)
                {
                    threw = true;
                }
                AssertTrue(threw, "Duplicate alias must throw InvalidDataException");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_MissingAlias_ThrowsInvalidDataException", async () =>
            {
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "d1") { Alias = "M1", DependsOnMissionAlias = "NonExistent" }
                };

                bool threw = false;
                try
                {
                    MissionAliasResolver.ResolveAndOrder(input);
                }
                catch (InvalidDataException)
                {
                    threw = true;
                }
                AssertTrue(threw, "Unknown alias reference must throw InvalidDataException");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_MixedLiteralAndAlias_WorksTogether", async () =>
            {
                // M2 uses a literal dependsOnMissionId; M3 uses an alias dep on M1.
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M3", "d3") { Alias = "M3", DependsOnMissionAlias = "M1" },
                    new MissionDescription("M2", "d2") { DependsOnMissionId = "msn_external_123" },
                    new MissionDescription("M1", "d1") { Alias = "M1" }
                };

                IReadOnlyList<MissionDescription> result = MissionAliasResolver.ResolveAndOrder(input);

                AssertEqual(3, result.Count, "All missions must be present");

                // M1 has no deps and is a root; M2 has no alias dep and is also a root.
                // M3 depends on M1 via alias -- must come after M1.
                // M2 (literal dep on external mission) may appear in any position relative
                // to M1 and M3 since it has no alias deps.
                int m1Pos = -1;
                int m3Pos = -1;
                for (int i = 0; i < result.Count; i++)
                {
                    if (result[i].Title == "M1") m1Pos = i;
                    if (result[i].Title == "M3") m3Pos = i;
                }
                AssertTrue(m1Pos >= 0, "M1 must be present");
                AssertTrue(m3Pos >= 0, "M3 must be present");
                AssertTrue(m1Pos < m3Pos, "M1 must appear before M3 (M3 depends on M1)");

                // Verify literal DependsOnMissionId is preserved on M2.
                string? m2Dep = null;
                foreach (MissionDescription md in result)
                {
                    if (md.Title == "M2") m2Dep = md.DependsOnMissionId;
                }
                AssertEqual("msn_external_123", m2Dep, "Literal DependsOnMissionId must be preserved");
                await Task.CompletedTask;
            });

            await RunTest("ResolveAndOrder_AliasWithoutDependents_NoOpButPreserved", async () =>
            {
                // M1 declares an alias but nothing depends on it.
                List<MissionDescription> input = new List<MissionDescription>
                {
                    new MissionDescription("M1", "d1") { Alias = "standalone" },
                    new MissionDescription("M2", "d2") { Alias = "standalone2" }
                };

                IReadOnlyList<MissionDescription> result = MissionAliasResolver.ResolveAndOrder(input);

                AssertEqual(2, result.Count, "Both missions must be present");
                // Aliases are preserved on output items.
                bool foundStandalone = false;
                bool foundStandalone2 = false;
                foreach (MissionDescription md in result)
                {
                    if (md.Alias == "standalone") foundStandalone = true;
                    if (md.Alias == "standalone2") foundStandalone2 = true;
                }
                AssertTrue(foundStandalone, "Alias 'standalone' must be preserved on returned item");
                AssertTrue(foundStandalone2, "Alias 'standalone2' must be preserved on returned item");
                await Task.CompletedTask;
            });
        }
    }
}
