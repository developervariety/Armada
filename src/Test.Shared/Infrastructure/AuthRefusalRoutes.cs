namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.Routes;

    /// <summary>
    /// The protected-route table the authentication suites probe with missing and wrong credentials, and the one
    /// check they apply: the caller gets exactly 401 with a NotAuthorized error document that carries no
    /// protected content, and an authenticated read shows the refused request changed nothing.
    /// </summary>
    public static class AuthRefusalRoutes
    {
        #region Public-Members

        /// <summary>
        /// Wrong-key header value. It is well-formed but matches no credential.
        /// </summary>
        public const string WrongApiKey = "wrong-key-value";

        /// <summary>
        /// Protected routes probed without an API key.
        /// </summary>
        public static IReadOnlyList<AuthRefusalRoute> NoApiKey { get; } = new List<AuthRefusalRoute>
        {
            ListRoute("GetFleets", "/api/v1/fleets"),
            ListRoute("GetStatus", "/api/v1/status"),
            ListRoute("GetCaptains", "/api/v1/captains"),
            ListRoute("GetMissions", "/api/v1/missions?pageSize=1"),
            ListRoute("GetVoyages", "/api/v1/voyages"),
            ListRoute("GetSignals", "/api/v1/signals"),
            ListRoute("GetVessels", "/api/v1/vessels"),
            PostFleet(),
            PostCaptain(),
            PostMission()
        };

        /// <summary>
        /// Protected routes probed with a wrong API key.
        /// </summary>
        public static IReadOnlyList<AuthRefusalRoute> WrongApiKeyRoutes { get; } = new List<AuthRefusalRoute>
        {
            ListRoute("GetFleets", "/api/v1/fleets"),
            ListRoute("GetCaptains", "/api/v1/captains"),
            ListRoute("GetMissions", "/api/v1/missions?pageSize=1"),
            ListRoute("GetStatus", "/api/v1/status"),
            PostFleet()
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build a read-only route probe.
        /// </summary>
        /// <param name="scenario">Short scenario name.</param>
        /// <param name="path">Request path.</param>
        /// <returns>The route probe.</returns>
        public static AuthRefusalRoute ListRoute(string scenario, string path)
        {
            return new AuthRefusalRoute(scenario, HttpMethod.Get, path);
        }

        /// <summary>
        /// Build a probe that deletes an existing fleet; the fleet must still be readable afterwards.
        /// </summary>
        /// <param name="fleetId">Existing fleet ID.</param>
        /// <returns>The route probe.</returns>
        public static AuthRefusalRoute DeleteFleet(string fleetId)
        {
            return new AuthRefusalRoute(
                "DeleteFleet",
                HttpMethod.Delete,
                "/api/v1/fleets/" + fleetId,
                null,
                async (reader, marker) =>
                {
                    using (HttpResponseMessage response = await reader.GetAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false))
                        return response.StatusCode != HttpStatusCode.OK;
                });
        }

        /// <summary>
        /// Build a probe that renames an existing fleet; the stored name must be unchanged afterwards.
        /// </summary>
        /// <param name="fleetId">Existing fleet ID.</param>
        /// <param name="storedName">Name the fleet was created with.</param>
        /// <returns>The route probe.</returns>
        public static AuthRefusalRoute PutFleet(string fleetId, string storedName)
        {
            return new AuthRefusalRoute(
                "PutFleet",
                HttpMethod.Put,
                "/api/v1/fleets/" + fleetId,
                marker => new { Name = marker },
                async (reader, marker) =>
                {
                    using (HttpResponseMessage response = await reader.GetAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false))
                    {
                        string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.StatusCode != HttpStatusCode.OK) return true;
                        FleetDetailResponse detail = JsonHelper.Deserialize<FleetDetailResponse>(raw);
                        return detail.Fleet?.Name != storedName;
                    }
                });
        }

        /// <summary>
        /// Send the route's request with <paramref name="caller"/> and describe every way the refusal falls short:
        /// a status other than 401, a body that is not a NotAuthorized error document, or a write that
        /// <paramref name="reader"/> can observe afterwards.
        /// </summary>
        /// <param name="caller">Client carrying missing or wrong credentials.</param>
        /// <param name="reader">Authenticated client used for the no-write check.</param>
        /// <param name="route">Route to probe.</param>
        /// <returns>Null when the route refused correctly; otherwise the failure description.</returns>
        public static async Task<string?> DescribeRefusalFailureAsync(HttpClient caller, HttpClient reader, AuthRefusalRoute route)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (route == null) throw new ArgumentNullException(nameof(route));

            string where = route.Method + " " + route.Path;
            string marker = "auth-refused-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            List<string> failures = new List<string>();
            using (HttpRequestMessage request = new HttpRequestMessage(route.Method, route.Path))
            {
                if (route.BuildBody != null)
                    request.Content = JsonHelper.ToJsonContent(route.BuildBody(marker));
                using (HttpResponseMessage response = await caller.SendAsync(request).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        failures.Add(where + ": expected 401 but got " + (int)response.StatusCode + " " + raw);
                    }
                    else
                    {
                        ArmadaErrorResponse? error = null;
                        try
                        {
                            error = JsonHelper.Deserialize<ArmadaErrorResponse>(raw);
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            failures.Add(where + ": body is not an error document (" + ex.Message + "): " + raw);
                        }

                        if (error != null)
                        {
                            if (error.Error != "NotAuthorized")
                                failures.Add(where + ": body error expected NotAuthorized but got " + (error.Error ?? "<none>") + " " + raw);
                            if (error.Message != RouteAuthRefusal.AuthenticationRequiredMessage)
                                failures.Add(where + ": body message expected '" + RouteAuthRefusal.AuthenticationRequiredMessage + "' but got '" + error.Message + "'");
                        }

                        if (raw.Contains("\"Objects\"", StringComparison.Ordinal) || raw.Contains("\"Id\"", StringComparison.Ordinal))
                            failures.Add(where + ": refusal body carries protected content: " + raw);
                    }
                }
            }

            if (route.WriteObservedAsync != null && await route.WriteObservedAsync(reader, marker).ConfigureAwait(false))
                failures.Add(where + ": the refused request wrote a row an authenticated read can see");

            return failures.Count == 0 ? null : String.Join("\n", failures);
        }

        #endregion

        #region Private-Methods

        private static AuthRefusalRoute PostFleet()
        {
            return new AuthRefusalRoute(
                "PostFleets",
                HttpMethod.Post,
                "/api/v1/fleets",
                marker => new { Name = marker },
                async (reader, marker) =>
                {
                    EnumerationResult<Fleet> fleets = await ReadListAsync<Fleet>(reader, "/api/v1/fleets?pageSize=1000").ConfigureAwait(false);
                    return fleets.Objects.Any(f => f.Name == marker);
                });
        }

        private static AuthRefusalRoute PostCaptain()
        {
            return new AuthRefusalRoute(
                "PostCaptains",
                HttpMethod.Post,
                "/api/v1/captains",
                marker => new { Name = marker, Runtime = "ClaudeCode" },
                async (reader, marker) =>
                {
                    EnumerationResult<Captain> captains = await ReadListAsync<Captain>(reader, "/api/v1/captains?pageSize=1000").ConfigureAwait(false);
                    return captains.Objects.Any(c => c.Name == marker);
                });
        }

        private static AuthRefusalRoute PostMission()
        {
            return new AuthRefusalRoute(
                "PostMissions",
                HttpMethod.Post,
                "/api/v1/missions",
                marker => new { Title = marker, Description = "Refused create" },
                async (reader, marker) =>
                {
                    // Missions list newest first, so a mission the refused request created is on the first page.
                    EnumerationResult<Mission> missions = await ReadListAsync<Mission>(reader, "/api/v1/missions?pageSize=1000").ConfigureAwait(false);
                    return missions.Objects.Any(m => m.Title == marker);
                });
        }

        private static async Task<EnumerationResult<T>> ReadListAsync<T>(HttpClient reader, string path)
        {
            using (HttpResponseMessage response = await reader.GetAsync(path).ConfigureAwait(false))
            {
                string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Authenticated read of " + path + " returned " + (int)response.StatusCode + ": " + raw);
                return JsonHelper.Deserialize<EnumerationResult<T>>(raw);
            }
        }

        #endregion
    }
}
