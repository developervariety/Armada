namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="CaptainHealthCheckService"/>. Cases verify that a check records a result
    /// in the per-captain history, that a cycle checks each distinct captain endpoint exactly once and
    /// skips captains without an endpoint, that history is bounded, and that a captain's history can be
    /// forgotten. A canned probe is injected so the behavior is exercised without a live HTTP server.
    /// </summary>
    public sealed class CaptainHealthCheckServiceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.CaptainHealthCheckService";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain Health Check Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("check_records_result_in_history", "CheckAsync records a result in the captain's history", TestTags.Positive, async () =>
            {
                CaptainHealthCheckService service = new CaptainHealthCheckService(HealthyProbe());

                CaptainHealthCheckResult? result = await service.CheckAsync("cpt_a", "http://localhost/health").ConfigureAwait(false);

                AssertNotNull(result);
                AssertTrue(result!.Healthy);
                List<CaptainHealthCheckResult> history = service.GetHistory("cpt_a");
                AssertEqual(1, history.Count);
                AssertEqual("http://localhost/health", history[0].EndpointUrl);
            }));

            cases.Add(CaseAsync("check_ignores_empty_inputs", "CheckAsync ignores empty captain id or endpoint", TestTags.Negative, async () =>
            {
                CaptainHealthCheckService service = new CaptainHealthCheckService(HealthyProbe());

                AssertNull(await service.CheckAsync("", "http://localhost").ConfigureAwait(false));
                AssertNull(await service.CheckAsync("cpt_a", "").ConfigureAwait(false));
                AssertEqual(0, service.GetHistory("cpt_a").Count);
            }));

            cases.Add(CaseAsync("cycle_checks_each_endpoint_once_and_skips_endpointless", "RunCycleAsync checks each distinct endpoint once and skips captains without one", TestTags.Positive, async () =>
            {
                int calls = 0;
                CaptainHealthCheckService service = new CaptainHealthCheckService((captainId, endpointUrl, token) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new CaptainHealthCheckResult { Healthy = true });
                });

                List<Captain> captains = new List<Captain>
                {
                    Captain("cpt_a", "http://a/health"),
                    Captain("cpt_b", "http://b/health"),
                    Captain("cpt_c", null)
                };

                int chec01 = await service.RunCycleAsync(captains).ConfigureAwait(false);

                AssertEqual(2, chec01);
                AssertEqual(2, calls);
                AssertEqual(1, service.GetHistory("cpt_a").Count);
                AssertEqual(1, service.GetHistory("cpt_b").Count);
                AssertEqual(0, service.GetHistory("cpt_c").Count);
            }));

            cases.Add(CaseAsync("history_is_bounded_by_limit", "History is bounded by HistoryLimit, keeping the most recent", TestTags.Positive, async () =>
            {
                CaptainHealthCheckService service = new CaptainHealthCheckService(HealthyProbe())
                {
                    HistoryLimit = 3
                };

                for (int i = 0; i < 6; i++)
                {
                    await service.CheckAsync("cpt_a", "http://localhost/health").ConfigureAwait(false);
                }

                AssertEqual(3, service.GetHistory("cpt_a").Count);
            }));

            cases.Add(CaseAsync("forget_clears_captain_history", "Forget removes a captain's recorded history", TestTags.Positive, async () =>
            {
                CaptainHealthCheckService service = new CaptainHealthCheckService(HealthyProbe());
                await service.CheckAsync("cpt_a", "http://localhost/health").ConfigureAwait(false);
                await service.CheckAsync("cpt_b", "http://localhost/health").ConfigureAwait(false);

                service.Forget("cpt_a");

                AssertEqual(0, service.GetHistory("cpt_a").Count);
                AssertEqual(1, service.GetHistory("cpt_b").Count);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Captain Health Check Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Func<string, string, CancellationToken, Task<CaptainHealthCheckResult>> HealthyProbe()
        {
            return (captainId, endpointUrl, token) => Task.FromResult(new CaptainHealthCheckResult
            {
                Healthy = true,
                StatusCode = 200,
                LatencyMs = 5
            });
        }

        private static Captain Captain(string id, string? endpointUrl)
        {
            return new Captain
            {
                Id = id,
                Name = id,
                ApiEndpointUrl = endpointUrl
            };
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
