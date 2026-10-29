namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the operator probe of a captain's configured model.
    /// </summary>
    public sealed class CaptainModelProbeServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Model Probe Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ProbeReportsSuccessLatencyAndCreatesNoMission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("probe-captain")
                {
                    Runtime = AgentRuntimeEnum.ClaudeCode,
                    Model = "probe-model"
                }).ConfigureAwait(false);
                int calls = 0;
                CaptainModelProbeService service = new CaptainModelProbeService(
                    testDb.Driver,
                    (ignored, token) =>
                    {
                        calls++;
                        return Task.FromResult<CaptainModelValidationFailure?>(null);
                    },
                    minimumInterval: TimeSpan.Zero);
                CaptainModelProbeResult result = await service.ProbeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    captain.Id).ConfigureAwait(false);
                AssertEqual(true, result.Success);
                AssertEqual(captain.Id, result.CaptainId);
                AssertTrue(result.Error == null);
                AssertTrue(result.LatencyMs >= 0);
                AssertEqual(1, calls);
                List<Mission> missions = await testDb.Driver.Missions.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(0, missions.Count, "A probe creates no mission");
            }).ConfigureAwait(false);

            await RunTest("ProbeReportsAShortErrorAndRefusesANonAdmin", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("probe-fail")
                {
                    Runtime = AgentRuntimeEnum.ClaudeCode,
                    Model = "probe-model"
                }).ConfigureAwait(false);
                string longReason = new string('x', 180) + "\n" + new string('y', 80);
                int calls = 0;
                CaptainModelProbeService service = new CaptainModelProbeService(
                    testDb.Driver,
                    (ignored, token) =>
                    {
                        calls++;
                        return Task.FromResult<CaptainModelValidationFailure?>(
                            CaptainModelValidationFailure.Create(CaptainModelValidationFailureEnum.ModelRejected, longReason));
                    },
                    minimumInterval: TimeSpan.Zero);
                CaptainModelProbeResult failed = await service.ProbeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    captain.Id).ConfigureAwait(false);
                AssertEqual(false, failed.Success);
                AssertTrue(failed.Error != null && failed.Error.Length == 200 && !failed.Error.Contains('\n'));

                bool refused = false;
                try
                {
                    await service.ProbeAsync(
                        AuthContext.Authenticated("default", "default", false, false, "UnitTest"),
                        captain.Id).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    refused = true;
                }
                AssertTrue(refused, "A caller who is not a global administrator cannot probe a captain model");
                AssertEqual(1, calls, "The refused call does not reach the model");
            }).ConfigureAwait(false);

            await RunTest("ProbeRateLimitAndApiEndpointPath", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain runtime = await testDb.Driver.Captains.CreateAsync(new Captain("probe-rate")
                {
                    Runtime = AgentRuntimeEnum.ClaudeCode,
                    Model = "probe-model"
                }).ConfigureAwait(false);
                ModelEndpoint endpoint = await testDb.Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
                {
                    Name = "probe-endpoint",
                    Kind = ModelEndpointKindEnum.Inference,
                    Scope = ScopeEnum.TenantWide,
                    Provider = ModelProviderEnum.OpenAICompatible,
                    BaseUrl = "https://provider.example.test",
                    Model = "probe-model",
                    Enabled = true
                }).ConfigureAwait(false);
                Captain api = await testDb.Driver.Captains.CreateAsync(new Captain("probe-api")
                {
                    Runtime = AgentRuntimeEnum.ApiEndpoint,
                    Model = "probe-model",
                    ModelEndpointId = endpoint.Id
                }).ConfigureAwait(false);
                DateTime now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                int captainCalls = 0;
                int endpointCalls = 0;
                CaptainModelProbeService service = new CaptainModelProbeService(
                    testDb.Driver,
                    (captain, token) =>
                    {
                        captainCalls++;
                        return Task.FromResult<CaptainModelValidationFailure?>(null);
                    },
                    (auth, endpointId, token) =>
                    {
                        endpointCalls++;
                        AssertEqual(endpoint.Id, endpointId);
                        return Task.FromResult(new ModelEndpointProbeResult { Success = false, Error = "provider refused" });
                    },
                    TimeSpan.FromSeconds(30),
                    () => now);
                AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "UnitTest");
                CaptainModelProbeResult first = await service.ProbeAsync(admin, runtime.Id).ConfigureAwait(false);
                AssertEqual(true, first.Success);
                bool limited = false;
                try
                {
                    await service.ProbeAsync(admin, runtime.Id).ConfigureAwait(false);
                }
                catch (CaptainModelProbeRateLimitException ex)
                {
                    limited = true;
                    AssertEqual(now.AddSeconds(30), ex.RetryAfterUtc);
                }
                AssertTrue(limited, "The same captain cannot be probed again before the interval elapses");
                AssertEqual(1, captainCalls);

                now = now.AddSeconds(30);
                CaptainModelProbeResult again = await service.ProbeAsync(admin, runtime.Id).ConfigureAwait(false);
                AssertEqual(true, again.Success);
                AssertEqual(2, captainCalls);

                CaptainModelProbeResult apiResult = await service.ProbeAsync(admin, api.Id).ConfigureAwait(false);
                AssertEqual(false, apiResult.Success);
                AssertEqual("provider refused", apiResult.Error);
                AssertEqual(3, captainCalls, "An API-endpoint captain is admitted by the captain validation path");
                AssertEqual(1, endpointCalls, "An API-endpoint captain is then probed by the endpoint validation path");
                List<Mission> missions = await testDb.Driver.Missions.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(0, missions.Count);
            }).ConfigureAwait(false);
        }
    }
}
