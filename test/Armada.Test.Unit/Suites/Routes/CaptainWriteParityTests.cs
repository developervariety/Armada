namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Creating and updating a captain apply one validation on REST, WebSocket and MCP: a captain whose runtime cannot
    /// serve (an API-endpoint captain with no model endpoint) is refused on every surface and nothing is written, and
    /// runtime options are normalized alike on the surfaces that take the whole captain body.
    /// </summary>
    public class CaptainWriteParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Write Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateCaptain_ApiEndpointWithoutModelEndpoint_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "WebSocket", "MCP" })
                    {
                        string name = "api-no-endpoint-" + surface.ToLowerInvariant();
                        SurfaceReply reply = surface switch
                        {
                            "REST" => await harness.RestAsync(HttpMethod.Post, "/api/v1/captains", new { Name = name, Runtime = "ApiEndpoint" }).ConfigureAwait(false),
                            "WebSocket" => await harness.WebSocketAsync("create_captain", null, new { Name = name, Runtime = "ApiEndpoint" }).ConfigureAwait(false),
                            _ => await harness.McpAsync("armada_create_captain", new { name = name, runtime = "ApiEndpoint" }).ConfigureAwait(false)
                        };

                        AssertTrue(reply.Refused, surface + ": an API-endpoint captain with no model endpoint is refused: " + reply);
                        AssertContains("model endpoint", reply.Body, surface + ": the refusal names the missing endpoint: " + reply);
                        AssertNull(await harness.Driver.Captains.ReadByNameAsync(name).ConfigureAwait(false), surface + ": nothing is created");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateCaptain_ToApiEndpointWithoutModelEndpoint_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "WebSocket", "MCP" })
                    {
                        string name = "switch-to-api-" + surface.ToLowerInvariant();
                        Captain captain = await harness.Driver.Captains.CreateAsync(new Captain(name) { Runtime = AgentRuntimeEnum.ClaudeCode }).ConfigureAwait(false);

                        SurfaceReply reply = surface switch
                        {
                            "REST" => await harness.RestAsync(HttpMethod.Put, "/api/v1/captains/" + captain.Id, new { Name = name, Runtime = "ApiEndpoint" }).ConfigureAwait(false),
                            "WebSocket" => await harness.WebSocketAsync("update_captain", captain.Id, new { Name = name, Runtime = "ApiEndpoint" }).ConfigureAwait(false),
                            _ => await harness.McpAsync("armada_update_captain", new { captainId = captain.Id, runtime = "ApiEndpoint" }).ConfigureAwait(false)
                        };

                        AssertTrue(reply.Refused, surface + ": switching to an API-endpoint runtime with no model endpoint is refused: " + reply);
                        Captain? stored = await harness.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertEqual(AgentRuntimeEnum.ClaudeCode, stored!.Runtime, surface + ": the captain keeps its runtime");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_MuxOnlyOptionsOnAnotherRuntime_AreDroppedAlikeOnRestAndWebSocket", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    List<string?> stored = new List<string?>();
                    foreach (string surface in new[] { "REST", "WebSocket" })
                    {
                        string name = "options-" + surface.ToLowerInvariant();
                        object body = new { Name = name, Runtime = "ClaudeCode", RuntimeOptionsJson = "{\"endpoint\":\"mux-only\"}" };
                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(HttpMethod.Post, "/api/v1/captains", body).ConfigureAwait(false)
                            : await harness.WebSocketAsync("create_captain", null, body).ConfigureAwait(false);
                        AssertFalse(reply.Refused, surface + ": the captain is created: " + reply);
                        Captain? created = await harness.Driver.Captains.ReadByNameAsync(name).ConfigureAwait(false);
                        AssertNotNull(created, surface + ": the captain exists");
                        stored.Add(created!.RuntimeOptionsJson);
                    }

                    AssertEqual(stored[0], stored[1], "REST and WebSocket store the same runtime options for the same body");
                    AssertNull(stored[1], "Mux runtime options on a non-Mux captain are dropped");
                }
            }).ConfigureAwait(false);
        }
    }
}
