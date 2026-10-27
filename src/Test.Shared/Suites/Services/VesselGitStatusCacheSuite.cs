namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="VesselGitStatusCache"/>: a result is reused for its lifetime (shorter for errors),
    /// concurrent requests share one read, and a read that throws is not cached.
    /// </summary>
    public sealed class VesselGitStatusCacheSuite : IArmadaTestSuite
    {
        /// <summary>
        /// Build the descriptor for the Vessel Git Status Cache suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("reuses_result_within_lifetime", "A result is reused until its lifetime passes", TestTags.Positive, async () =>
            {
                DateTime now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
                VesselGitStatusCache cache = new VesselGitStatusCache(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15), () => now);
                int reads = 0;
                Func<Task<VesselGitStatusResponse>> read = () =>
                {
                    reads++;
                    return Task.FromResult(new VesselGitStatusResponse { VesselId = "vsl_1", CommitsAhead = reads, CommitsBehind = 0 });
                };

                VesselGitStatusResponse first = await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                now = now.AddSeconds(119);
                VesselGitStatusResponse second = await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                AssertEqual(1, reads, "read once within the lifetime");
                AssertEqual(first.CommitsAhead, second.CommitsAhead);

                now = now.AddSeconds(1);
                VesselGitStatusResponse third = await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                AssertEqual(2, reads, "read again once the lifetime passed");
                AssertEqual(2, third.CommitsAhead);

                await cache.GetAsync("vsl_2", read).ConfigureAwait(false);
                AssertEqual(3, reads, "keys are cached separately");
            }));

            cases.Add(Case("errors_expire_sooner", "A result with an error is reused only for the error lifetime", TestTags.Negative, async () =>
            {
                DateTime now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
                VesselGitStatusCache cache = new VesselGitStatusCache(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15), () => now);
                int reads = 0;
                Func<Task<VesselGitStatusResponse>> read = () =>
                {
                    reads++;
                    return Task.FromResult(new VesselGitStatusResponse { VesselId = "vsl_1", Error = "no Harbor" });
                };

                await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                now = now.AddSeconds(14);
                await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                AssertEqual(1, reads);
                now = now.AddSeconds(1);
                await cache.GetAsync("vsl_1", read).ConfigureAwait(false);
                AssertEqual(2, reads);
            }));

            cases.Add(Case("concurrent_requests_share_one_read", "Requests made while a read runs share it", TestTags.Positive, async () =>
            {
                VesselGitStatusCache cache = new VesselGitStatusCache();
                TaskCompletionSource<VesselGitStatusResponse> gate = new TaskCompletionSource<VesselGitStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                int reads = 0;
                Func<Task<VesselGitStatusResponse>> read = () =>
                {
                    Interlocked.Increment(ref reads);
                    return gate.Task;
                };

                List<Task<VesselGitStatusResponse>> waiting = new List<Task<VesselGitStatusResponse>>();
                for (int i = 0; i < 10; i++) waiting.Add(cache.GetAsync("vsl_1", read));
                gate.SetResult(new VesselGitStatusResponse { VesselId = "vsl_1", CommitsAhead = 4, CommitsBehind = 1 });
                VesselGitStatusResponse[] results = await Task.WhenAll(waiting).ConfigureAwait(false);
                AssertEqual(1, reads, "one read for ten concurrent requests");
                foreach (VesselGitStatusResponse result in results) AssertEqual(4, result.CommitsAhead);
            }));

            cases.Add(Case("failed_read_is_not_cached", "A read that throws is not cached", TestTags.Negative, async () =>
            {
                VesselGitStatusCache cache = new VesselGitStatusCache();
                int reads = 0;
                Func<Task<VesselGitStatusResponse>> failing = async () =>
                {
                    reads++;
                    await Task.Yield();
                    throw new InvalidOperationException("boom");
                };

                await AssertThrowsAsync<InvalidOperationException>(() => cache.GetAsync("vsl_1", failing)).ConfigureAwait(false);
                VesselGitStatusResponse ok = await cache.GetAsync("vsl_1", () =>
                {
                    reads++;
                    return Task.FromResult(new VesselGitStatusResponse { VesselId = "vsl_1", CommitsAhead = 0, CommitsBehind = 0 });
                }).ConfigureAwait(false);
                AssertEqual(2, reads, "the next request reads again");
                AssertEqual(0, ok.CommitsAhead);
            }));

            cases.Add(Case("rejects_null_arguments", "GetAsync rejects a null key or read", TestTags.Negative, async () =>
            {
                VesselGitStatusCache cache = new VesselGitStatusCache();
                await AssertThrowsAsync<ArgumentNullException>(() => cache.GetAsync(null!, () => Task.FromResult(new VesselGitStatusResponse()))).ConfigureAwait(false);
                await AssertThrowsAsync<ArgumentNullException>(() => cache.GetAsync("vsl_1", null!)).ConfigureAwait(false);
                AssertThrows<ArgumentOutOfRangeException>(() => new VesselGitStatusCache(TimeSpan.FromSeconds(-1), TimeSpan.Zero, null));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.VesselGitStatusCache",
                displayName: "Vessel Git Status Cache",
                cases: cases);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.VesselGitStatusCache",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }
    }
}
