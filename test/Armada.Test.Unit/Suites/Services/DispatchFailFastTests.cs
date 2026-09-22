namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using SyslogLogging;
    using Armada.Core.Settings;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Proves the dispatch path fails fast instead of blocking when a code-index dependency stalls.
    ///
    /// Regression context (2026-07-23): armada_dispatch blocked indefinitely -- no voyage row, no
    /// error, and no admiral log line -- because the precondition guard called GetStatusAsync with
    /// no token and no timeout, and the context-pack cache-probe/warm calls were likewise unbounded.
    /// Only BuildContextPackAsync had ever been time-boxed.
    ///
    /// These tests use hand-rolled blocking doubles (no mocking library, no real network) and never
    /// sleep longer than the configured bound.
    /// </summary>
    public class DispatchFailFastTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dispatch Fail Fast";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("GitProcessTimeouts_Default_Is120Seconds", () =>
            {
                string? prior = Environment.GetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar);
                try
                {
                    Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, null);
                    AssertEqual(120_000d, GitProcessTimeouts.Resolve().TotalMilliseconds,
                        "absent env var must fall back to the 120s default");
                }
                finally { Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, prior); }
                return Task.CompletedTask;
            });

            await RunTest("GitProcessTimeouts_EnvVar_Overrides", () =>
            {
                string? prior = Environment.GetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar);
                try
                {
                    Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, "45000");
                    AssertEqual(45_000d, GitProcessTimeouts.Resolve().TotalMilliseconds,
                        "a valid env var must override the default");
                }
                finally { Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, prior); }
                return Task.CompletedTask;
            });

            await RunTest("GitProcessTimeouts_ClampsBothEnds", () =>
            {
                string? prior = Environment.GetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar);
                try
                {
                    // A misconfigured tiny value must not make every git call fail instantly...
                    Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, "1");
                    AssertEqual((double)GitProcessTimeouts.MinTimeoutMs, GitProcessTimeouts.Resolve().TotalMilliseconds,
                        "below-minimum values clamp up");

                    // ...and a huge one must not reintroduce an effectively unbounded wait.
                    Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, "999999999");
                    AssertEqual((double)GitProcessTimeouts.MaxTimeoutMs, GitProcessTimeouts.Resolve().TotalMilliseconds,
                        "above-maximum values clamp down");
                }
                finally { Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, prior); }
                return Task.CompletedTask;
            });

            await RunTest("GitProcessTimeouts_InvalidOrNonPositive_FallsBackToDefault", () =>
            {
                string? prior = Environment.GetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar);
                try
                {
                    foreach (string bad in new[] { "not-a-number", "0", "-5", "" })
                    {
                        Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, bad);
                        AssertEqual(120_000d, GitProcessTimeouts.Resolve().TotalMilliseconds,
                            "invalid value '" + bad + "' must fall back to the default, not throw");
                    }
                }
                finally { Environment.SetEnvironmentVariable(GitProcessTimeouts.TimeoutEnvVar, prior); }
                return Task.CompletedTask;
            });

            await RunTest("DispatchGuard_StalledStatusLookup_ReturnsWithinBound_DoesNotBlock", async () =>
            {
                string? prior = Environment.GetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar);
                using BlockingCodeIndexService blocking = new BlockingCodeIndexService();
                try
                {
                    // Small bound so the test is fast; we never sleep longer than this.
                    Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, "300");

                    string? warning = null;
                    DateTime started = DateTime.UtcNow;
                    object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                        blocking, "vsl_test", "armada_dispatch", null, null, message => warning = message)
                        .ConfigureAwait(false);
                    TimeSpan elapsed = DateTime.UtcNow - started;

                    // The whole point: a hung index backend must not stall dispatch.
                    AssertTrue(elapsed < TimeSpan.FromSeconds(15),
                        "guard must return promptly on a stalled status lookup, took " + elapsed.TotalSeconds.ToString("F1") + "s");
                    AssertNull(blocked,
                        "dispatch must NOT be blocked just because the staleness guard was unavailable");
                    AssertNotNull(warning, "the timeout must be surfaced as a warning, not swallowed silently");
                    AssertTrue(warning!.Contains("timed out", StringComparison.OrdinalIgnoreCase),
                        "warning should say the lookup timed out; got: " + warning);
                }
                finally
                {
                    Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, prior);
                    blocking.Release();
                }
            });

            await RunTest("DispatchGuard_StaleIndex_BlockPolicy_StillBlocks", async () =>
            {
                StaleCodeIndexService stale = new StaleCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.Block };
                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    stale, "vsl_test", "armada_dispatch", settings, Silent()).ConfigureAwait(false);
                AssertNotNull(blocked, "Block policy must still block a stale index");
                AssertContains("code_index_stale", System.Text.Json.JsonSerializer.Serialize(blocked));
            });

            await RunTest("DispatchGuard_StaleButIrrelevant_ProceedsWithoutRefresh_EvenUnderBlock", async () =>
            {
                IrrelevantStaleCodeIndexService svc = new IrrelevantStaleCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.Block };
                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch", settings, Silent()).ConfigureAwait(false);
                AssertNull(blocked, "a stale index whose diff touches no indexable source must not block, even under Block");
                AssertFalse(svc.UpdateCalled, "no refresh is scheduled for irrelevant staleness");
            });

            await RunTest("DispatchGuard_StaleIndex_DefaultAndProceed_DispatchesAndSchedulesBackgroundRefresh", async () =>
            {
                RecordingRefreshCodeIndexService svc = new RecordingRefreshCodeIndexService();
                // debounce 0 so the coalesced refresh fires promptly for the test.
                CodeIndexSettings settings = new CodeIndexSettings { PostLandRefreshDebounceSeconds = 0 };
                // default policy is Proceed
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed, settings.DispatchStalenessPolicy);

                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch", settings, Silent()).ConfigureAwait(false);

                AssertNull(blocked, "Proceed policy must NOT block a stale index");
                bool refreshed = await svc.UpdateCalled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                AssertTrue(refreshed, "Proceed must schedule a background refresh (UpdateAsync should be invoked)");
            });

            await RunTest("DispatchGuard_StaleIndex_RefreshInline_RefreshesBeforeReturningAndProceeds", async () =>
            {
                RecordingRefreshCodeIndexService svc = new RecordingRefreshCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.RefreshInline };

                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch", settings, Silent()).ConfigureAwait(false);

                AssertNull(blocked, "RefreshInline proceeds after refreshing");
                AssertTrue(svc.UpdateCalled.Task.IsCompleted, "RefreshInline must run the refresh inline, before returning");
                AssertEqual(1, svc.UpdateInvocations, "the inline refresh runs exactly once");
            });

            await RunTest("DispatchGuard_UpdateInProgress_ProceedDoesNotBlock_BlockDoes", async () =>
            {
                UpdatingCodeIndexService svc = new UpdatingCodeIndexService();
                object? proceed = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch",
                    new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.Proceed }, Silent()).ConfigureAwait(false);
                AssertNull(proceed, "a refresh already running must not block dispatch under Proceed");

                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch",
                    new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.Block }, Silent()).ConfigureAwait(false);
                AssertNotNull(blocked, "Block waits for the in-progress refresh");
                AssertContains("code_index_update_in_progress", System.Text.Json.JsonSerializer.Serialize(blocked));
            });

            await RunTest("DispatchGuard_ModelCannotBlockWhenPolicyIsProceed", async () =>
            {
                using Armada.Test.Unit.TestHelpers.TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StaleCodeIndexService stale = new StaleCodeIndexService();
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(StalenessChoice("block", 0.99));
                TypedDispatchStalenessAdapter adapter = BuildStalenessAdapter(testDb.Driver, client);
                CodeIndexSettings settings = new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.Proceed };
                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    stale, "vsl_test", "armada_dispatch", settings, Silent(), null, default, adapter, "Fix the encoder", "Edit src/FrameEncoder.cs").ConfigureAwait(false);
                AssertNull(blocked, "the model cannot introduce a Block the Proceed policy would not");
                AssertTrue(client.CallCount >= 1, "the adapter was consulted on a relevant stale index");
            });

            await RunTest("DispatchGuard_RefreshInline_ModelProceed_SkipsInlineRefresh", async () =>
            {
                using Armada.Test.Unit.TestHelpers.TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                RecordingRefreshCodeIndexService svc = new RecordingRefreshCodeIndexService();
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(StalenessChoice("proceed", 0.95));
                TypedDispatchStalenessAdapter adapter = BuildStalenessAdapter(testDb.Driver, client);
                CodeIndexSettings settings = new CodeIndexSettings { DispatchStalenessPolicy = CodeIndexDispatchStalenessPolicyEnum.RefreshInline };
                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    svc, "vsl_test", "armada_dispatch", settings, Silent(), null, default, adapter, "Fix a README typo", "docs only").ConfigureAwait(false);
                AssertNull(blocked, "demoting RefreshInline to Proceed still dispatches");
                AssertEqual(0, svc.UpdateInvocations, "a Proceed demotion does not run the inline refresh before return");
            });
        }


        private static TypedDecisionResult StalenessChoice(string choice, double confidence)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new System.Collections.Generic.Dictionary<string, TypedAnswer>(System.StringComparer.Ordinal)
                {
                    [TypedDispatchStalenessAdapter.QuestionId] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = confidence }
                }
            };
        }

        private static TypedDispatchStalenessAdapter BuildStalenessAdapter(Armada.Core.Database.DatabaseDriver database, FakeTypedDecisionClient client)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions["dispatch_staleness"].Mode = TypedDecisionModeEnum.Gate;
            settings.Decisions["dispatch_staleness"].GateThreshold = 0.90;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new TypedDispatchStalenessAdapter(client, recorder, settings, Silent());
        }

        private static LoggingModule Silent()
        {
            LoggingModule l = new LoggingModule();
            l.Settings.EnableConsole = false;
            return l;
        }

        #region Test-Doubles

        /// <summary>Hand-rolled double whose status lookup blocks until released.</summary>
        private sealed class BlockingCodeIndexService : ICodeIndexService, IDisposable
        {
            private readonly ManualResetEventSlim _Gate = new ManualResetEventSlim(false);

            public void Release() { try { _Gate.Set(); } catch (ObjectDisposedException) { } }

            public void Dispose() { _Gate.Dispose(); }

            public async Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                // Block until cancelled or released -- emulates a hung index/embedding backend.
                await Task.Run(() => _Gate.Wait(token), token).ConfigureAwait(false);
                return new CodeIndexStatus();
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.Run(() => _Gate.Wait(token), token);

            public async Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                await Task.Run(() => _Gate.Wait(token), token).ConfigureAwait(false);
                return null;
            }

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default) => throw new NotSupportedException();
        }

        /// <summary>Hand-rolled double reporting a stale index so the precondition should fire.</summary>
        private sealed class StaleCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                return Task.FromResult(new CodeIndexStatus
                {
                    VesselId = vesselId,
                    VesselName = "test",
                    Freshness = "Stale",
                    IndexedCommitSha = "aaaaaaaa",
                    CurrentCommitSha = "bbbbbbbb"
                });
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default) => Task.CompletedTask;
            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default) => Task.FromResult<ContextPackResponse?>(null);
            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default) => throw new NotSupportedException();
        }

        /// <summary>Reports Stale, records UpdateAsync, and returns Fresh after a refresh.</summary>
        private sealed class RecordingRefreshCodeIndexService : ICodeIndexService
        {
            private int _Refreshed;

            public System.Threading.Tasks.TaskCompletionSource<bool> UpdateCalled { get; }
                = new System.Threading.Tasks.TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int UpdateInvocations => System.Threading.Volatile.Read(ref _Refreshed);

            public Task<bool> IsIndexedAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(true);

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                bool fresh = System.Threading.Volatile.Read(ref _Refreshed) > 0;
                return Task.FromResult(new CodeIndexStatus
                {
                    VesselId = vesselId,
                    VesselName = "test",
                    Freshness = fresh ? "Fresh" : "Stale",
                    IndexedCommitSha = fresh ? "bbbbbbbb" : "aaaaaaaa",
                    CurrentCommitSha = "bbbbbbbb"
                });
            }

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
            {
                System.Threading.Interlocked.Increment(ref _Refreshed);
                UpdateCalled.TrySetResult(true);
                return Task.FromResult(new CodeIndexStatus { VesselId = vesselId, Freshness = "Fresh", IndexedCommitSha = "bbbbbbbb", CurrentCommitSha = "bbbbbbbb" });
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default) => Task.CompletedTask;
            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default) => Task.FromResult<ContextPackResponse?>(null);
            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default) => throw new NotSupportedException();
        }

        /// <summary>Reports an update already in progress.</summary>
        private sealed class UpdatingCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId, VesselName = "test", Freshness = "Updating", UpdateInProgress = true, UpdateStartedUtc = DateTime.UtcNow });

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default) => Task.CompletedTask;
            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default) => Task.FromResult<ContextPackResponse?>(null);
            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default) => throw new NotSupportedException();
        }

        /// <summary>Stale index whose diff touches no indexable source (relevance not relevant).</summary>
        private sealed class IrrelevantStaleCodeIndexService : ICodeIndexService
        {
            public bool UpdateCalled { get; private set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId, VesselName = "test", Freshness = "Stale", IndexedCommitSha = "aaaaaaaa", CurrentCommitSha = "bbbbbbbb" });

            public Task<CodeIndexStalenessRelevance> GetStalenessRelevanceAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStalenessRelevance { VesselId = vesselId, IsStale = true, ChangedFileCount = 2, ChangedSourceFileCount = 0, IsRelevant = false, DiffUnavailable = false });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default) { UpdateCalled = true; return Task.FromResult(new CodeIndexStatus()); }
            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default) => Task.CompletedTask;
            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default) => Task.FromResult<ContextPackResponse?>(null);
            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default) => throw new NotSupportedException();
        }

        #endregion
    }
}
