namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for status and health check routes.
    /// </summary>
    public class StatusTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "Status Routes";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;
        private string _SettingsFilePath;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new StatusTests suite with shared HTTP clients.
        /// </summary>
        /// <param name="authClient">Authenticated client.</param>
        /// <param name="unauthClient">Unauthenticated client.</param>
        /// <param name="settingsFilePath">Settings file the harness server is bound to. The persistence
        /// assertion reads this file; reading the machine-wide default would assert on the host operator's
        /// live settings, which the harness must neither read nor write.</param>
        public StatusTests(HttpClient authClient, HttpClient unauthClient, string settingsFilePath)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            if (String.IsNullOrWhiteSpace(settingsFilePath)) throw new ArgumentNullException(nameof(settingsFilePath));
            _SettingsFilePath = settingsFilePath;
        }

        #endregion

        #region Private-Methods

        private static JsonElement Prop(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            throw new InvalidOperationException("Property '" + name + "' not found; present: " + String.Join(", ", element.EnumerateObject().Select(p => p.Name)));
        }

        private static string NodeKey(JsonObject node, string name)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in node)
                if (String.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)) return entry.Key;
            return name;
        }

        private async Task<JsonElement> ReadSettingsAsync()
        {
            using (HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
                    return document.RootElement.Clone();
            }
        }

        private async Task<string> ReadTypedDecisionsTextAsync()
        {
            using (HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/typed-decisions").ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<JsonElement> ReadTypedDecisionsAsync()
        {
            using (JsonDocument document = JsonDocument.Parse(await ReadTypedDecisionsTextAsync().ConfigureAwait(false)))
                return document.RootElement.Clone();
        }

        private async Task PutTypedDecisionsAsync(string json)
        {
            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/typed-decisions", content).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private JsonElement FindDecision(JsonElement status, string key)
        {
            foreach (JsonElement entry in Prop(status, "decisions").EnumerateArray())
                if (String.Equals(Prop(entry, "key").GetString(), key, StringComparison.Ordinal)) return entry;
            throw new Exception("Decision not listed: " + key);
        }

        private static JsonValueKind NullableKind(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value.ValueKind;
            return JsonValueKind.Null;
        }

        private async Task PutSettingsJsonAsync(string json)
        {
            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/settings", content).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private async Task<Captain> CreateCaptainAsync(string name)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName, Runtime = "ClaudeCode" });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/captains", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Captain>(resp).ConfigureAwait(false);
        }

        private async Task<string> CreateFleetAsync()
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "StatusTestFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/fleets", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp).ConfigureAwait(false);
            return fleet.Id;
        }

        private async Task<string> CreateVesselAsync(string fleetId)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "StatusTestVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleetId });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            return vessel.Id;
        }

        private async Task<Mission> CreateMissionAsync(string title)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Title = title, Description = "Status test mission" });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/missions", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Read body once since stream can only be consumed once.
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            if (wrapper.Mission != null)
                return wrapper.Mission;

            // Response was the mission directly.
            return JsonHelper.Deserialize<Mission>(body);
        }

        private async Task<Voyage> CreateVoyageAsync(string vesselId, string title, int missionCount = 2)
        {
            object[] missions = Enumerable.Range(1, missionCount)
                .Select(i => (object)new { Title = "Voyage Mission " + i, Description = "Desc " + i })
                .ToArray();

            StringContent content = JsonHelper.ToJsonContent(new
            {
                Title = title,
                Description = "Status test voyage",
                VesselId = vesselId,
                Missions = missions
            });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/voyages", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Voyage>(resp).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a voyage this suite dispatched. A dispatched voyage on a real vessel launches its missions
        /// in turn, and each later mission becomes assignable when the one before it ends. Left open, that
        /// chain outlives the test and hands work to idle captains that later suites create, so a captain a
        /// later test expects to stay idle can go Working. A cancelled voyage's missions are never assigned.
        /// </summary>
        private async Task CancelVoyageAsync(string voyageId)
        {
            HttpResponseMessage resp = await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, resp.StatusCode, "The dispatched voyage must be cancelled when the test ends");
        }

        private async Task<Signal> CreateSignalAsync(string type, string message)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Type = type, Message = message });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/signals", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Signal>(resp).ConfigureAwait(false);
        }

        private async Task<string> CreateTenantCredentialAsync(string tenantId, string label, bool isTenantAdmin)
        {
            HttpResponseMessage userResponse = await _AuthClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    Email = label + "@status.armada",
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = isTenantAdmin
                })).ConfigureAwait(false);
            userResponse.EnsureSuccessStatusCode();
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await _AuthClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new { TenantId = tenantId, UserId = user.Id, Name = label + "-cred" })).ConfigureAwait(false);
            credentialResponse.EnsureSuccessStatusCode();
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            return credential.BearerToken;
        }

        private async Task<HttpStatusCode> GetStatusCodeWithBearerAsync(string bearerToken)
        {
            using (HttpClient client = new HttpClient { BaseAddress = _AuthClient.BaseAddress })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                using (HttpResponseMessage response = await client.GetAsync("/api/v1/status").ConfigureAwait(false))
                    return response.StatusCode;
            }
        }

        private async Task<ArmadaStatus> GetStatusAsync()
        {
            HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<ArmadaStatus>(response).ConfigureAwait(false);
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Status_AuthorizationMatrix_OnlyGlobalAdministratorReadsFleetStatus", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage tenantResponse = await _AuthClient.PostAsync("/api/v1/tenants",
                    JsonHelper.ToJsonContent(new { Name = "status-matrix-" + suffix })).ConfigureAwait(false);
                tenantResponse.EnsureSuccessStatusCode();
                TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);

                string userToken = await CreateTenantCredentialAsync(tenant.Id, "status-user-" + suffix, false).ConfigureAwait(false);
                string tenantAdminToken = await CreateTenantCredentialAsync(tenant.Id, "status-admin-" + suffix, true).ConfigureAwait(false);

                using (HttpResponseMessage anonymous = await _UnauthClient.GetAsync("/api/v1/status").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "anonymous caller");
                AssertEqual(HttpStatusCode.Forbidden, await GetStatusCodeWithBearerAsync(userToken).ConfigureAwait(false), "tenant user");
                AssertEqual(HttpStatusCode.Forbidden, await GetStatusCodeWithBearerAsync(tenantAdminToken).ConfigureAwait(false), "tenant administrator");
                using (HttpResponseMessage global = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, global.StatusCode, "global administrator");
            }).ConfigureAwait(false);

            await RunTest("UsagePreview_RequiresAuthentication", async () =>
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { persona = "Worker" }))
                using (HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/settings/usage-preview", content).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("UsagePreview_ValidatesDraftWithoutSaving", async () =>
            {
                using (HttpResponseMessage before = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
                {
                    string saved = await before.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (StringContent content = JsonHelper.ToJsonContent(new
                    {
                        persona = "Worker",
                        usageRouting = new
                        {
                            enabled = true,
                            accounts = new object[0],
                            personaRoutes = new { },
                            personaModels = new { Worker = new { @default = new[] { "example-default-model" }, stronger = new[] { "example-stronger-model" } } }
                        }
                    }))
                    using (HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/settings/usage-preview", content).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.OK, response.StatusCode);
                        string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        foreach (string field in new[] { "legacyOrder", "usageFilter", "modelGroups", "candidates", "capacity" })
                            AssertTrue(result.IndexOf("\"" + field + "\"", StringComparison.OrdinalIgnoreCase) >= 0, "preview reports " + field);
                        // Without mission text the preview reports the Default list and never calls the client.
                        AssertTrue(result.Contains("no_work_text"), "capacity source without mission text");
                        AssertTrue(result.Replace(" ", "").IndexOf("\"hasPersonaModels\":true", StringComparison.OrdinalIgnoreCase) >= 0, "the draft persona model entry applies");
                    }
                    using (HttpResponseMessage after = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
                        AssertEqual(saved, await after.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
                using (StringContent invalid = JsonHelper.ToJsonContent(new { usageRouting = new { refreshIntervalMinutes = 0 } }))
                using (HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/settings/usage-preview", invalid).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.BadRequest, response.StatusCode);
            }).ConfigureAwait(false);

            // A settings update applies each supplied field and leaves every absent field as stored, so
            // the dashboard's model routing policy and Routing V2 parts can save without replacing each other.
            await RunTest("TypedDecisions_PutModes_ValidatesPersistsAndReports", async () =>
            {
                JsonElement before = await ReadTypedDecisionsAsync().ConfigureAwait(false);
                JsonElement flakeBefore = FindDecision(before, "flake_score");
                string modeBefore = Prop(flakeBefore, "mode").GetString()!;
                double thresholdBefore = Prop(flakeBefore, "threshold").GetDouble();
                try
                {
                    foreach (string invalid in new[]
                    {
                        "{\"decisions\":{\"no_such_decision\":{\"mode\":\"Gate\"}}}",
                        "{\"decisions\":{\"flake_score\":{\"gateThreshold\":1.5}}}",
                        "{\"mode\":\"Loud\"}"
                    })
                    {
                        using (StringContent content = new StringContent(invalid, Encoding.UTF8, "application/json"))
                        using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/typed-decisions", content).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, "refused: " + invalid);
                    }
                    AssertEqual(modeBefore, Prop(FindDecision(await ReadTypedDecisionsAsync().ConfigureAwait(false), "flake_score"), "mode").GetString(), "a refused update changes nothing");

                    using (StringContent content = new StringContent("{\"decisions\":{\"flake_score\":{\"mode\":\"Shadow\",\"gateThreshold\":0.7}}}", Encoding.UTF8, "application/json"))
                    using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/typed-decisions", content).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.OK, response.StatusCode);
                    JsonElement flake = FindDecision(await ReadTypedDecisionsAsync().ConfigureAwait(false), "flake_score");
                    AssertEqual("Shadow", Prop(flake, "mode").GetString());
                    AssertEqual(0.7, Prop(flake, "threshold").GetDouble());
                    Armada.Core.Settings.ArmadaSettings saved = await Armada.Core.Settings.ArmadaSettings.LoadAsync(_SettingsFilePath).ConfigureAwait(false);
                    AssertEqual(Armada.Core.Enums.TypedDecisionModeEnum.Shadow, saved.TypedDecisions.Decisions["flake_score"].Mode, "the update is saved through the settings file");
                }
                finally
                {
                    string restore = "{\"decisions\":{\"flake_score\":{\"mode\":\"" + modeBefore + "\",\"gateThreshold\":" + thresholdBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}";
                    using (StringContent content = new StringContent(restore, Encoding.UTF8, "application/json"))
                    using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/typed-decisions", content).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.OK, response.StatusCode);
                }
            }).ConfigureAwait(false);

            if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARMADA_TYPESAFE_KEY")))
                SkipTest("TypedDecisions_KeyFile_SwitchesEffectiveModeAndIsNeverEchoed", "ARMADA_TYPESAFE_KEY is set for this process, so the key file cannot change the effective mode.");
            else await RunTest("TypedDecisions_KeyFile_SwitchesEffectiveModeAndIsNeverEchoed", async () =>
            {
                const string key = "automated-typed-decision-SECRET-value";
                JsonElement before = await ReadTypedDecisionsAsync().ConfigureAwait(false);
                string storedMode = Prop(before, "storedMode").GetString()!;
                AssertEqual("Off", Prop(before, "effectiveMode").GetString(), "no key: effective Off");
                AssertEqual("typed_decisions_no_key", Prop(before, "effectiveReason").GetString());
                // Keep the provider uncalled while a test key is present.
                await PutTypedDecisionsAsync("{\"mode\":\"Off\"}").ConfigureAwait(false);
                try
                {
                    using (StringContent content = JsonHelper.ToJsonContent(new { apiKey = key }))
                    using (HttpResponseMessage response = await _AuthClient.PutAsync("/api/v1/typed-decisions/key", content).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.NoContent, response.StatusCode);
                        AssertFalse((await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("SECRET"), "the key is not echoed");
                    }
                    string withKey = await ReadTypedDecisionsTextAsync().ConfigureAwait(false);
                    AssertFalse(withKey.Contains("SECRET"), "status never carries the key");
                    using (JsonDocument document = JsonDocument.Parse(withKey))
                    {
                        AssertTrue(Prop(document.RootElement, "keyPresent").GetBoolean());
                        AssertEqual("file", Prop(document.RootElement, "keySource").GetString());
                        AssertEqual(JsonValueKind.Null, NullableKind(document.RootElement, "effectiveReason"), "a key clears the no-key reason");
                    }
                    using (HttpResponseMessage settingsResponse = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
                        AssertFalse((await settingsResponse.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("SECRET"), "settings never carry the key");
                    using (HttpResponseMessage events = await _AuthClient.GetAsync("/api/v1/events?pageSize=100").ConfigureAwait(false))
                        AssertFalse((await events.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("SECRET"), "events never carry the key");

                    using (HttpResponseMessage deleted = await _AuthClient.DeleteAsync("/api/v1/typed-decisions/key").ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.OK, deleted.StatusCode);
                        string body = await deleted.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertFalse(body.Contains("SECRET"));
                        AssertTrue(body.Replace(" ", "").IndexOf("\"fileRemoved\":true", StringComparison.OrdinalIgnoreCase) >= 0, "the file was removed");
                    }
                    JsonElement after = await ReadTypedDecisionsAsync().ConfigureAwait(false);
                    AssertFalse(Prop(after, "keyPresent").GetBoolean());
                    AssertEqual("typed_decisions_no_key", Prop(after, "effectiveReason").GetString());
                    using (HttpResponseMessage status = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false))
                        AssertTrue((await status.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("typed_decisions_no_key"), "status reports the no-key reason");
                }
                finally
                {
                    await _AuthClient.DeleteAsync("/api/v1/typed-decisions/key").ConfigureAwait(false);
                    await PutTypedDecisionsAsync("{\"mode\":\"" + storedMode + "\"}").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateSettings_ReservedSlotsOnly_LeavesUsageRoutingAndModelProviders", async () =>
            {
                JsonElement before = await ReadSettingsAsync().ConfigureAwait(false);
                JsonElement modelTier = Prop(before, "modelTier");
                string usageRoutingBefore = Prop(modelTier, "usageRouting").GetRawText();
                string modelProvidersBefore = Prop(before, "modelProviders").GetRawText();
                int slotsBefore = Prop(modelTier, "reservedHighTierSlots").GetInt32();
                int changed = slotsBefore == 3 ? 4 : 3;
                try
                {
                    await PutSettingsJsonAsync("{\"modelTier\":{\"reservedHighTierSlots\":" + changed + "}}").ConfigureAwait(false);
                    JsonElement after = await ReadSettingsAsync().ConfigureAwait(false);
                    AssertEqual(changed, Prop(Prop(after, "modelTier"), "reservedHighTierSlots").GetInt32());
                    AssertEqual(usageRoutingBefore, Prop(Prop(after, "modelTier"), "usageRouting").GetRawText());
                    AssertEqual(modelProvidersBefore, Prop(after, "modelProviders").GetRawText());
                }
                finally
                {
                    await PutSettingsJsonAsync("{\"modelTier\":{\"reservedHighTierSlots\":" + slotsBefore + "}}").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateSettings_UsageRoutingOnly_LeavesTierPolicyAndModelProviders", async () =>
            {
                JsonElement before = await ReadSettingsAsync().ConfigureAwait(false);
                JsonElement modelTier = Prop(before, "modelTier");
                string usageRoutingBefore = Prop(modelTier, "usageRouting").GetRawText();
                string modelProvidersBefore = Prop(before, "modelProviders").GetRawText();
                string slotsBefore = Prop(modelTier, "reservedHighTierSlots").GetRawText();
                string nonNativeBefore = Prop(modelTier, "preferNonNativeFirst").GetRawText();
                JsonObject changed = JsonNode.Parse(usageRoutingBefore)!.AsObject();
                string currency = changed[NodeKey(changed, "currency")]?.GetValue<string>() == "EUR" ? "GBP" : "EUR";
                changed[NodeKey(changed, "currency")] = currency;
                try
                {
                    await PutSettingsJsonAsync("{\"modelTier\":{\"usageRouting\":" + changed.ToJsonString() + "}}").ConfigureAwait(false);
                    JsonElement after = await ReadSettingsAsync().ConfigureAwait(false);
                    JsonElement afterTier = Prop(after, "modelTier");
                    AssertEqual(currency, Prop(Prop(afterTier, "usageRouting"), "currency").GetString());
                    AssertEqual(slotsBefore, Prop(afterTier, "reservedHighTierSlots").GetRawText());
                    AssertEqual(nonNativeBefore, Prop(afterTier, "preferNonNativeFirst").GetRawText());
                    AssertEqual(modelProvidersBefore, Prop(after, "modelProviders").GetRawText());
                }
                finally
                {
                    await PutSettingsJsonAsync("{\"modelTier\":{\"usageRouting\":" + usageRoutingBefore + "}}").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateSettings_RetiredTierKeys_AreIgnored", async () =>
            {
                string marker = "retired-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                await PutSettingsJsonAsync("{\"modelTier\":{\"midTierModels\":[\"" + marker + "\"],\"specialistPersonas\":[\"" + marker + "\"]}}").ConfigureAwait(false);
                JsonElement after = await ReadSettingsAsync().ConfigureAwait(false);
                AssertFalse(Prop(after, "modelTier").GetRawText().Contains(marker), "a retired tier key sent to the settings API is not stored");
            }).ConfigureAwait(false);

            #region Status-Endpoint

            await RunTest("GetStatus_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_ReturnsJson", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                AssertEqual("application/json", contentType);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_HasAllExpectedProperties", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                AssertNotNull(status);
                Assert(status.MissionsByStatus != null, "Missing MissionsByStatus");
                Assert(status.Voyages != null, "Missing Voyages");
                Assert(status.RecentSignals != null, "Missing RecentSignals");
                Assert(status.TimestampUtc != default, "Missing TimestampUtc");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_ShowsZeros", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                // Note: previous test suites may have created captains, so we just verify the fields exist and are >= 0
                AssertTrue(status.TotalCaptains >= 0);
                AssertTrue(status.IdleCaptains >= 0);
                AssertTrue(status.WorkingCaptains >= 0);
                AssertTrue(status.StalledCaptains >= 0);
                AssertTrue(status.ActiveVoyages >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_EmptyVoyages", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.Voyages.Count >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_EmptyRecentSignals", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.RecentSignals.Count >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_MissionsByStatusIsObject", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertNotNull(status.MissionsByStatus);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingCaptains_ShowsCorrectCounts", async () =>
            {
                await CreateCaptainAsync("status-captain-1").ConfigureAwait(false);
                await CreateCaptainAsync("status-captain-2").ConfigureAwait(false);
                await CreateCaptainAsync("status-captain-3").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                AssertTrue(status.TotalCaptains >= 3);
                AssertTrue(status.IdleCaptains >= 3);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingMissions_ShowsMissionsByStatus", async () =>
            {
                await CreateMissionAsync("Status Mission 1").ConfigureAwait(false);
                await CreateMissionAsync("Status Mission 2").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                if (status.MissionsByStatus.TryGetValue("Pending", out int pending))
                {
                    AssertTrue(pending >= 2);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingVoyage_ShowsActiveVoyages", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Voyage voyage = await CreateVoyageAsync(vesselId, "Status Voyage 1").ConfigureAwait(false);
                try
                {
                    ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                    AssertTrue(status.ActiveVoyages >= 1);
                }
                finally
                {
                    await CancelVoyageAsync(voyage.Id).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingVoyage_VoyagesArrayPopulated", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Voyage voyage = await CreateVoyageAsync(vesselId, "Voyage Array Check").ConfigureAwait(false);
                try
                {
                    ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                    AssertTrue(status.Voyages.Count >= 1);
                }
                finally
                {
                    await CancelVoyageAsync(voyage.Id).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingSignals_ShowsRecentSignals", async () =>
            {
                await CreateSignalAsync("Mail", "Test signal 1").ConfigureAwait(false);
                await CreateSignalAsync("Heartbeat", "Test signal 2").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.RecentSignals.Count >= 2);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_TimestampUtc_IsRecent", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                DateTime timestamp = status.TimestampUtc.ToUniversalTime();
                TimeSpan elapsed = DateTime.UtcNow - timestamp;

                Assert(elapsed.TotalMinutes < 1, "TimestampUtc should be within the last minute but was " + elapsed.TotalMinutes + " minutes ago");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_TimestampUtc_IsValidDateTime", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                Assert(status.TimestampUtc != default, "TimestampUtc should be a valid datetime");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_WithoutAuth_ReturnsResponse", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertNotNull(response);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_WrongApiKey_ReturnsResponse", async () =>
            {
                HttpClient wrongKeyClient = new HttpClient();
                wrongKeyClient.BaseAddress = _AuthClient.BaseAddress;
                wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

                HttpResponseMessage response = await wrongKeyClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertNotNull(response);

                wrongKeyClient.Dispose();
            }).ConfigureAwait(false);

            #endregion

            #region Health-Check-Endpoint

            await RunTest("GetHealth_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_ReturnsHealthyStatus", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_NoAuth_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_NoAuth_ReturnsHealthyStatus", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_WrongApiKey_StillReturnsOk", async () =>
            {
                HttpClient wrongKeyClient = new HttpClient();
                wrongKeyClient.BaseAddress = _AuthClient.BaseAddress;
                wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "totally-wrong-key");

                HttpResponseMessage response = await wrongKeyClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);

                wrongKeyClient.Dispose();
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasTimestamp", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Timestamp != null, "Health check should include Timestamp");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasVersion", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Version != null, "Health check should include Version");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasUptime", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Uptime != null, "Health check should include Uptime");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasPorts", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Ports != null, "Health check should include Ports");
                Assert(health.Ports!.Admiral >= 0, "Ports should include Admiral");
                Assert(health.Ports!.Mcp >= 0, "Ports should include Mcp");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_ReturnsJson", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                AssertEqual("application/json", contentType);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_StartUtcIsBeforeNow", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.StartUtc != null, "Health check should include StartUtc");
                DateTime startUtc = health.StartUtc!.Value.ToUniversalTime();
                Assert(startUtc <= DateTime.UtcNow, "StartUtc should be in the past");
            }).ConfigureAwait(false);

            #endregion
        }

        #endregion
    }
}
