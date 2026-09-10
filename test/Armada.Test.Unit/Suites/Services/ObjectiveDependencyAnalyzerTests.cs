namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests complete objective dependency graphs and write-time cycle rejection.
    /// </summary>
    public class ObjectiveDependencyAnalyzerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Dependency Analyzer";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("No blockers are dependency-ready", () =>
            {
                Objective root = MakeObjective("obj_a");
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, new[] { root });

                AssertTrue(result.IsDependencyReady);
                AssertEqual(0, result.BlockingNodes.Count);
                AssertEqual(0, result.BlockingEdges.Count);
                AssertEqual(0, result.BlockingChains.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Completed blockers do not appear in the blocking graph", () =>
            {
                Objective done = MakeObjective("obj_done", ObjectiveStatusEnum.Completed);
                Objective root = MakeObjective("obj_root", blockedBy: new[] { done.Id });
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, new[] { root, done });

                AssertTrue(result.IsDependencyReady);
                AssertEqual(0, result.BlockingNodes.Count);
                AssertEqual(0, result.BlockingEdges.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A transitive chain includes every objective and terminal status", () =>
            {
                Objective terminal = MakeObjective("obj_c", ObjectiveStatusEnum.InProgress);
                Objective middle = MakeObjective("obj_b", blockedBy: new[] { terminal.Id });
                Objective root = MakeObjective("obj_a", blockedBy: new[] { middle.Id });
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, new[] { terminal, root, middle });

                AssertFalse(result.IsDependencyReady);
                AssertEqual(2, result.BlockingEdges.Count);
                AssertEqual(1, result.BlockingChains.Count);
                AssertEqual("obj_a -> obj_b -> obj_c", String.Join(" -> ", result.BlockingChains[0].ObjectiveIds));
                AssertEqual(ObjectiveDependencyTerminalReasonEnum.Incomplete, result.BlockingChains[0].TerminalReason);
                ObjectiveDependencyNode terminalNode = result.BlockingNodes.Single(node => node.ObjectiveId == "obj_c");
                AssertEqual(ObjectiveStatusEnum.InProgress, terminalNode.Status);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A diamond keeps both complete blocking paths and every edge", () =>
            {
                Objective terminal = MakeObjective("obj_d", ObjectiveStatusEnum.InProgress);
                Objective left = MakeObjective("obj_b", blockedBy: new[] { terminal.Id });
                Objective right = MakeObjective("obj_c", blockedBy: new[] { terminal.Id });
                Objective root = MakeObjective("obj_a", blockedBy: new[] { right.Id, left.Id });
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, new[] { right, terminal, root, left });

                AssertEqual(4, result.BlockingEdges.Count);
                AssertEqual(2, result.BlockingChains.Count);
                AssertEqual("obj_a -> obj_b -> obj_d", String.Join(" -> ", result.BlockingChains[0].ObjectiveIds));
                AssertEqual("obj_a -> obj_c -> obj_d", String.Join(" -> ", result.BlockingChains[1].ObjectiveIds));
                AssertEqual(3, result.BlockingNodes.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A missing blocker is a named terminal node", () =>
            {
                Objective root = MakeObjective("obj_a", blockedBy: new[] { "obj_missing" });
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, new[] { root });

                AssertFalse(result.IsDependencyReady);
                AssertEqual(1, result.BlockingNodes.Count);
                AssertTrue(result.BlockingNodes[0].IsMissing);
                AssertEqual("obj_missing", result.BlockingNodes[0].ObjectiveId);
                AssertEqual(ObjectiveDependencyTerminalReasonEnum.Missing, result.BlockingChains[0].TerminalReason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Legacy cycles terminate and return a closed deterministic path", () =>
            {
                Objective first = MakeObjective("obj_a", blockedBy: new[] { "obj_b" });
                Objective second = MakeObjective("obj_b", blockedBy: new[] { "obj_c" });
                Objective third = MakeObjective("obj_c", blockedBy: new[] { "obj_a" });
                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(first, new[] { third, first, second });

                AssertTrue(result.HasCycle);
                AssertEqual("obj_a -> obj_b -> obj_c -> obj_a", String.Join(" -> ", result.CyclePath));
                AssertEqual(ObjectiveDependencyTerminalReasonEnum.Cycle, result.BlockingChains.Single().TerminalReason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Blocking paths are bounded while nodes and edges stay complete", () =>
            {
                List<Objective> objectives = new List<Objective>();
                List<List<Objective>> layers = new List<List<Objective>>();
                for (int layer = 0; layer < 8; layer++)
                {
                    List<Objective> current = new List<Objective>
                    {
                        MakeObjective("obj_layer_" + layer + "_a"),
                        MakeObjective("obj_layer_" + layer + "_b")
                    };
                    layers.Add(current);
                    objectives.AddRange(current);
                }
                for (int layer = 0; layer < layers.Count - 1; layer++)
                {
                    List<string> nextIds = layers[layer + 1].Select(item => item.Id).ToList();
                    foreach (Objective item in layers[layer]) item.BlockedByObjectiveIds = nextIds.ToList();
                }
                Objective root = MakeObjective("obj_root", blockedBy: layers[0].Select(item => item.Id));
                objectives.Add(root);

                ObjectiveDependencyAnalysis result = ObjectiveDependencyAnalyzer.Analyze(root, objectives);

                AssertTrue(result.BlockingChainsTruncated, "More than the safe path limit must be explicit.");
                AssertEqual(ObjectiveDependencyAnalyzer.MaxBlockingChains, result.BlockingChains.Count);
                AssertEqual(16, result.BlockingNodes.Count, "Every reachable node remains in the complete graph.");
                AssertEqual(30, result.BlockingEdges.Count, "Every reachable edge remains in the complete graph.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Blocker and snapshot order do not change output order", () =>
            {
                Objective blockerB = MakeObjective("obj_b");
                Objective blockerC = MakeObjective("obj_c");
                Objective rootOne = MakeObjective("obj_a", blockedBy: new[] { "obj_c", "obj_b" });
                Objective rootTwo = MakeObjective("obj_a", blockedBy: new[] { "obj_b", "obj_c" });

                ObjectiveDependencyAnalysis first = ObjectiveDependencyAnalyzer.Analyze(rootOne, new[] { blockerC, rootOne, blockerB });
                ObjectiveDependencyAnalysis second = ObjectiveDependencyAnalyzer.Analyze(rootTwo, new[] { blockerB, blockerC, rootTwo });

                AssertEqual(
                    String.Join("|", first.BlockingChains.Select(chain => String.Join(" -> ", chain.ObjectiveIds))),
                    String.Join("|", second.BlockingChains.Select(chain => String.Join(" -> ", chain.ObjectiveIds))));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ObjectiveService rejects a direct self-dependency and preserves the row", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService service = new ObjectiveService(testDb.Driver);
                AuthContext auth = AdminAuth();
                Objective objective = await service.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Self cycle" }).ConfigureAwait(false);

                InvalidOperationException error = await CaptureInvalidOperationAsync(() => service.UpdateAsync(
                    auth,
                    objective.Id,
                    new ObjectiveUpsertRequest { BlockedByObjectiveIds = new List<string> { objective.Id } })).ConfigureAwait(false);

                AssertEqual(
                    "Objective dependency cycle is not permitted: " + objective.Id + " -> " + objective.Id + ".",
                    error.Message);
                Objective? persistedResult = await service.ReadAsync(auth, objective.Id).ConfigureAwait(false);
                AssertNotNull(persistedResult);
                Objective persisted = persistedResult!;
                AssertEqual(0, persisted.BlockedByObjectiveIds.Count);
            }).ConfigureAwait(false);

            await RunTest("ObjectiveService rejects a three-objective cycle with its full path", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService service = new ObjectiveService(testDb.Driver);
                AuthContext auth = AdminAuth();
                Objective first = await service.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "First" }).ConfigureAwait(false);
                Objective second = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Second",
                    BlockedByObjectiveIds = new List<string> { first.Id }
                }).ConfigureAwait(false);
                Objective third = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Third",
                    BlockedByObjectiveIds = new List<string> { second.Id }
                }).ConfigureAwait(false);

                InvalidOperationException error = await CaptureInvalidOperationAsync(() => service.UpdateAsync(
                    auth,
                    first.Id,
                    new ObjectiveUpsertRequest { BlockedByObjectiveIds = new List<string> { third.Id } })).ConfigureAwait(false);

                AssertEqual(
                    "Objective dependency cycle is not permitted: " + first.Id + " -> " + third.Id + " -> " + second.Id + " -> " + first.Id + ".",
                    error.Message);
                Objective? persistedResult = await service.ReadAsync(auth, first.Id).ConfigureAwait(false);
                AssertNotNull(persistedResult);
                Objective persisted = persistedResult!;
                AssertEqual(0, persisted.BlockedByObjectiveIds.Count);
            }).ConfigureAwait(false);

            await RunTest("ObjectiveService rejects a structural cycle through a completed objective", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService service = new ObjectiveService(testDb.Driver);
                AuthContext auth = AdminAuth();
                Objective first = await service.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "First" }).ConfigureAwait(false);
                Objective completed = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Completed blocker",
                    Status = ObjectiveStatusEnum.Completed,
                    BlockedByObjectiveIds = new List<string> { first.Id }
                }).ConfigureAwait(false);

                InvalidOperationException error = await CaptureInvalidOperationAsync(() => service.UpdateAsync(
                    auth,
                    first.Id,
                    new ObjectiveUpsertRequest { BlockedByObjectiveIds = new List<string> { completed.Id } })).ConfigureAwait(false);

                AssertEqual(
                    "Objective dependency cycle is not permitted: " + first.Id + " -> " + completed.Id + " -> " + first.Id + ".",
                    error.Message);
            }).ConfigureAwait(false);

            await RunTest("Concurrent blocker writes cannot commit a two-objective cycle", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService firstService = new ObjectiveService(testDb.Driver);
                ObjectiveService secondService = new ObjectiveService(testDb.Driver);
                AuthContext auth = AdminAuth();
                Objective first = await firstService.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Concurrent first" }).ConfigureAwait(false);
                Objective second = await firstService.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Concurrent second" }).ConfigureAwait(false);

                Task<bool> firstWrite = TryDependencyUpdateAsync(firstService, auth, first.Id, second.Id);
                Task<bool> secondWrite = TryDependencyUpdateAsync(secondService, auth, second.Id, first.Id);
                bool[] outcomes = await Task.WhenAll(firstWrite, secondWrite).ConfigureAwait(false);

                AssertEqual(1, outcomes.Count(outcome => outcome), "Exactly one acyclic blocker write must commit.");
                AssertEqual(1, outcomes.Count(outcome => !outcome), "The competing cycle-closing write must fail.");
                List<Objective> persisted = await testDb.Driver.Objectives.EnumerateAsync().ConfigureAwait(false);
                Objective persistedFirst = persisted.Single(item => item.Id == first.Id);
                AssertEqual(0, ObjectiveDependencyAnalyzer.FindStructuralCycle(persistedFirst, persisted).Count,
                    "The committed graph must remain acyclic.");
            }).ConfigureAwait(false);

            await RunTest("ObjectiveService accepts an acyclic diamond", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService service = new ObjectiveService(testDb.Driver);
                AuthContext auth = AdminAuth();
                Objective terminal = await service.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Terminal" }).ConfigureAwait(false);
                Objective left = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Left",
                    BlockedByObjectiveIds = new List<string> { terminal.Id }
                }).ConfigureAwait(false);
                Objective right = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Right",
                    BlockedByObjectiveIds = new List<string> { terminal.Id }
                }).ConfigureAwait(false);
                Objective root = await service.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Root",
                    BlockedByObjectiveIds = new List<string> { right.Id, left.Id }
                }).ConfigureAwait(false);

                AssertEqual(2, root.BlockedByObjectiveIds.Count);
            }).ConfigureAwait(false);

            await RunTest("An unrelated update tolerates a legacy cycle", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Objective first = MakeObjective("obj_legacy_a", blockedBy: new[] { "obj_legacy_b" });
                first.TenantId = Constants.DefaultTenantId;
                first.UserId = Constants.DefaultUserId;
                Objective second = MakeObjective("obj_legacy_b", blockedBy: new[] { "obj_legacy_a" });
                second.TenantId = Constants.DefaultTenantId;
                second.UserId = Constants.DefaultUserId;
                await testDb.Driver.Objectives.CreateAsync(first).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(second).ConfigureAwait(false);

                ObjectiveService service = new ObjectiveService(testDb.Driver);
                Objective updated = await service.UpdateAsync(
                    AdminAuth(),
                    first.Id,
                    new ObjectiveUpsertRequest { Title = "Renamed legacy objective" }).ConfigureAwait(false);

                AssertEqual("Renamed legacy objective", updated.Title);
                AssertEqual("obj_legacy_b", updated.BlockedByObjectiveIds.Single());
            }).ConfigureAwait(false);
        }

        private static Objective MakeObjective(
            string id,
            ObjectiveStatusEnum status = ObjectiveStatusEnum.Scoped,
            IEnumerable<string>? blockedBy = null)
        {
            return new Objective
            {
                Id = id,
                Title = "Title " + id,
                Status = status,
                BlockedByObjectiveIds = blockedBy?.ToList() ?? new List<string>()
            };
        }

        private static AuthContext AdminAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                true,
                true,
                "UnitTest");
        }

        private static async Task<InvalidOperationException> CaptureInvalidOperationAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }

            throw new Exception("Expected InvalidOperationException, but no exception was thrown.");
        }

        private static async Task<bool> TryDependencyUpdateAsync(
            ObjectiveService service,
            AuthContext auth,
            string objectiveId,
            string blockerId)
        {
            try
            {
                await service.UpdateAsync(auth, objectiveId, new ObjectiveUpsertRequest
                {
                    BlockedByObjectiveIds = new List<string> { blockerId }
                }).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
