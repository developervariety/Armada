namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

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
                        blocking, "vsl_test", "armada_dispatch", message => warning = message)
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

            await RunTest("DispatchGuard_HealthyStatus_StillBlocksOnStaleIndex", async () =>
            {
                // Guard against over-correcting: bounding the call must not disable the precondition.
                StaleCodeIndexService stale = new StaleCodeIndexService();
                object? blocked = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    stale, "vsl_test", "armada_dispatch").ConfigureAwait(false);
                AssertNotNull(blocked, "a genuinely stale index must still block dispatch");
            });
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

        #endregion
    }
}
