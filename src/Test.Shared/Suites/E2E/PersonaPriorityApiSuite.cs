namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>REST persona routing configuration, validation, and partial-update regression descriptors.</summary>
    public sealed class PersonaPriorityApiSuite : IArmadaTestSuite
    {
        /// <summary>Build the REST round-trip descriptors.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("E2E.PersonaPriorityApi", "Persona priority REST API", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor("E2E.PersonaPriorityApi", "routing_roundtrip", "REST ordered routing: create, preserve, reject, replace, clear", async token =>
                {
                    E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                    string name = "rest-priority-" + Guid.NewGuid().ToString("N");
                    using (StringContent content = JsonHelper.ToJsonContent(new
                    {
                        name, promptTemplateName = "persona.worker",
                        captainPriorities = new object[] { new { runtime = "Mux", endpoint = "test", model = "gpt-oss:120b", minimumTier = "Economy" }, new { runtime = "ApiEndpoint", endpoint = "mep_specific" } },
                        priorityAvailability = "FallThrough", priorityExhaustion = "Wait"
                    }))
                    using (HttpResponseMessage response = await fixture.AuthClient.PostAsync("/api/v1/personas", content, token).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.Created, response.StatusCode);
                        Persona persona = JsonHelper.Deserialize<Persona>(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                        AssertEqual(2, persona.CaptainPriorities.Count);
                        AssertEqual(AgentRuntimeEnum.Mux, persona.CaptainPriorities[0].Runtime);
                        AssertEqual("mep_specific", persona.CaptainPriorities[1].Endpoint);
                    }
                    await PutAsync(fixture.AuthClient, name, new { description = "unrelated edit" }, HttpStatusCode.OK, token).ConfigureAwait(false);
                    Persona preserved = await ReadAsync(fixture.AuthClient, name, token).ConfigureAwait(false);
                    AssertEqual(2, preserved.CaptainPriorities.Count);
                    AssertEqual("test", preserved.CaptainPriorities[0].Endpoint);
                    AssertEqual("mep_specific", preserved.CaptainPriorities[1].Endpoint);
                    await PutAsync(fixture.AuthClient, name, new { captainPriorities = new object[] { new { } } }, HttpStatusCode.BadRequest, token).ConfigureAwait(false);
                    await PutAsync(fixture.AuthClient, name, new { captainPriorities = new object[] { new { runtime = "Codex", endpoint = "test" } } }, HttpStatusCode.BadRequest, token).ConfigureAwait(false);
                    await PutAsync(fixture.AuthClient, name, new { priorityAvailability = 999 }, HttpStatusCode.BadRequest, token).ConfigureAwait(false);
                    await PutAsync(fixture.AuthClient, name, new { captainPriorities = new object[] { new { model = new string('m', 1025) } } }, HttpStatusCode.BadRequest, token).ConfigureAwait(false);
                    AssertEqual(2, (await ReadAsync(fixture.AuthClient, name, token).ConfigureAwait(false)).CaptainPriorities.Count);
                    await PutAsync(fixture.AuthClient, name, new { captainPriorities = new object[] { new { runtime = "Gemini" } }, priorityAvailability = "WaitForFirstPriority" }, HttpStatusCode.OK, token).ConfigureAwait(false);
                    Persona replaced = await ReadAsync(fixture.AuthClient, name, token).ConfigureAwait(false);
                    AssertEqual(1, replaced.CaptainPriorities.Count);
                    AssertEqual(PersonaPriorityAvailabilityEnum.WaitForFirstPriority, replaced.PriorityAvailability);
                    await PutAsync(fixture.AuthClient, name, new { captainPriorities = Array.Empty<object>() }, HttpStatusCode.OK, token).ConfigureAwait(false);
                    AssertEqual(0, (await ReadAsync(fixture.AuthClient, name, token).ConfigureAwait(false)).CaptainPriorities.Count);
                    using (HttpResponseMessage deleted = await fixture.AuthClient.DeleteAsync("/api/v1/personas/" + name, token).ConfigureAwait(false))
                        AssertTrue(deleted.IsSuccessStatusCode);
                })
            });
        }

        private static async Task<Persona> ReadAsync(HttpClient client, string name, CancellationToken token)
        {
            using (HttpResponseMessage response = await client.GetAsync("/api/v1/personas/" + name, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                return JsonHelper.Deserialize<Persona>(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            }
        }

        private static async Task PutAsync(HttpClient client, string name, object body, HttpStatusCode expected, CancellationToken token)
        {
            using (StringContent content = JsonHelper.ToJsonContent(body))
            using (HttpResponseMessage response = await client.PutAsync("/api/v1/personas/" + name, content, token).ConfigureAwait(false))
                AssertEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        }
    }
}
