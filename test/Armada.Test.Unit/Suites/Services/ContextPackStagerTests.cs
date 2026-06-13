namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for <see cref="ContextPackStager"/>: budget-exceed fallback,
    /// large-repo threshold, full path, off-mode skip, already-staged skip,
    /// and error-swallowing behaviour.
    /// </summary>
    public class ContextPackStagerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context Pack Stager";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("GenerateAndStageAsync_OffMode_NothingStaged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-off-");
                    try
                    {
                        NullCodeIndexService stub = new NullCodeIndexService();
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging());

                        Mission mission = new Mission { CodeContextMode = "off", Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test" };

                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertFalse(File.Exists(Path.Combine(worktree, "_briefing", "context-pack.md")), "No pack should be staged in off mode");
                        AssertFalse(stub.BuildContextPackCalled, "BuildContextPackAsync must not be called in off mode");

                        EnumerationQuery evtQuery = new EnumerationQuery { EventType = "code_index.pack_fast_fallback" };
                        EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(evtQuery).ConfigureAwait(false);
                        AssertEqual(0, events.Objects.Count, "No fallback event should be emitted in off mode");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });

            await RunTest("GenerateAndStageAsync_AlreadyStagedPack_LeftUntouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-already-");
                    try
                    {
                        string briefingDir = Path.Combine(worktree, "_briefing");
                        Directory.CreateDirectory(briefingDir);
                        string packPath = Path.Combine(briefingDir, "context-pack.md");
                        File.WriteAllText(packPath, "existing content");

                        NullCodeIndexService stub = new NullCodeIndexService();
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging());

                        Mission mission = new Mission { Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test" };

                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertFalse(stub.BuildContextPackCalled, "BuildContextPackAsync must not be called when pack already present");
                        AssertEqual("existing content", File.ReadAllText(packPath), "Pre-staged pack content must be untouched");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });

            await RunTest("GenerateAndStageAsync_LargeRepoThreshold_FastPackStagedAndEventEmitted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-large-");
                    try
                    {
                        LargeRepoCodeIndexService stub = new LargeRepoCodeIndexService();
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging());

                        Mission mission = new Mission { Id = "msn_test_001", Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test_large" };

                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertTrue(File.Exists(Path.Combine(worktree, "_briefing", "context-pack.md")), "Fast pack should be staged");
                        AssertFalse(stub.FullPackCalled, "Full pack path must not be invoked for large-repo threshold");

                        EnumerationQuery evtQuery = new EnumerationQuery { EventType = "code_index.pack_fast_fallback" };
                        EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(evtQuery).ConfigureAwait(false);
                        AssertEqual(1, events.Objects.Count, "Exactly one fallback event should be emitted");
                        AssertContains("large_repo", events.Objects[0].Payload ?? "", "Fallback reason should be large_repo");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });

            await RunTest("GenerateAndStageAsync_BudgetExceeded_FastPackStagedAndEventEmitted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-budget-");
                    try
                    {
                        // Full pack honors cancellation; fast pack returns immediately with a file.
                        BudgetExceedingCodeIndexService stub = new BudgetExceedingCodeIndexService();
                        // 50 ms budget so the stub's delay (500 ms) always exceeds it.
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging(), contextPackBudgetMs: 50);

                        Mission mission = new Mission { Id = "msn_test_002", Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test_budget" };

                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertTrue(File.Exists(Path.Combine(worktree, "_briefing", "context-pack.md")), "Fast pack should be staged after budget exceed");

                        EnumerationQuery evtQuery = new EnumerationQuery { EventType = "code_index.pack_fast_fallback" };
                        EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(evtQuery).ConfigureAwait(false);
                        AssertEqual(1, events.Objects.Count, "Exactly one fallback event should be emitted on budget exceed");
                        AssertContains("budget_exceeded", events.Objects[0].Payload ?? "", "Fallback reason should be budget_exceeded");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });

            await RunTest("GenerateAndStageAsync_SmallRepo_FullPackStagedNoEvent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-full-");
                    try
                    {
                        SmallRepoCodeIndexService stub = new SmallRepoCodeIndexService();
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging());

                        Mission mission = new Mission { Id = "msn_test_003", Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test_small" };

                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertTrue(File.Exists(Path.Combine(worktree, "_briefing", "context-pack.md")), "Full pack should be staged");
                        AssertTrue(stub.FullPackCalled, "Full pack path must be invoked for small repo");

                        EnumerationQuery evtQuery = new EnumerationQuery { EventType = "code_index.pack_fast_fallback" };
                        EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(evtQuery).ConfigureAwait(false);
                        AssertEqual(0, events.Objects.Count, "No fallback event should be emitted for full pack path");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });

            await RunTest("GenerateAndStageAsync_PackGenThrows_SwallowsAndDoesNotCrash", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string worktree = NewTempDir("cpstager-throw-");
                    try
                    {
                        ThrowingCodeIndexService stub = new ThrowingCodeIndexService();
                        ContextPackStager stager = new ContextPackStager(stub, testDb.Driver, SilentLogging());

                        Mission mission = new Mission { Title = "T", Description = "D" };
                        Vessel vessel = new Vessel { Id = "vsl_test_throw" };

                        // Must not throw.
                        await stager.GenerateAndStageAsync(mission, vessel, worktree).ConfigureAwait(false);

                        AssertFalse(File.Exists(Path.Combine(worktree, "_briefing", "context-pack.md")), "No pack should be staged when pack-gen throws");
                    }
                    finally
                    {
                        TryDeleteDir(worktree);
                    }
                }
            });
        }

        #region Private-Methods

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string NewTempDir(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        private static PrestagedFile MakeContentFile(string dest, string content)
        {
            return PrestagedFile.FromContent(dest, content);
        }

        #endregion

        #region Private-Stubs

        /// <summary>
        /// Stub that does nothing and records whether BuildContextPackAsync was called.
        /// </summary>
        private sealed class NullCodeIndexService : ICodeIndexService
        {
            public bool BuildContextPackCalled { get; private set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                BuildContextPackCalled = true;
                return Task.FromResult(new ContextPackResponse());
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());

            public Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(false);
        }

        /// <summary>
        /// Stub that reports the vessel as large so the fast-pack threshold fires.
        /// </summary>
        private sealed class LargeRepoCodeIndexService : ICodeIndexService
        {
            public bool FullPackCalled { get; private set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                if (!request.FastPackOnly)
                    FullPackCalled = true;

                ContextPackResponse response = new ContextPackResponse();
                response.Metrics.FastPackFallbackUsed = request.FastPackOnly;
                response.PrestagedFiles.Add(PrestagedFile.FromContent("_briefing/context-pack.md", "# fast pack\n"));
                return Task.FromResult(response);
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());

            public Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(true);
        }

        /// <summary>
        /// Stub where the full pack blocks until the cancellation token fires,
        /// while the fast pack returns immediately with a staged file.
        /// </summary>
        private sealed class BudgetExceedingCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public async Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                if (!request.FastPackOnly)
                {
                    // Simulate a slow full-pack that honors the cancellation token.
                    await Task.Delay(500, token).ConfigureAwait(false);
                }

                ContextPackResponse response = new ContextPackResponse();
                response.Metrics.FastPackFallbackUsed = request.FastPackOnly;
                response.PrestagedFiles.Add(PrestagedFile.FromContent("_briefing/context-pack.md", "# fast pack fallback\n"));
                return response;
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());

            public Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(false);
        }

        /// <summary>
        /// Stub for a small repo: fast-pack threshold returns false,
        /// full pack returns immediately and records the call.
        /// </summary>
        private sealed class SmallRepoCodeIndexService : ICodeIndexService
        {
            public bool FullPackCalled { get; private set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus());

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                if (!request.FastPackOnly)
                    FullPackCalled = true;

                ContextPackResponse response = new ContextPackResponse();
                response.Metrics.FastPackFallbackUsed = false;
                response.PrestagedFiles.Add(PrestagedFile.FromContent("_briefing/context-pack.md", "# full pack\n"));
                return Task.FromResult(response);
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());

            public Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(false);
        }

        /// <summary>
        /// Stub that throws on every call to exercise the swallow path.
        /// </summary>
        private sealed class ThrowingCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");

            public Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
                => throw new InvalidOperationException("simulated failure");
        }

        #endregion
    }
}
