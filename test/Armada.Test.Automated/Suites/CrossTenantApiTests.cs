namespace Armada.Test.Automated.Suites
{
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level cross-tenant isolation tests. Verifies that entities created by one tenant
    /// are invisible and inaccessible to another tenant via list, read, and delete operations.
    /// </summary>
    public class CrossTenantApiTests : TestSuite
    {
        private sealed class AuthRefusalProbe
        {
            public AuthRefusalProbe(string routeFile, string method, string path, bool asOrdinaryUser)
            {
                RouteFile = routeFile;
                Method = method;
                Path = path;
                AsOrdinaryUser = asOrdinaryUser;
            }

            public string RouteFile { get; }

            public string Method { get; }

            public string Path { get; }

            public bool AsOrdinaryUser { get; }
        }

        private sealed class TenantUserCredentialResult
        {
            public string TenantId { get; set; } = String.Empty;

            public string UserId { get; set; } = String.Empty;

            public string CredentialId { get; set; } = String.Empty;

            public string BearerToken { get; set; } = String.Empty;
        }

        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "Cross-Tenant Isolation API Tests";

        #endregion

        #region Private-Members

        private HttpClient _AdminClient;
        private HttpClient _UnauthClient;
        private string _BaseUrl;
        private string _ApiKey;

        // Tenant A state
        private string? _TenantAId;
        private string? _UserAId;
        private string? _CredentialAId;
        private string? _BearerTokenA;
        private HttpClient? _ClientA;
        private string? _UserA2Id;
        private string? _CredentialA2Id;
        private HttpClient? _ClientA2;
        private string? _UserA3Id;
        private string? _CredentialA3Id;
        private HttpClient? _ClientA3;

        // Tenant B state
        private string? _TenantBId;
        private string? _UserBId;
        private string? _CredentialBId;
        private string? _BearerTokenB;
        private HttpClient? _ClientB;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new CrossTenantApiTests suite with shared HTTP clients, base URL, and API key.
        /// </summary>
        public CrossTenantApiTests(HttpClient authClient, HttpClient unauthClient, string baseUrl, string apiKey)
        {
            _AdminClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
            _ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        }

        #endregion

        #region Private-Methods

        private async Task<TenantUserCredentialResult> CreateTenantWithUserAsync(string label)
        {
            // Create tenant via admin
            string tenantName = "xt-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage tenantResp = await _AdminClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new { Name = tenantName })).ConfigureAwait(false);
            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResp).ConfigureAwait(false);

            // Create user in tenant
            string email = label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada";
            HttpResponseMessage userResp = await _AdminClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = true
                })).ConfigureAwait(false);
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResp).ConfigureAwait(false);

            // Create credential (bearer token) for the user
            HttpResponseMessage credResp = await _AdminClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = label + "-cred"
                })).ConfigureAwait(false);
            Credential cred = await JsonHelper.DeserializeAsync<Credential>(credResp).ConfigureAwait(false);

            return new TenantUserCredentialResult
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                CredentialId = cred.Id,
                BearerToken = cred.BearerToken
            };
        }

        private HttpClient CreateBearerClient(string bearerToken)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(_BaseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

        private async Task<TenantUserCredentialResult> CreateUserCredentialAsync(string tenantId, string label, bool isTenantAdmin = false)
        {
            string email = label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada";
            HttpResponseMessage userResp = await _AdminClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = isTenantAdmin
                })).ConfigureAwait(false);
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResp).ConfigureAwait(false);
            HttpResponseMessage credResp = await _AdminClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new { TenantId = tenantId, UserId = user.Id, Name = label + "-cred" })).ConfigureAwait(false);
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credResp).ConfigureAwait(false);
            return new TenantUserCredentialResult { TenantId = tenantId, UserId = user.Id, CredentialId = credential.Id, BearerToken = credential.BearerToken };
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("git failed: " + error.Trim() + output.Trim());
                return output.Trim();
            }
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            #region Setup

            await RunTest("Setup_CreateTenantA", async () =>
            {
                TenantUserCredentialResult result = await CreateTenantWithUserAsync("tenantA").ConfigureAwait(false);
                _TenantAId = result.TenantId;
                _UserAId = result.UserId;
                _CredentialAId = result.CredentialId;
                _BearerTokenA = result.BearerToken;
                _ClientA = CreateBearerClient(_BearerTokenA);

                AssertNotNull(_TenantAId, "TenantA ID");
                AssertNotNull(_BearerTokenA, "TenantA bearer token");
            }).ConfigureAwait(false);

            await RunTest("Setup_CreateTenantB", async () =>
            {
                TenantUserCredentialResult result = await CreateTenantWithUserAsync("tenantB").ConfigureAwait(false);
                _TenantBId = result.TenantId;
                _UserBId = result.UserId;
                _CredentialBId = result.CredentialId;
                _BearerTokenB = result.BearerToken;
                _ClientB = CreateBearerClient(_BearerTokenB);

                AssertNotNull(_TenantBId, "TenantB ID");
                AssertNotNull(_BearerTokenB, "TenantB bearer token");
            }).ConfigureAwait(false);

            await RunTest("Setup_VerifyTenantAIdentity", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                WhoAmIResult whoami = await JsonHelper.DeserializeAsync<WhoAmIResult>(response).ConfigureAwait(false);
                AssertEqual(_TenantAId, whoami.Tenant!.Id);
            }).ConfigureAwait(false);

            await RunTest("Setup_VerifyTenantBIdentity", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                WhoAmIResult whoami = await JsonHelper.DeserializeAsync<WhoAmIResult>(response).ConfigureAwait(false);
                AssertEqual(_TenantBId, whoami.Tenant!.Id);
            }).ConfigureAwait(false);

            await RunTest("Setup_CreateOrdinaryTenantAUsers", async () =>
            {
                TenantUserCredentialResult owner = await CreateUserCredentialAsync(_TenantAId!, "tenantA-owner", true).ConfigureAwait(false);
                TenantUserCredentialResult other = await CreateUserCredentialAsync(_TenantAId!, "tenantA-other").ConfigureAwait(false);
                _UserA2Id = owner.UserId;
                _CredentialA2Id = owner.CredentialId;
                _ClientA2 = CreateBearerClient(owner.BearerToken);
                _UserA3Id = other.UserId;
                _CredentialA3Id = other.CredentialId;
                _ClientA3 = CreateBearerClient(other.BearerToken);
                AssertNotNull(_ClientA2, "Owner client");
                AssertNotNull(_ClientA3, "Other user client");
            }).ConfigureAwait(false);

            #endregion

            #region Fleet-Isolation

            string fleetAId = null!;

            await RunTest("Fleet_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/fleets",
                    JsonHelper.ToJsonContent(new { Name = "xt-fleet-A-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(response).ConfigureAwait(false);
                AssertNotNull(fleet.Id, "Fleet ID");
                fleetAId = fleet.Id;
            }).ConfigureAwait(false);

            await RunTest("Fleet_ListFromTenantA_ContainsFleet", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/fleets").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Fleet> result = await JsonHelper.DeserializeAsync<EnumerationResult<Fleet>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(f => f.Id == fleetAId);
                AssertTrue(found, "Expected fleet " + fleetAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Fleet_ListFromTenantB_DoesNotContainFleet", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/fleets").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Fleet> result = await JsonHelper.DeserializeAsync<EnumerationResult<Fleet>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(f => f.Id == fleetAId);
                AssertFalse(found, "Expected fleet " + fleetAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Fleet_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/fleets/" + fleetAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Fleet_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/fleets/" + fleetAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Fleet_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/fleets/" + fleetAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                FleetDetailResponse detail = await JsonHelper.DeserializeAsync<FleetDetailResponse>(response).ConfigureAwait(false);
                AssertEqual(fleetAId, detail.Fleet!.Id);
            }).ConfigureAwait(false);

            await RunTest("Fleet_UpdateWithNameAndDescriptionOnly_KeepsOwnershipAndActive", async () =>
            {
                HttpResponseMessage deactivate = await _ClientA!.PutAsync("/api/v1/fleets/" + fleetAId,
                    JsonHelper.ToJsonContent(new { Name = "xt-fleet-A-inactive", Active = false })).ConfigureAwait(false);
                await AssertStatusCodeAsync(HttpStatusCode.OK, deactivate).ConfigureAwait(false);

                HttpResponseMessage rename = await _ClientA!.PutAsync("/api/v1/fleets/" + fleetAId,
                    JsonHelper.ToJsonContent(new { Name = "xt-fleet-A-renamed", Description = "renamed by tenant A" })).ConfigureAwait(false);
                await AssertStatusCodeAsync(HttpStatusCode.OK, rename).ConfigureAwait(false);

                HttpResponseMessage read = await _ClientA!.GetAsync("/api/v1/fleets/" + fleetAId).ConfigureAwait(false);
                await AssertStatusCodeAsync(HttpStatusCode.OK, read).ConfigureAwait(false);
                FleetDetailResponse detail = await JsonHelper.DeserializeAsync<FleetDetailResponse>(read).ConfigureAwait(false);
                AssertEqual("xt-fleet-A-renamed", detail.Fleet!.Name);
                AssertEqual(_TenantAId, detail.Fleet!.TenantId, "a partial PUT keeps the stored tenant");
                AssertEqual(_UserAId, detail.Fleet!.UserId, "a partial PUT keeps the stored owner");
                AssertFalse(detail.Fleet!.Active, "a PUT that omits Active keeps the stored value");
            }).ConfigureAwait(false);

            #endregion

            #region Captain-Isolation

            string captainAId = null!;

            await RunTest("Captain_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new { Name = "xt-captain-A-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Captain captain = await JsonHelper.DeserializeAsync<Captain>(response).ConfigureAwait(false);
                AssertNotNull(captain.Id, "Captain ID");
                captainAId = captain.Id;
            }).ConfigureAwait(false);

            await RunTest("Captain_ListFromTenantA_ContainsCaptain", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/captains").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(c => c.Id == captainAId);
                AssertTrue(found, "Expected captain " + captainAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Captain_ListFromTenantB_DoesNotContainCaptain", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/captains").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(c => c.Id == captainAId);
                AssertFalse(found, "Expected captain " + captainAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Captain_FormattedLog_PreservesAuthorization", async () =>
            {
                string url = "/api/v1/captains/" + captainAId + "/log?formatted=true";
                HttpResponseMessage denied = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode);
                denied = await _UnauthClient.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                HttpResponseMessage allowed = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, allowed.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Captain_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/captains/" + captainAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Captain_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/captains/" + captainAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Captain_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/captains/" + captainAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                Captain captain = await JsonHelper.DeserializeAsync<Captain>(response).ConfigureAwait(false);
                AssertEqual(captainAId, captain.Id);
            }).ConfigureAwait(false);

            #endregion

            #region Persona-Default-Captain

            // A persona's default captain is chosen by id. The update resolves the id inside the
            // persona's tenant and refuses a captain whose allow-list excludes the persona.
            string defaultedPersonaName = "xt-defaulted-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tenantBPersonaName = "xt-defaulted-b-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            await RunTest("PersonaDefaultCaptain_SetReadAndClear_RoundTrips", async () =>
            {
                HttpResponseMessage created = await _ClientA!.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = defaultedPersonaName, PromptTemplateName = "persona.worker" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);

                HttpResponseMessage set = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    JsonHelper.ToJsonContent(new { DefaultCaptainId = captainAId, Specialist = true })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, set.StatusCode);
                Persona read = await JsonHelper.DeserializeAsync<Persona>(await _ClientA!.GetAsync("/api/v1/personas/" + defaultedPersonaName).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(captainAId, read.DefaultCaptainId, "The default captain is stored");
                AssertTrue(read.Specialist, "The specialist flag in the same update is stored");

                HttpResponseMessage describe = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    JsonHelper.ToJsonContent(new { Description = "described" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, describe.StatusCode);
                Persona kept = await JsonHelper.DeserializeAsync<Persona>(await _ClientA!.GetAsync("/api/v1/personas/" + defaultedPersonaName).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(captainAId, kept.DefaultCaptainId, "An update that omits the default captain keeps it");

                HttpResponseMessage clear = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    new StringContent("{\"defaultCaptainId\":null}", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, clear.StatusCode);
                Persona cleared = await JsonHelper.DeserializeAsync<Persona>(await _ClientA!.GetAsync("/api/v1/personas/" + defaultedPersonaName).ConfigureAwait(false)).ConfigureAwait(false);
                AssertNull(cleared.DefaultCaptainId, "A null default captain clears it");
            }).ConfigureAwait(false);

            await RunTest("PersonaDefaultCaptain_UnknownOrOtherTenantCaptain_Returns400NotFound", async () =>
            {
                HttpResponseMessage unknown = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    JsonHelper.ToJsonContent(new { DefaultCaptainId = "cpt_examplemissing" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
                AssertContains("default_captain_not_found", await unknown.Content.ReadAsStringAsync().ConfigureAwait(false));

                HttpResponseMessage createdB = await _ClientB!.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = tenantBPersonaName, PromptTemplateName = "persona.worker" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createdB.StatusCode);
                HttpResponseMessage foreign = await _ClientB!.PutAsync("/api/v1/personas/" + tenantBPersonaName,
                    JsonHelper.ToJsonContent(new { DefaultCaptainId = captainAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, foreign.StatusCode, "Another tenant's captain counts as not found");
                AssertContains("default_captain_not_found", await foreign.Content.ReadAsStringAsync().ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("PersonaDefaultCaptain_CreateWithOtherTenantCaptain_Returns400AndCreatesNothing", async () =>
            {
                string refusedName = "xt-create-foreign-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage refused = await _ClientB!.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = refusedName, PromptTemplateName = "persona.worker", DefaultCaptainId = captainAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, refused.StatusCode, "A create naming another tenant's captain is refused");
                AssertContains("default_captain_not_found", await refused.Content.ReadAsStringAsync().ConfigureAwait(false));

                HttpResponseMessage lookup = await _ClientB!.GetAsync("/api/v1/personas/" + refusedName).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, lookup.StatusCode, "The refused create writes nothing");
            }).ConfigureAwait(false);

            await RunTest("PersonaDefaultCaptain_PersonaLockedCaptain_Returns400", async () =>
            {
                HttpResponseMessage lockedResponse = await _ClientA!.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new { Name = "xt-locked-" + Guid.NewGuid().ToString("N").Substring(0, 8), AllowedPersonas = "[\"Worker\"]" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, lockedResponse.StatusCode);
                Captain locked = await JsonHelper.DeserializeAsync<Captain>(lockedResponse).ConfigureAwait(false);

                HttpResponseMessage refused = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    JsonHelper.ToJsonContent(new { DefaultCaptainId = locked.Id })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, refused.StatusCode);
                AssertContains("default_captain_persona_locked", await refused.Content.ReadAsStringAsync().ConfigureAwait(false));

                await _ClientA!.DeleteAsync("/api/v1/captains/" + locked.Id).ConfigureAwait(false);
                await _ClientA!.DeleteAsync("/api/v1/personas/" + defaultedPersonaName).ConfigureAwait(false);
                await _ClientB!.DeleteAsync("/api/v1/personas/" + tenantBPersonaName).ConfigureAwait(false);
            }).ConfigureAwait(false);

            #endregion

            #region Backlog-And-Refinement-Isolation

            string objectiveAId = null!;
            string refinementSessionAId = null!;

            await RunTest("Backlog_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/backlog",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-backlog-A-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Description = "Cross-tenant backlog isolation coverage.",
                        Status = "Scoped",
                        Kind = "Feature",
                        Priority = "P1",
                        Rank = 10,
                        BacklogState = "Inbox",
                        Effort = "M"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Objective objective = await JsonHelper.DeserializeAsync<Objective>(response).ConfigureAwait(false);
                AssertNotNull(objective.Id, "Backlog objective ID");
                objectiveAId = objective.Id;
            }).ConfigureAwait(false);

            await RunTest("Backlog_ListFromTenantA_ContainsObjective", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/backlog?pageSize=100").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Objective> result = await JsonHelper.DeserializeAsync<EnumerationResult<Objective>>(response).ConfigureAwait(false);
                AssertTrue(result.Objects.Any(objective => objective.Id == objectiveAId), "Expected backlog item in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Backlog_ListFromTenantB_DoesNotContainObjective", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/backlog?pageSize=100").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Objective> result = await JsonHelper.DeserializeAsync<EnumerationResult<Objective>>(response).ConfigureAwait(false);
                AssertFalse(result.Objects.Any(objective => objective.Id == objectiveAId), "Expected backlog item to be hidden from tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Backlog_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/backlog/" + objectiveAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Backlog_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/backlog/" + objectiveAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Backlog_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/backlog/" + objectiveAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                Objective objective = await JsonHelper.DeserializeAsync<Objective>(response).ConfigureAwait(false);
                AssertEqual(objectiveAId, objective.Id);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/backlog/" + objectiveAId + "/refinement-sessions",
                    JsonHelper.ToJsonContent(new
                    {
                        CaptainId = captainAId,
                        Title = "xt-refinement-A-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                ObjectiveRefinementSessionDetail detail = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(response).ConfigureAwait(false);
                AssertNotNull(detail.Session.Id, "Refinement session ID");
                refinementSessionAId = detail.Session.Id;
                AssertEqual(objectiveAId, detail.Session.ObjectiveId);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_ListFromTenantA_ContainsSession", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/backlog/" + objectiveAId + "/refinement-sessions").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                List<ObjectiveRefinementSession> sessions = await JsonHelper.DeserializeAsync<List<ObjectiveRefinementSession>>(response).ConfigureAwait(false);
                AssertTrue(sessions.Any(session => session.Id == refinementSessionAId), "Expected refinement session in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_ListFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/backlog/" + objectiveAId + "/refinement-sessions").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/objective-refinement-sessions/" + refinementSessionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/objective-refinement-sessions/" + refinementSessionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/objective-refinement-sessions/" + refinementSessionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                ObjectiveRefinementSessionDetail detail = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(response).ConfigureAwait(false);
                AssertEqual(refinementSessionAId, detail.Session.Id);
            }).ConfigureAwait(false);

            await RunTest("BacklogRefinement_DeleteFromTenantA_Returns204", async () =>
            {
                HttpResponseMessage response = await _ClientA!.DeleteAsync("/api/v1/objective-refinement-sessions/" + refinementSessionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Backlog_DeleteFromTenantA_Returns204", async () =>
            {
                HttpResponseMessage response = await _ClientA!.DeleteAsync("/api/v1/backlog/" + objectiveAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, response.StatusCode);
            }).ConfigureAwait(false);

            #endregion

            #region Vessel-Isolation

            string vesselAId = null!;

            await RunTest("Vessel_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/vessels",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-vessel-A-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        FleetId = fleetAId,
                        RepoUrl = TestRepoHelper.GetLocalBareRepoUrl()
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false);
                AssertNotNull(vessel.Id, "Vessel ID");
                vesselAId = vessel.Id;
            }).ConfigureAwait(false);

            await RunTest("Vessel_ListFromTenantA_ContainsVessel", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/vessels").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(v => v.Id == vesselAId);
                AssertTrue(found, "Expected vessel " + vesselAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Vessel_ListFromTenantB_DoesNotContainVessel", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/vessels").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(v => v.Id == vesselAId);
                AssertFalse(found, "Expected vessel " + vesselAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Vessel_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/vessels/" + vesselAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Vessel_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/vessels/" + vesselAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Vessel_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/vessels/" + vesselAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false);
                AssertEqual(vesselAId, vessel.Id);
            }).ConfigureAwait(false);

            await RunTest("Vessel_BranchInspection_EnforcesScopeAndPreservesRepository", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-branch-api-" + Guid.NewGuid().ToString("N"));
                string working = Path.Combine(root, "working");
                    string bare = Path.Combine(root, "repository.git");
                string vesselId = String.Empty;
                string bareVesselId = String.Empty;
                try
                {
                    Directory.CreateDirectory(working);
                    await RunGitAsync(working, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(working, "config", "user.name", "Armada API Tests").ConfigureAwait(false);
                    await RunGitAsync(working, "config", "user.email", "armada-api-tests@example.test").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(working, "main.txt"), "main\n").ConfigureAwait(false);
                    await RunGitAsync(working, "add", "main.txt").ConfigureAwait(false);
                    await RunGitAsync(working, "commit", "-m", "API branch base").ConfigureAwait(false);
                    await RunGitAsync(working, "checkout", "-b", "feature/ünusual.name").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(working, "feature.txt"), "feature\n").ConfigureAwait(false);
                    await RunGitAsync(working, "add", "feature.txt").ConfigureAwait(false);
                    await RunGitAsync(working, "commit", "-m", "API branch feature").ConfigureAwait(false);
                    await RunGitAsync(working, "checkout", "main").ConfigureAwait(false);
                    await RunGitAsync(root, "clone", "--bare", working, bare).ConfigureAwait(false);
                    string refsBefore = await RunGitAsync(working, "show-ref").ConfigureAwait(false);
                    string bareRefsBefore = await RunGitAsync(bare, "show-ref").ConfigureAwait(false);

                    HttpResponseMessage create = await _ClientA2!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-branch-working-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        RepoUrl = "file:///branch-working",
                        LocalPath = working,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, create.StatusCode, "Ordinary owner creates working repository vessel");
                    Vessel workingVessel = await JsonHelper.DeserializeAsync<Vessel>(create).ConfigureAwait(false);
                    vesselId = workingVessel.Id;
                    HttpResponseMessage demoteOwner = await _AdminClient.PutAsync("/api/v1/users/" + _UserA2Id, JsonHelper.ToJsonContent(new
                    {
                        Email = "demoted-owner-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada",
                        IsTenantAdmin = false,
                        Active = true
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, demoteOwner.StatusCode, "Owner fixture is demoted before inspection");

                    HttpResponseMessage ownerResponse = await _ClientA2.GetAsync("/api/v1/vessels/" + Uri.EscapeDataString(vesselId) + "/branches").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, ownerResponse.StatusCode, "Owner can inspect branches");
                    BranchListResponse ownerListing = await JsonHelper.DeserializeAsync<BranchListResponse>(ownerResponse).ConfigureAwait(false);
                    AssertEqual(vesselId, ownerListing.VesselId, "Vessel ID");
                    AssertEqual("main", ownerListing.DefaultBranch, "Default branch");
                    AssertEqual("LocalPath", ownerListing.Source, "Repository source");
                    AssertEqual("attached", ownerListing.HeadState, "Working repository HEAD state");
                    AssertEqual("main", ownerListing.HeadRef, "Working repository HEAD ref");
                    AssertEqual(2, ownerListing.BranchCount, "Branch count");
                    AssertEqual("main", ownerListing.Branches[0].Name, "Default branch first");
                    AssertTrue(ownerListing.Branches[0].IsDefault && ownerListing.Branches[0].IsCurrent, "Default and current markers");
                    AssertEqual("feature/ünusual.name", ownerListing.Branches[1].Name, "Encoded unusual branch name preserved");
                    AssertEqual(1, ownerListing.Branches[1].Ahead, "Feature ahead count");
                    AssertEqual(0, ownerListing.Branches[1].Behind, "Feature behind count");
                    AssertTrue(ownerListing.Branches[1].CommitHash != null, "Feature tip hash");
                    AssertTrue(ownerListing.Branches[1].CommitSubject == "API branch feature", "Feature tip subject");
                    AssertTrue(ownerListing.Branches[1].CommitDate.HasValue, "Feature tip date");
                    AssertTrue(ownerListing.Error == null, "Successful listing has no error");

                    HttpResponseMessage tenantAdminResponse = await _ClientA!.GetAsync("/api/v1/vessels/" + vesselId + "/branches").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, tenantAdminResponse.StatusCode, "Tenant admin can inspect branches");
                    AssertEqual(2, (await JsonHelper.DeserializeAsync<BranchListResponse>(tenantAdminResponse).ConfigureAwait(false)).BranchCount, "Tenant admin branch count");
                    AssertEqual(HttpStatusCode.NotFound, (await _ClientA3!.GetAsync("/api/v1/vessels/" + vesselId + "/branches").ConfigureAwait(false)).StatusCode, "Same-tenant other user is denied");
                    AssertEqual(HttpStatusCode.NotFound, (await _ClientB!.GetAsync("/api/v1/vessels/" + vesselId + "/branches").ConfigureAwait(false)).StatusCode, "Other tenant is denied");
                    AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.GetAsync("/api/v1/vessels/" + vesselId + "/branches").ConfigureAwait(false)).StatusCode, "Anonymous is denied");
                    HttpResponseMessage adminResponse = await _AdminClient.GetAsync("/api/v1/vessels/" + vesselId + "/branches").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, adminResponse.StatusCode, "Global admin can inspect branches");

                    HttpResponseMessage bareCreate = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-branch-bare-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        RepoUrl = "file:///branch-bare",
                        LocalPath = bare,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, bareCreate.StatusCode, "Ordinary owner creates bare repository vessel");
                    bareVesselId = (await JsonHelper.DeserializeAsync<Vessel>(bareCreate).ConfigureAwait(false)).Id;
                    HttpResponseMessage bareResponse = await _ClientA.GetAsync("/api/v1/vessels/" + bareVesselId + "/branches").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, bareResponse.StatusCode, "Tenant admin can inspect bare repository");
                    BranchListResponse bareListing = await JsonHelper.DeserializeAsync<BranchListResponse>(bareResponse).ConfigureAwait(false);
                    AssertEqual("LocalPath", bareListing.Source, "Bare repository source");
                    AssertEqual("bare", bareListing.HeadState, "Bare repository HEAD state");
                    AssertEqual("main", bareListing.HeadRef, "Bare repository symbolic HEAD");
                    AssertEqual(2, bareListing.BranchCount, "Bare repository branch count");
                    string bareRefsAfter = await RunGitAsync(bare, "show-ref").ConfigureAwait(false);
                    AssertEqual(bareRefsBefore, bareRefsAfter, "Bare API inspection preserves repository refs");

                    string detached = Path.Combine(root, "detached");
                    await RunGitAsync(working, "worktree", "add", "--detach", detached, "feature/ünusual.name").ConfigureAwait(false);
                    HttpResponseMessage detachedCreate = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-branch-detached-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        RepoUrl = "file:///branch-detached",
                        LocalPath = detached,
                        DefaultBranch = "missing-default"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, detachedCreate.StatusCode, "Ordinary owner creates detached repository vessel");
                    Vessel detachedVessel = await JsonHelper.DeserializeAsync<Vessel>(detachedCreate).ConfigureAwait(false);
                    BranchListResponse detachedListing = await JsonHelper.DeserializeAsync<BranchListResponse>(await _ClientA.GetAsync("/api/v1/vessels/" + detachedVessel.Id + "/branches").ConfigureAwait(false)).ConfigureAwait(false);
                    AssertEqual("detached", detachedListing.HeadState, "Detached repository HEAD state (error=" + (detachedListing.Error ?? "<null>") + ")");
                    AssertTrue(detachedListing.HeadRef == null, "Detached repository has no symbolic HEAD ref");
                    AssertTrue(detachedListing.Branches[0].DivergenceError != null, "Missing default branch reports unknown divergence");

                    string unrelated = Path.Combine(root, "unrelated.git");
                    await RunGitAsync(root, "clone", "--bare", working, unrelated).ConfigureAwait(false);
                    string missingPath = Path.Combine(root, "missing");
                    HttpResponseMessage missingCreate = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "../" + Path.GetFileName(unrelated),
                        RepoUrl = "file:///branch-missing",
                        LocalPath = missingPath,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, missingCreate.StatusCode, "Ordinary owner creates missing repository vessel");
                    Vessel missingVessel = await JsonHelper.DeserializeAsync<Vessel>(missingCreate).ConfigureAwait(false);
                    BranchListResponse missingListing = await JsonHelper.DeserializeAsync<BranchListResponse>(await _ClientA.GetAsync("/api/v1/vessels/" + missingVessel.Id + "/branches").ConfigureAwait(false)).ConfigureAwait(false);
                    AssertEqual("unavailable", missingListing.Source, "Missing repository source");
                    AssertEqual("unknown", missingListing.HeadState, "Missing repository head state");
                    AssertEqual("No repository found for this vessel", missingListing.Error, "Missing repository error");
                    AssertEqual(0, missingListing.BranchCount, "Missing repository has no branches");

                    string corrupt = Path.Combine(root, "corrupt");
                    Directory.CreateDirectory(corrupt);
                    await RunGitAsync(corrupt, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(corrupt, "config", "user.name", "Armada API Tests").ConfigureAwait(false);
                    await RunGitAsync(corrupt, "config", "user.email", "armada-api-tests@example.test").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(corrupt, "corrupt.txt"), "corrupt\n").ConfigureAwait(false);
                    await RunGitAsync(corrupt, "add", "corrupt.txt").ConfigureAwait(false);
                    await RunGitAsync(corrupt, "commit", "-m", "Corrupt HEAD base").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(corrupt, ".git", "HEAD"), "ref: refs/heads/no-such-branch\n").ConfigureAwait(false);
                    HttpResponseMessage corruptCreate = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-branch-corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        RepoUrl = "file:///branch-corrupt",
                        LocalPath = corrupt,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, corruptCreate.StatusCode, "Tenant admin creates corrupt HEAD vessel");
                    Vessel corruptVessel = await JsonHelper.DeserializeAsync<Vessel>(corruptCreate).ConfigureAwait(false);
                    HttpResponseMessage corruptResponse = await _ClientA.GetAsync("/api/v1/vessels/" + corruptVessel.Id + "/branches").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, corruptResponse.StatusCode, "Corrupt HEAD inspection returns a response");
                    BranchListResponse corruptListing = await JsonHelper.DeserializeAsync<BranchListResponse>(corruptResponse).ConfigureAwait(false);
                    AssertEqual("unknown", corruptListing.HeadState, "Corrupt HEAD is not reported as detached");
                    AssertEqual("Git branch inspection failed.", corruptListing.Error, "Corrupt HEAD error is explicit");

                    string refsAfter = await RunGitAsync(working, "show-ref").ConfigureAwait(false);
                    AssertEqual(refsBefore, refsAfter, "API inspection preserves repository refs");
                }
                finally
                {
                    if (Directory.Exists(Path.Combine(root, "detached"))) await RunGitAsync(working, "worktree", "remove", "--force", Path.Combine(root, "detached")).ConfigureAwait(false);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("Vessel_BranchWrites_RequireTenantAdministratorAndPushOnlyToOrigin", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-branch-write-api-" + Guid.NewGuid().ToString("N"));
                string source = Path.Combine(root, "source");
                string origin = Path.Combine(root, "origin.git");
                string landing = Path.Combine(root, "landing.git");
                try
                {
                    Directory.CreateDirectory(source);
                    await RunGitAsync(source, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(source, "config", "user.name", "Armada API Tests").ConfigureAwait(false);
                    await RunGitAsync(source, "config", "user.email", "armada-api-tests@example.test").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "main.txt"), "main\n").ConfigureAwait(false);
                    await RunGitAsync(source, "add", "main.txt").ConfigureAwait(false);
                    await RunGitAsync(source, "commit", "-m", "Write base").ConfigureAwait(false);
                    string baseCommit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(source, "checkout", "-b", "feature/api-write").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "feature.txt"), "feature\n").ConfigureAwait(false);
                    await RunGitAsync(source, "add", "feature.txt").ConfigureAwait(false);
                    await RunGitAsync(source, "commit", "-m", "Write feature").ConfigureAwait(false);
                    string featureCommit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(root, "init", "--bare", "-b", "main", origin).ConfigureAwait(false);
                    await RunGitAsync(source, "push", origin, "main").ConfigureAwait(false);
                    await RunGitAsync(root, "clone", "--bare", origin, landing).ConfigureAwait(false);
                    await RunGitAsync(landing, "fetch", source, "feature/api-write:refs/heads/feature/api-write").ConfigureAwait(false);

                    HttpResponseMessage create = await _ClientA!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-branch-write-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        RepoUrl = origin,
                        LocalPath = landing,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, create.StatusCode, "Tenant admin creates write vessel");
                    string vesselId = (await JsonHelper.DeserializeAsync<Vessel>(create).ConfigureAwait(false)).Id;
                    string branchesPath = "/api/v1/vessels/" + vesselId + "/branches";

                    BranchListResponse listing = await JsonHelper.DeserializeAsync<BranchListResponse>(await _ClientA.GetAsync(branchesPath).ConfigureAwait(false)).ConfigureAwait(false);
                    AssertTrue(listing.WriteControls.PushAvailable, "Tenant admin sees push (reason=" + (listing.WriteControls.PushUnavailableReason ?? "<null>") + ")");
                    AssertTrue(listing.WriteControls.MergeAvailable, "Tenant admin sees merge");
                    AssertEqual("origin", listing.WriteControls.Remote, "Push remote");

                    object push = new { SourceRef = "feature/api-write", TargetRef = "main", Remote = "origin" };
                    AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(push)).ConfigureAwait(false)).StatusCode, "Anonymous push denied");
                    AssertEqual(HttpStatusCode.Forbidden, (await _ClientA3!.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(push)).ConfigureAwait(false)).StatusCode, "Same-tenant non-administrator push denied");
                    AssertEqual(HttpStatusCode.Forbidden, (await _ClientA3.PostAsync(branchesPath + "/merge", JsonHelper.ToJsonContent(new { SourceRef = "feature/api-write", TargetRef = "main", Strategy = "FastForward" })).ConfigureAwait(false)).StatusCode, "Same-tenant non-administrator merge denied");
                    AssertEqual(HttpStatusCode.NotFound, (await _ClientB!.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(push)).ConfigureAwait(false)).StatusCode, "Other tenant administrator push denied");
                    AssertEqual(baseCommit, (await RunGitAsync(origin, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(), "Denied pushes leave origin unchanged");

                    HttpResponseMessage upstream = await _ClientA.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(new { SourceRef = "feature/api-write", TargetRef = "main", Remote = "upstream" })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, upstream.StatusCode, "Non-origin remote rejected");
                    AssertEqual("remote_not_allowed", (await JsonHelper.DeserializeAsync<BranchWriteResult>(upstream).ConfigureAwait(false)).Reason, "Non-origin remote reason");

                    HttpResponseMessage invalid = await _ClientA.PostAsync(branchesPath + "/merge", JsonHelper.ToJsonContent(new { SourceRef = "bad..ref", TargetRef = "main", Strategy = "FastForward" })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, invalid.StatusCode, "Invalid ref rejected");
                    AssertEqual("invalid_ref", (await JsonHelper.DeserializeAsync<BranchWriteResult>(invalid).ConfigureAwait(false)).Reason, "Invalid ref reason");

                    HttpResponseMessage pushed = await _ClientA.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(push)).ConfigureAwait(false);
                    BranchWriteResult pushResult = await JsonHelper.DeserializeAsync<BranchWriteResult>(pushed).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, pushed.StatusCode, "Tenant admin push succeeds (reason=" + (pushResult.Reason ?? "<null>") + ": " + pushResult.Message + ")");
                    AssertEqual(featureCommit, pushResult.TargetCommit, "Verified pushed commit");
                    AssertEqual(featureCommit, (await RunGitAsync(origin, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(), "Origin main advanced");

                    HttpResponseMessage again = await _ClientA.PostAsync(branchesPath + "/push", JsonHelper.ToJsonContent(push)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Conflict, again.StatusCode, "Repeated push refused");
                    AssertEqual("nothing_to_write", (await JsonHelper.DeserializeAsync<BranchWriteResult>(again).ConfigureAwait(false)).Reason, "Repeated push reason");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            #endregion

            #region Mission-Isolation

            string missionAId = null!;

            await RunTest("Mission_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-mission-A-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        VesselId = vesselAId
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                Mission mission;
                if (wrapper.Mission != null)
                    mission = wrapper.Mission;
                else
                    mission = JsonHelper.Deserialize<Mission>(body);

                AssertNotNull(mission.Id, "Mission ID");
                missionAId = mission.Id;
            }).ConfigureAwait(false);

            await RunTest("Mission_ListFromTenantA_ContainsMission", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/missions").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(m => m.Id == missionAId);
                AssertTrue(found, "Expected mission " + missionAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Mission_ListFromTenantB_DoesNotContainMission", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/missions").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(m => m.Id == missionAId);
                AssertFalse(found, "Expected mission " + missionAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Mission_DefinitionOfDoneReport_PreservesAuthorization", async () =>
            {
                string url = "/api/v1/missions/" + missionAId + "/definition-of-done";
                HttpResponseMessage own = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, own.StatusCode, "Owning tenant reads the report");
                MissionDefinitionOfDoneReport report = await JsonHelper.DeserializeAsync<MissionDefinitionOfDoneReport>(own).ConfigureAwait(false);
                AssertEqual(missionAId, report.MissionId, "Report mission");
                AssertEqual(Armada.Core.Enums.RecordedHistoryStateEnum.NotRecorded, report.HistoryState, "A new mission has no recorded evaluation");

                HttpResponseMessage denied = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode, "Another tenant cannot read the report");

                HttpResponseMessage anonymous = await _UnauthClient.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "Unauthenticated callers are refused");
            }).ConfigureAwait(false);

            await RunTest("Mission_AutoLandReport_PreservesAuthorization", async () =>
            {
                string url = "/api/v1/missions/" + missionAId + "/auto-land";
                HttpResponseMessage own = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, own.StatusCode, "Owning tenant reads the auto-land report");
                MissionAutoLandReport report = await JsonHelper.DeserializeAsync<MissionAutoLandReport>(own).ConfigureAwait(false);
                AssertEqual(missionAId, report.MissionId, "Report mission");
                AssertEqual(Armada.Core.Enums.RecordedHistoryStateEnum.NotRecorded, report.DecisionState, "A new mission has no auto-land decision");

                HttpResponseMessage denied = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode, "Another tenant cannot read the auto-land report");

                HttpResponseMessage anonymous = await _UnauthClient.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "Unauthenticated callers are refused");
            }).ConfigureAwait(false);

            await RunTest("Mission_StatusChangeEvent_IsVisibleToOwningTenantOnly", async () =>
            {
                HttpResponseMessage created = await _ClientA!.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-event-scope-A-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        VesselId = vesselAId
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode, "Tenant A creates a mission");
                string body = await created.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                Mission mission = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);

                HttpResponseMessage cancelled = await _ClientA!.PutAsync("/api/v1/missions/" + mission.Id + "/status",
                    JsonHelper.ToJsonContent(new { Status = "Cancelled" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, cancelled.StatusCode, "Tenant A cancels its mission");

                string url = "/api/v1/events?type=mission.status_changed&missionId=" + mission.Id;
                HttpResponseMessage own = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, own.StatusCode);
                EnumerationResult<ArmadaEvent> ownEvents = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(own).ConfigureAwait(false);
                AssertEqual(1, ownEvents.Objects.Count, "The owning tenant must see the status change event");
                AssertEqual(_TenantAId, ownEvents.Objects[0].TenantId, "The event carries the mission owner's tenant");

                HttpResponseMessage foreign = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, foreign.StatusCode);
                EnumerationResult<ArmadaEvent> foreignEvents = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(foreign).ConfigureAwait(false);
                AssertEqual(0, foreignEvents.Objects.Count, "Another tenant must not see the status change event");
            }).ConfigureAwait(false);

            await RunTest("Mission_RecoveryReport_PreservesAuthorization", async () =>
            {
                string url = "/api/v1/missions/" + missionAId + "/recovery";
                HttpResponseMessage own = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, own.StatusCode, "Owning tenant reads the recovery report");
                MissionRecoveryReport report = await JsonHelper.DeserializeAsync<MissionRecoveryReport>(own).ConfigureAwait(false);
                AssertEqual(missionAId, report.MissionId, "Report mission");
                AssertEqual(0, report.RecoveryAttempts, "A new mission has no recovery attempts");

                HttpResponseMessage denied = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode, "Another tenant cannot read the recovery report");

                HttpResponseMessage anonymous = await _UnauthClient.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "Unauthenticated callers are refused");
            }).ConfigureAwait(false);

            await RunTest("Mission_FormattedLog_PreservesAuthorization", async () =>
            {
                string url = "/api/v1/missions/" + missionAId + "/log?formatted=true";
                HttpResponseMessage denied = await _ClientB!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, denied.StatusCode);
                denied = await _UnauthClient.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                HttpResponseMessage allowed = await _ClientA!.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, allowed.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Mission_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/missions/" + missionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Mission_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/missions/" + missionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Mission_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/missions/" + missionAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Mission mission = JsonHelper.Deserialize<Mission>(body);
                AssertEqual(missionAId, mission.Id);
            }).ConfigureAwait(false);

            #endregion

            #region Voyage-Isolation

            string voyageAId = null!;

            await RunTest("Voyage_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/voyages",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-voyage-A-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(response).ConfigureAwait(false);
                AssertNotNull(voyage.Id, "Voyage ID");
                voyageAId = voyage.Id;
            }).ConfigureAwait(false);

            await RunTest("Voyage_ListFromTenantA_ContainsVoyage", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/voyages").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Voyage> result = await JsonHelper.DeserializeAsync<EnumerationResult<Voyage>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(v => v.Id == voyageAId);
                AssertTrue(found, "Expected voyage " + voyageAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Voyage_Summary_UsesAuthenticatedUserAndTenantScope", async () =>
            {
                string? userId = null;
                string? credentialId = null;
                string? ownVoyageId = null;
                List<Exception> failures = new List<Exception>();
                try
                {
                    UserMaster user;
                    using (StringContent content = JsonHelper.ToJsonContent(new
                    {
                        TenantId = _TenantAId, Email = "summary-" + Guid.NewGuid().ToString("N") + "@example.test",
                        PasswordSha256 = UserMaster.ComputePasswordHash("summary-fixture"), IsTenantAdmin = true
                    }))
                    using (HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/users", content))
                    {
                        AssertEqual(HttpStatusCode.Created, response.StatusCode);
                        user = await JsonHelper.DeserializeAsync<UserMaster>(response);
                        userId = user.Id;
                    }
                    Credential credential;
                    using (StringContent content = JsonHelper.ToJsonContent(new { TenantId = _TenantAId, UserId = userId, Name = "summary" }))
                    using (HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/credentials", content))
                    {
                        AssertEqual(HttpStatusCode.Created, response.StatusCode);
                        credential = await JsonHelper.DeserializeAsync<Credential>(response);
                        credentialId = credential.Id;
                    }
                    using (HttpClient client = CreateBearerClient(credential.BearerToken))
                    {
                        using (StringContent content = JsonHelper.ToJsonContent(new { Title = "Summary owned voyage" }))
                        using (HttpResponseMessage response = await client.PostAsync("/api/v1/voyages", content))
                        {
                            AssertEqual(HttpStatusCode.Created, response.StatusCode);
                            ownVoyageId = (await JsonHelper.DeserializeAsync<Voyage>(response)).Id;
                        }
                        user.IsTenantAdmin = false;
                        using (StringContent content = JsonHelper.ToJsonContent(user))
                        using (HttpResponseMessage response = await _AdminClient.PutAsync("/api/v1/users/" + user.Id, content))
                            AssertEqual(HttpStatusCode.OK, response.StatusCode);
                        using (HttpResponseMessage response = await client.GetAsync("/api/v1/whoami"))
                            AssertFalse((await JsonHelper.DeserializeAsync<WhoAmIResult>(response)).User!.IsTenantAdmin);

                        using (HttpResponseMessage response = await client.GetAsync("/api/v1/voyages/" + ownVoyageId + "/mission-summary"))
                            AssertEqual(HttpStatusCode.OK, response.StatusCode);
                        using (HttpResponseMessage response = await client.GetAsync("/api/v1/voyages/" + voyageAId + "/mission-summary?tenantId=" + _TenantAId + "&userId=" + _UserAId))
                            AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "A query cannot override authenticated user scope");
                        using (HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/voyages/" + ownVoyageId + "/mission-summary"))
                            AssertEqual(HttpStatusCode.OK, response.StatusCode, "Tenant admin can read another user's voyage");
                        using (HttpResponseMessage response = await _AdminClient.GetAsync("/api/v1/voyages/" + ownVoyageId + "/mission-summary"))
                            AssertEqual(HttpStatusCode.OK, response.StatusCode, "Global admin can read the voyage");
                        using (HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/voyages/" + ownVoyageId + "/mission-summary?tenantId=" + _TenantAId))
                            AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Foreign tenant cannot override scope");
                    }
                }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    foreach (string path in new[]
                    {
                        ownVoyageId == null ? "" : "/api/v1/voyages/" + ownVoyageId,
                        ownVoyageId == null ? "" : "/api/v1/voyages/" + ownVoyageId + "/purge",
                        credentialId == null ? "" : "/api/v1/credentials/" + credentialId,
                        userId == null ? "" : "/api/v1/users/" + userId
                    })
                    {
                        if (path.Length == 0) continue;
                        try
                        {
                            using (HttpResponseMessage response = await _AdminClient.DeleteAsync(path))
                                AssertEqual(HttpStatusCode.OK, response.StatusCode, "Summary fixture cleanup");
                        }
                        catch (Exception exception) { failures.Add(exception); }
                    }
                    if (failures.Count > 0) throw new AggregateException("Summary scope test or cleanup failed", failures);
                }
            }).ConfigureAwait(false);

            await RunTest("Voyage_ListFromTenantB_DoesNotContainVoyage", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/voyages").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Voyage> result = await JsonHelper.DeserializeAsync<EnumerationResult<Voyage>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(v => v.Id == voyageAId);
                AssertFalse(found, "Expected voyage " + voyageAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Voyage_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/voyages/" + voyageAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Voyage_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/voyages/" + voyageAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Voyage_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/voyages/" + voyageAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                VoyageDetailResponse detail = await JsonHelper.DeserializeAsync<VoyageDetailResponse>(response).ConfigureAwait(false);
                AssertEqual(voyageAId, detail.Voyage!.Id);
            }).ConfigureAwait(false);

            #endregion

            #region Signal-Isolation

            string signalAId = null!;

            await RunTest("Signal_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/signals",
                    JsonHelper.ToJsonContent(new
                    {
                        Type = "Nudge",
                        Payload = "xt-signal-payload",
                        ToCaptainId = captainAId
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Signal signal = await JsonHelper.DeserializeAsync<Signal>(response).ConfigureAwait(false);
                AssertNotNull(signal.Id, "Signal ID");
                signalAId = signal.Id;
            }).ConfigureAwait(false);

            await RunTest("Signal_ListFromTenantA_ContainsSignal", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/signals?toCaptainId=" + captainAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Signal> result = await JsonHelper.DeserializeAsync<EnumerationResult<Signal>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(s => s.Id == signalAId);
                AssertTrue(found, "Expected signal " + signalAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Signal_ListFromTenantB_DoesNotContainSignal", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/signals").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Signal> result = await JsonHelper.DeserializeAsync<EnumerationResult<Signal>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(s => s.Id == signalAId);
                AssertFalse(found, "Expected signal " + signalAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("Signal_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/signals/" + signalAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            #endregion

            #region MergeQueue-Isolation

            string mergeEntryAId = null!;

            await RunTest("MergeQueue_EnqueueInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/merge-queue",
                    JsonHelper.ToJsonContent(new
                    {
                        MissionId = missionAId,
                        VesselId = vesselAId,
                        BranchName = "feature/xt-merge-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        TargetBranch = "main"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                MergeEntry entry = await JsonHelper.DeserializeAsync<MergeEntry>(response).ConfigureAwait(false);
                AssertNotNull(entry.Id, "MergeEntry ID");
                mergeEntryAId = entry.Id;
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_ListFromTenantA_ContainsEntry", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/merge-queue").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(e => e.Id == mergeEntryAId);
                AssertTrue(found, "Expected merge entry " + mergeEntryAId + " to appear in tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_ListFromTenantB_DoesNotContainEntry", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/merge-queue").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);
                bool found = result.Objects.Any(e => e.Id == mergeEntryAId);
                AssertFalse(found, "Expected merge entry " + mergeEntryAId + " NOT to appear in tenant-B list");
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_StillExistsInTenantA_AfterTenantBDeleteAttempt", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                MergeEntry entry = await JsonHelper.DeserializeAsync<MergeEntry>(response).ConfigureAwait(false);
                AssertEqual(mergeEntryAId, entry.Id);
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_ProcessFromTenantBAdmin_IsForbiddenAndLeavesTenantAEntryQueued", async () =>
            {
                HttpResponseMessage process = await _ClientB!.PostAsync("/api/v1/merge-queue/process", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, process.StatusCode, "Processing the whole queue acts on every tenant, so a tenant administrator is refused");
                ArmadaErrorResponse body = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(process).ConfigureAwait(false);
                AssertEqual("Forbidden", body.Error, "403 body names Forbidden");

                MergeEntry entry = await JsonHelper.DeserializeAsync<MergeEntry>(
                    await _ClientA!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(Armada.Core.Enums.MergeStatusEnum.Queued, entry.Status, "Tenant A's entry is not processed by tenant B's request");
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_BatchPurgeFromTenantBAdmin_DoesNotPurgeTenantATerminalEntry", async () =>
            {
                // Tenant A cancels its own entry, which makes it terminal and so purgeable.
                HttpResponseMessage cancel = await _ClientA!.DeleteAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, cancel.StatusCode);
                MergeEntry cancelled = await JsonHelper.DeserializeAsync<MergeEntry>(
                    await _ClientA!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(Armada.Core.Enums.MergeStatusEnum.Cancelled, cancelled.Status, "Tenant A's entry is terminal before the purge attempt");

                HttpResponseMessage purge = await _ClientB!.PostAsync("/api/v1/merge-queue/purge",
                    JsonHelper.ToJsonContent(new { EntryIds = new[] { mergeEntryAId } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, purge.StatusCode);
                MergeQueuePurgeResult result = await JsonHelper.DeserializeAsync<MergeQueuePurgeResult>(purge).ConfigureAwait(false);
                AssertEqual(0, result.EntriesPurged, "A tenant administrator must not purge another tenant's entry");
                AssertTrue(result.Skipped.Any(s => s.EntryId == mergeEntryAId && s.Reason == "Not found"),
                    "Another tenant's entry is reported as not found");

                HttpResponseMessage stillThere = await _ClientA!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, stillThere.StatusCode, "Tenant A's entry survives tenant B's batch purge");
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_BatchPurgeFromSystemAdmin_PurgesAnyTenantEntry", async () =>
            {
                HttpResponseMessage purge = await _AdminClient.PostAsync("/api/v1/merge-queue/purge",
                    JsonHelper.ToJsonContent(new { EntryIds = new[] { mergeEntryAId } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, purge.StatusCode);
                MergeQueuePurgeResult result = await JsonHelper.DeserializeAsync<MergeQueuePurgeResult>(purge).ConfigureAwait(false);
                AssertEqual(1, result.EntriesPurged, "A system administrator purges a terminal entry of any tenant");

                HttpResponseMessage gone = await _ClientA!.GetAsync("/api/v1/merge-queue/" + mergeEntryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, gone.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("MergeQueue_AuthRefusals_BodyErrorMatchesStatus", async () =>
            {
                HttpResponseMessage unauthenticated = await _UnauthClient.PostAsync("/api/v1/merge-queue/purge",
                    JsonHelper.ToJsonContent(new { EntryIds = new[] { "mrg_example" } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
                ArmadaErrorResponse unauthenticatedBody = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(unauthenticated).ConfigureAwait(false);
                AssertEqual("NotAuthorized", unauthenticatedBody.Error, "401 body names NotAuthorized");

                HttpResponseMessage forbidden = await _ClientA3!.PostAsync("/api/v1/merge-queue/purge",
                    JsonHelper.ToJsonContent(new { EntryIds = new[] { "mrg_example" } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
                ArmadaErrorResponse forbiddenBody = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(forbidden).ConfigureAwait(false);
                AssertEqual("Forbidden", forbiddenBody.Error, "403 body names Forbidden");
            }).ConfigureAwait(false);

            await RunTest("RouteAuthRefusals_BodyErrorMatchesStatusOnEveryRouteFile", async () =>
            {
                // One representative refusal per route file: an anonymous caller must be refused with 401 and
                // NotAuthorized, and an authenticated caller without permission with 403 and Forbidden. The harbor
                // runner routes are served only when harbor runners are enabled, which this harness does not do.
                List<AuthRefusalProbe> probes = new List<AuthRefusalProbe>
                {
                    new AuthRefusalProbe("AskRoutes", "POST", "/api/v1/ask", false),
                    new AuthRefusalProbe("AskRoutes", "POST", "/api/v1/ask", true),
                    new AuthRefusalProbe("AuthRoutes", "GET", "/api/v1/whoami", false),
                    new AuthRefusalProbe("BackupRoutes", "GET", "/api/v1/backup", false),
                    new AuthRefusalProbe("BackupRoutes", "GET", "/api/v1/backup", true),
                    new AuthRefusalProbe("CaptainRoutes", "GET", "/api/v1/captains", false),
                    new AuthRefusalProbe("CaptainRoutes", "POST", "/api/v1/captains", true),
                    new AuthRefusalProbe("CheckRunRoutes", "GET", "/api/v1/check-runs", false),
                    new AuthRefusalProbe("CodeIndexRoutes", "GET", "/api/v1/vessels/vsl_missing/code-index/status", false),
                    new AuthRefusalProbe("CoordinationRoutes", "GET", "/api/v1/coordination/rooms", false),
                    new AuthRefusalProbe("CoordinationRoutes", "GET", "/api/v1/coordination/rooms", true),
                    new AuthRefusalProbe("DeploymentRoutes", "GET", "/api/v1/deployments", false),
                    new AuthRefusalProbe("DeploymentRoutes", "POST", "/api/v1/deployments", true),
                    new AuthRefusalProbe("DockRoutes", "GET", "/api/v1/docks", false),
                    new AuthRefusalProbe("DockRoutes", "DELETE", "/api/v1/docks/dck_missing", true),
                    new AuthRefusalProbe("EnvironmentRoutes", "GET", "/api/v1/environments", false),
                    new AuthRefusalProbe("EnvironmentRoutes", "POST", "/api/v1/environments", true),
                    new AuthRefusalProbe("EventRoutes", "GET", "/api/v1/events", false),
                    new AuthRefusalProbe("EventRoutes", "DELETE", "/api/v1/events/evt_missing", true),
                    new AuthRefusalProbe("FleetRoutes", "GET", "/api/v1/fleets", false),
                    new AuthRefusalProbe("FleetRoutes", "POST", "/api/v1/fleets", true),
                    new AuthRefusalProbe("HistoryRoutes", "GET", "/api/v1/history", false),
                    new AuthRefusalProbe("InboxRoutes", "GET", "/api/v1/inbox", false),
                    new AuthRefusalProbe("InboxRoutes", "GET", "/api/v1/inbox", true),
                    new AuthRefusalProbe("IncidentRoutes", "GET", "/api/v1/incidents", false),
                    new AuthRefusalProbe("IncidentRoutes", "POST", "/api/v1/incidents", true),
                    new AuthRefusalProbe("JobRoutes", "GET", "/api/v1/jobs", false),
                    new AuthRefusalProbe("MergeQueueRoutes", "GET", "/api/v1/merge-queue", false),
                    new AuthRefusalProbe("MergeQueueRoutes", "POST", "/api/v1/merge-queue/process", true),
                    new AuthRefusalProbe("MissionRoutes", "GET", "/api/v1/missions", false),
                    new AuthRefusalProbe("MissionRoutes", "POST", "/api/v1/missions", true),
                    new AuthRefusalProbe("ModelEndpointRoutes", "GET", "/api/v1/model-endpoints", false),
                    new AuthRefusalProbe("ObjectiveRefinementRoutes", "GET", "/api/v1/objective-refinement-sessions/ors_missing", false),
                    new AuthRefusalProbe("ObjectiveRefinementRoutes", "POST", "/api/v1/objectives/obj_missing/refinement-sessions", true),
                    new AuthRefusalProbe("ObjectiveRoutes", "GET", "/api/v1/objectives", false),
                    new AuthRefusalProbe("ObjectiveRoutes", "POST", "/api/v1/objectives", true),
                    new AuthRefusalProbe("PersonaRoutes", "GET", "/api/v1/personas", false),
                    new AuthRefusalProbe("PersonaRoutes", "POST", "/api/v1/personas", true),
                    new AuthRefusalProbe("PipelineRoutes", "GET", "/api/v1/pipelines", false),
                    new AuthRefusalProbe("PipelineRoutes", "POST", "/api/v1/pipelines", true),
                    new AuthRefusalProbe("PlanningSessionRoutes", "GET", "/api/v1/planning-sessions", false),
                    new AuthRefusalProbe("PlanningSessionRoutes", "POST", "/api/v1/planning-sessions", true),
                    new AuthRefusalProbe("PlaybookRoutes", "GET", "/api/v1/playbooks", false),
                    new AuthRefusalProbe("PlaybookRoutes", "POST", "/api/v1/playbooks", true),
                    new AuthRefusalProbe("ProductionRoutes", "GET", "/api/v1/production/summary", false),
                    new AuthRefusalProbe("ProjectProfileRoutes", "GET", "/api/v1/project-profiles", false),
                    new AuthRefusalProbe("PromptTemplateRoutes", "GET", "/api/v1/prompt-templates", false),
                    new AuthRefusalProbe("PromptTemplateRoutes", "POST", "/api/v1/prompt-templates", true),
                    new AuthRefusalProbe("ReleaseRoutes", "GET", "/api/v1/releases", false),
                    new AuthRefusalProbe("ReleaseRoutes", "POST", "/api/v1/releases", true),
                    new AuthRefusalProbe("RequestHistoryRoutes", "GET", "/api/v1/request-history", false),
                    new AuthRefusalProbe("RequestHistoryRoutes", "DELETE", "/api/v1/request-history/req_missing", true),
                    new AuthRefusalProbe("RunbookRoutes", "GET", "/api/v1/runbooks", false),
                    new AuthRefusalProbe("RunbookRoutes", "POST", "/api/v1/runbooks", true),
                    new AuthRefusalProbe("RuntimeRoutes", "GET", "/api/v1/runtimes/mux/endpoints", false),
                    new AuthRefusalProbe("SignalRoutes", "GET", "/api/v1/signals", false),
                    new AuthRefusalProbe("SignalRoutes", "POST", "/api/v1/signals", true),
                    new AuthRefusalProbe("SkillRoutes", "GET", "/api/v1/skills", false),
                    new AuthRefusalProbe("StatusRoutes", "GET", "/api/v1/status", false),
                    new AuthRefusalProbe("StatusRoutes", "GET", "/api/v1/status", true),
                    new AuthRefusalProbe("StatusRoutes", "POST", "/api/v1/settings/usage-preview", true),
                    new AuthRefusalProbe("TenantRoutes", "GET", "/api/v1/tenants", false),
                    new AuthRefusalProbe("TenantRoutes", "GET", "/api/v1/tenants", true),
                    new AuthRefusalProbe("TokenUsageRoutes", "GET", "/api/v1/token-usage/summary", false),
                    new AuthRefusalProbe("TypedDecisionRoutes", "GET", "/api/v1/typed-decisions", false),
                    new AuthRefusalProbe("TypedDecisionRoutes", "GET", "/api/v1/typed-decisions", true),
                    new AuthRefusalProbe("UsageAccountLoginRoutes", "GET", "/api/v1/usage-accounts/acct_missing/login/status", false),
                    new AuthRefusalProbe("VesselRoutes", "GET", "/api/v1/vessels", false),
                    new AuthRefusalProbe("VesselRoutes", "POST", "/api/v1/vessels", true),
                    new AuthRefusalProbe("VoyageRoutes", "GET", "/api/v1/voyages", false),
                    new AuthRefusalProbe("VoyageRoutes", "POST", "/api/v1/voyages", true),
                    new AuthRefusalProbe("WorkflowProfileRoutes", "GET", "/api/v1/workflow-profiles", false),
                    new AuthRefusalProbe("WorkflowProfileRoutes", "POST", "/api/v1/workflow-profiles", true),
                    new AuthRefusalProbe("WorkspaceRoutes", "GET", "/api/v1/workspace/vessels/vsl_missing/tree", false),
                };

                List<string> failures = new List<string>();
                foreach (AuthRefusalProbe probe in probes)
                {
                    HttpClient client = probe.AsOrdinaryUser ? _ClientA3! : _UnauthClient;
                    using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path))
                    {
                        if (probe.Method != "GET" && probe.Method != "DELETE")
                            request.Content = JsonHelper.ToJsonContent(new { });
                        HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                        HttpStatusCode expectedStatus = probe.AsOrdinaryUser ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
                        string expectedError = probe.AsOrdinaryUser ? "Forbidden" : "NotAuthorized";
                        string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        string where = probe.RouteFile + " " + probe.Method + " " + probe.Path + (probe.AsOrdinaryUser ? " (ordinary user)" : " (anonymous)");
                        if (response.StatusCode != expectedStatus)
                        {
                            failures.Add(where + ": expected " + (int)expectedStatus + " but got " + (int)response.StatusCode + " " + raw);
                            continue;
                        }

                        ArmadaErrorResponse? body;
                        try
                        {
                            body = System.Text.Json.JsonSerializer.Deserialize<ArmadaErrorResponse>(raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            failures.Add(where + ": body is not an error document (" + ex.Message + "): " + raw);
                            continue;
                        }

                        if (body == null || body.Error != expectedError)
                            failures.Add(where + ": body error expected " + expectedError + " but got " + (body?.Error ?? "<none>") + " " + raw);
                    }
                }

                AssertTrue(failures.Count == 0, "Every auth refusal names the same outcome as its status:\n" + String.Join("\n", failures));
            }).ConfigureAwait(false);

            #endregion

            #region Event-Isolation

            await RunTest("Event_ListFromTenantA_DoesNotContainTenantBEvents", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/events").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response).ConfigureAwait(false);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertNotEqual(_TenantBId, evt.TenantId, "Tenant-A event list must not contain tenant-B events");
                }
            }).ConfigureAwait(false);

            await RunTest("Event_ListFromTenantB_DoesNotContainTenantAEvents", async () =>
            {
                HttpResponseMessage createResp = await _ClientB!.PostAsync("/api/v1/fleets",
                    JsonHelper.ToJsonContent(new { Name = "xt-event-fleet-B-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);

                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/events").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response).ConfigureAwait(false);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertNotEqual(_TenantAId, evt.TenantId, "Tenant-B event list must not contain tenant-A events");
                }
            }).ConfigureAwait(false);

            #endregion

            #region Memory-Isolation

            string memoryAId = null!;

            await RunTest("Memory_CreateInTenantA_Returns201", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/memories",
                    JsonHelper.ToJsonContent(new
                    {
                        Type = "Semantic",
                        Topic = "cross-tenant",
                        Key = "cross-tenant/" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Content = "A finding owned by tenant A"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Memory memory = await JsonHelper.DeserializeAsync<Memory>(response).ConfigureAwait(false);
                AssertNotNull(memory.Id, "Memory ID");
                memoryAId = memory.Id;
            }).ConfigureAwait(false);

            await RunTest("Memory_ListFromTenantA_ContainsMemory", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/memories").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Memory> result = await JsonHelper.DeserializeAsync<EnumerationResult<Memory>>(response).ConfigureAwait(false);
                AssertTrue(result.Objects.Any(memory => memory.Id == memoryAId), "Expected the record in the tenant-A list");
            }).ConfigureAwait(false);

            await RunTest("Memory_ListFromTenantB_DoesNotContainMemory", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/memories").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Memory> result = await JsonHelper.DeserializeAsync<EnumerationResult<Memory>>(response).ConfigureAwait(false);
                AssertFalse(result.Objects.Any(memory => memory.Id == memoryAId), "Tenant B must not see the tenant-A record");
            }).ConfigureAwait(false);

            await RunTest("Memory_ReadFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.GetAsync("/api/v1/memories/" + memoryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Memory_DeleteFromTenantB_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/memories/" + memoryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("Memory_StillReadableInTenantA_AfterTenantBAttempts", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/memories/" + memoryAId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                Memory memory = await JsonHelper.DeserializeAsync<Memory>(response).ConfigureAwait(false);
                AssertEqual("A finding owned by tenant A", memory.Content, "The record is unchanged");
            }).ConfigureAwait(false);

            #endregion

            #region Shared-Asset-Authorization

            // Personas, pipelines and prompt templates are read by name. A tenant admin must not
            // reach a record another tenant owns, and shared prompt templates change only through
            // a global administrator.
            await RunTest("Persona_DeleteBuiltInFromOtherTenant_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/personas/Worker").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Another tenant's persona must not be reachable by name");
            }).ConfigureAwait(false);

            await RunTest("Pipeline_DeleteBuiltInFromOtherTenant_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/pipelines/FullPipeline").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Another tenant's pipeline must not be reachable by name");
            }).ConfigureAwait(false);

            await RunTest("Persona_CreateFromTenantAdmin_RecordsCallerTenant", async () =>
            {
                string name = "xt-persona-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage created = await _ClientA!.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = name, Description = "tenant A persona", PromptTemplateName = "persona.worker", TenantId = "default" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);
                Persona persona = await JsonHelper.DeserializeAsync<Persona>(created).ConfigureAwait(false);
                AssertEqual(_TenantAId, persona.TenantId, "The server records the caller's tenant, not the body's");

                HttpResponseMessage fromB = await _ClientB!.DeleteAsync("/api/v1/personas/" + name).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, fromB.StatusCode, "Tenant B must not delete tenant A's persona");

                HttpResponseMessage fromA = await _ClientA!.DeleteAsync("/api/v1/personas/" + name).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, fromA.StatusCode, "Tenant A deletes its own persona");
            }).ConfigureAwait(false);

            await RunTest("PromptTemplate_ResetFromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/prompt-templates/mission.rules/reset", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "Shared prompt templates change only through a global administrator");
            }).ConfigureAwait(false);

            await RunTest("PromptTemplate_UpdateFromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PutAsync("/api/v1/prompt-templates/mission.rules",
                    JsonHelper.ToJsonContent(new { Description = "tenant A edit" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "Shared prompt templates change only through a global administrator");
            }).ConfigureAwait(false);

            #endregion

            #region Owned-Asset-Read-Scope

            // Reads follow the shared ownership rule: nobody but a global administrator crosses a
            // tenant, a user-specific record is visible only to its owner and tenant administrators,
            // and built-in records stay readable to every authenticated caller.
            string tenantWidePersona = "xt-tw-persona-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string privatePersona = "xt-us-persona-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tenantWidePipeline = "xt-tw-pipeline-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string privatePipeline = "xt-us-pipeline-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string adminTemplate = "xt.template." + Guid.NewGuid().ToString("N").Substring(0, 8);

            // This region creates its own owner and reader, so earlier tests that change the shared
            // tenant A users cannot decide its outcome.
            HttpClient? ownerClient = null;
            HttpClient? readerClient = null;
            string? ownerUserId = null;

            await RunTest("OwnedAssets_Setup_CreateTenantWideAndPrivateRecords", async () =>
            {
                TenantUserCredentialResult owner = await CreateUserCredentialAsync(_TenantAId!, "owned-owner", true).ConfigureAwait(false);
                TenantUserCredentialResult reader = await CreateUserCredentialAsync(_TenantAId!, "owned-reader").ConfigureAwait(false);
                ownerClient = CreateBearerClient(owner.BearerToken);
                readerClient = CreateBearerClient(reader.BearerToken);
                ownerUserId = owner.UserId;

                HttpResponseMessage tw = await _ClientA!.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = tenantWidePersona, PromptTemplateName = "persona.worker" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, tw.StatusCode, "Tenant-wide persona created");

                HttpResponseMessage us = await ownerClient.PostAsync("/api/v1/personas",
                    JsonHelper.ToJsonContent(new { Name = privatePersona, PromptTemplateName = "persona.worker", OwnershipScope = "UserSpecific", UserId = _UserAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, us.StatusCode, "User-specific persona created");
                Persona created = await JsonHelper.DeserializeAsync<Persona>(us).ConfigureAwait(false);
                AssertEqual(ownerUserId, created.UserId, "The server records the creating user, not the body's");

                HttpResponseMessage twp = await _ClientA!.PostAsync("/api/v1/pipelines",
                    JsonHelper.ToJsonContent(new { Name = tenantWidePipeline, Stages = new[] { new { Order = 1, PersonaName = "Worker" } } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, twp.StatusCode, "Tenant-wide pipeline created");

                HttpResponseMessage usp = await ownerClient.PostAsync("/api/v1/pipelines",
                    JsonHelper.ToJsonContent(new { Name = privatePipeline, OwnershipScope = "UserSpecific", Stages = new[] { new { Order = 1, PersonaName = "Worker" } } })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, usp.StatusCode, "User-specific pipeline created");

                HttpResponseMessage tpl = await _AdminClient.PostAsync("/api/v1/prompt-templates",
                    JsonHelper.ToJsonContent(new { Name = adminTemplate, Category = "mission", Content = "global admin template" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, tpl.StatusCode, "Default-tenant template created");
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_AnonymousAndInvalidCredential_Return401", async () =>
            {
                foreach (string path in new[] { "/api/v1/personas", "/api/v1/pipelines", "/api/v1/prompt-templates", "/api/v1/personas/Worker" })
                {
                    HttpResponseMessage anonymous = await _UnauthClient.GetAsync(path).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "Anonymous read of " + path);
                    using (HttpClient invalid = CreateBearerClient("invalid-" + Guid.NewGuid().ToString("N")))
                    {
                        HttpResponseMessage response = await invalid.GetAsync(path).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode, "Invalid credential read of " + path);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_OtherTenant_CannotReadTenantRecords", async () =>
            {
                AssertEqual(HttpStatusCode.NotFound, (await _ClientB!.GetAsync("/api/v1/personas/" + tenantWidePersona).ConfigureAwait(false)).StatusCode, "Tenant B persona detail");
                AssertEqual(HttpStatusCode.NotFound, (await _ClientB!.GetAsync("/api/v1/pipelines/" + tenantWidePipeline).ConfigureAwait(false)).StatusCode, "Tenant B pipeline detail");
                AssertEqual(HttpStatusCode.NotFound, (await _ClientB!.GetAsync("/api/v1/prompt-templates/" + adminTemplate).ConfigureAwait(false)).StatusCode, "Tenant B template detail");

                EnumerationResult<Persona> personas = await JsonHelper.DeserializeAsync<EnumerationResult<Persona>>(
                    await _ClientB!.GetAsync("/api/v1/personas?pageSize=1000").ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(personas.Objects.Any(p => p.Name == tenantWidePersona || p.Name == privatePersona), "Tenant B persona list");
                AssertTrue(personas.Objects.All(p => p.IsBuiltIn || p.TenantId == _TenantBId), "Tenant B lists only built-in or own-tenant personas");

                EnumerationResult<Pipeline> pipelines = await JsonHelper.DeserializeAsync<EnumerationResult<Pipeline>>(
                    await _ClientB!.PostAsync("/api/v1/pipelines/enumerate", JsonHelper.ToJsonContent(new { PageSize = 1000 })).ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(pipelines.Objects.Any(p => p.Name == tenantWidePipeline || p.Name == privatePipeline), "Tenant B pipeline enumerate");

                EnumerationResult<PromptTemplate> templates = await JsonHelper.DeserializeAsync<EnumerationResult<PromptTemplate>>(
                    await _ClientB!.GetAsync("/api/v1/prompt-templates?pageSize=1000").ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(templates.Objects.Any(t => t.Name == adminTemplate), "Tenant B template list");
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_BuiltIns_StayReadableToEveryCaller", async () =>
            {
                foreach (HttpClient client in new[] { _ClientB!, readerClient!, _AdminClient })
                {
                    AssertEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/personas/Worker").ConfigureAwait(false)).StatusCode, "Built-in persona");
                    AssertEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/pipelines/WorkerOnly").ConfigureAwait(false)).StatusCode, "Built-in pipeline");
                    AssertEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/prompt-templates/mission.rules").ConfigureAwait(false)).StatusCode, "Built-in template");
                }
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_OrdinaryUser_SeesTenantWideButNotAnotherUsersRecord", async () =>
            {
                AssertEqual(HttpStatusCode.OK, (await readerClient!.GetAsync("/api/v1/personas/" + tenantWidePersona).ConfigureAwait(false)).StatusCode, "Tenant-wide persona");
                AssertEqual(HttpStatusCode.OK, (await readerClient!.GetAsync("/api/v1/pipelines/" + tenantWidePipeline).ConfigureAwait(false)).StatusCode, "Tenant-wide pipeline");
                AssertEqual(HttpStatusCode.NotFound, (await readerClient!.GetAsync("/api/v1/personas/" + privatePersona).ConfigureAwait(false)).StatusCode, "Another user's persona");
                AssertEqual(HttpStatusCode.NotFound, (await readerClient!.GetAsync("/api/v1/pipelines/" + privatePipeline).ConfigureAwait(false)).StatusCode, "Another user's pipeline");

                EnumerationResult<Persona> personas = await JsonHelper.DeserializeAsync<EnumerationResult<Persona>>(
                    await readerClient!.PostAsync("/api/v1/personas/enumerate", JsonHelper.ToJsonContent(new { PageSize = 1000 })).ConfigureAwait(false)).ConfigureAwait(false);
                AssertTrue(personas.Objects.Any(p => p.Name == tenantWidePersona), "Tenant-wide persona listed");
                AssertFalse(personas.Objects.Any(p => p.Name == privatePersona), "Another user's persona not listed");
                AssertEqual((long)personas.Objects.Count, personas.TotalRecords, "Totals count only visible records");
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_OwnerTenantAdminAndGlobalAdmin_SeePrivateRecord", async () =>
            {
                AssertEqual(HttpStatusCode.OK, (await ownerClient!.GetAsync("/api/v1/personas/" + privatePersona).ConfigureAwait(false)).StatusCode, "Owner");
                AssertEqual(HttpStatusCode.OK, (await _ClientA!.GetAsync("/api/v1/personas/" + privatePersona).ConfigureAwait(false)).StatusCode, "Tenant administrator");
                AssertEqual(HttpStatusCode.OK, (await _AdminClient.GetAsync("/api/v1/personas/" + privatePersona).ConfigureAwait(false)).StatusCode, "Global administrator");
                AssertEqual(HttpStatusCode.OK, (await ownerClient!.GetAsync("/api/v1/pipelines/" + privatePipeline).ConfigureAwait(false)).StatusCode, "Owner pipeline");
                AssertEqual(HttpStatusCode.OK, (await _AdminClient.GetAsync("/api/v1/prompt-templates/" + adminTemplate).ConfigureAwait(false)).StatusCode, "Global administrator template");
            }).ConfigureAwait(false);

            await RunTest("OwnedAssets_Cleanup", async () =>
            {
                AssertEqual(HttpStatusCode.NoContent, (await _ClientA!.DeleteAsync("/api/v1/personas/" + privatePersona).ConfigureAwait(false)).StatusCode, "Delete private persona");
                AssertEqual(HttpStatusCode.NoContent, (await _ClientA!.DeleteAsync("/api/v1/personas/" + tenantWidePersona).ConfigureAwait(false)).StatusCode, "Delete tenant-wide persona");
                AssertEqual(HttpStatusCode.NoContent, (await _ClientA!.DeleteAsync("/api/v1/pipelines/" + privatePipeline).ConfigureAwait(false)).StatusCode, "Delete private pipeline");
                AssertEqual(HttpStatusCode.NoContent, (await _ClientA!.DeleteAsync("/api/v1/pipelines/" + tenantWidePipeline).ConfigureAwait(false)).StatusCode, "Delete tenant-wide pipeline");
                ownerClient?.Dispose();
                readerClient?.Dispose();
            }).ConfigureAwait(false);

            #endregion

            #region Fleet-Aggregate-Authorization

            // Inbox and Ask read every tenant with no caller scope, so only a global administrator
            // may call them.
            await RunTest("Inbox_FromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/inbox").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "The fleet-wide inbox requires a global administrator");
            }).ConfigureAwait(false);

            await RunTest("Ask_FromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/ask",
                    JsonHelper.ToJsonContent(new { Message = "status" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "Ask answers from fleet-wide state and requires a global administrator");
            }).ConfigureAwait(false);

            await RunTest("Inbox_FromGlobalAdmin_Returns200", async () =>
            {
                HttpResponseMessage response = await _AdminClient.GetAsync("/api/v1/inbox").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "A global administrator still reads the inbox");
            }).ConfigureAwait(false);

            // The status intent reads fleet status only and starts no captain runtime.
            await RunTest("Ask_FromGlobalAdmin_Returns200", async () =>
            {
                HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/ask",
                    JsonHelper.ToJsonContent(new { Message = "status" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "A global administrator still asks Armada");
            }).ConfigureAwait(false);

            // Coordination rooms are found by key alone, so every room is shared across tenants.
            await RunTest("CoordinationMessages_FromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/coordination/rooms/fleet/messages").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "The shared coordination board requires a global administrator");
            }).ConfigureAwait(false);

            await RunTest("CoordinationClaims_FromTenantAdmin_Returns403", async () =>
            {
                HttpResponseMessage response = await _ClientA!.GetAsync("/api/v1/coordination/claims").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "The shared coordination board requires a global administrator");
            }).ConfigureAwait(false);

            await RunTest("CoordinationMessages_FromGlobalAdmin_Returns200", async () =>
            {
                HttpResponseMessage response = await _AdminClient.GetAsync("/api/v1/coordination/rooms/fleet/messages").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "A global administrator still reads the coordination board");
            }).ConfigureAwait(false);

            // The route must refuse before the captain runtime starts, so the test never launches a model.
            await RunTest("CaptainChat_OtherTenantCaptain_Returns404", async () =>
            {
                HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/captains/" + captainAId + "/chat",
                    JsonHelper.ToJsonContent(new { Message = "hello" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Tenant B must not chat with tenant A's captain");
            }).ConfigureAwait(false);

            // Subscription account logins run CLIs and write credentials on the Admiral, so only a global administrator may use them.
            await RunTest("UsageAccountLogin_FromTenantAdminOrUser_Returns403", async () =>
            {
                foreach (HttpClient client in new[] { _ClientA!, _ClientA3! })
                {
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/usage-accounts/automated-login/login/home", null).ConfigureAwait(false)).StatusCode, "home");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/usage-accounts/automated-login/login/start", null).ConfigureAwait(false)).StatusCode, "start");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/usage-accounts/automated-login/login/key",
                        JsonHelper.ToJsonContent(new { ApiKey = "automated-key-value" })).ConfigureAwait(false)).StatusCode, "key");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/usage-accounts/automated-login/login/status").ConfigureAwait(false)).StatusCode, "status");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/usage-accounts/automated-login/login/cancel", null).ConfigureAwait(false)).StatusCode, "cancel");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/v1/usage-accounts/automated-login").ConfigureAwait(false)).StatusCode, "delete");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/usage-accounts/automated-login/refresh", null).ConfigureAwait(false)).StatusCode, "refresh");
                }
                AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.PostAsync("/api/v1/usage-accounts/automated-login/login/home", null).ConfigureAwait(false)).StatusCode, "unauthenticated");
                AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.DeleteAsync("/api/v1/usage-accounts/automated-login").ConfigureAwait(false)).StatusCode, "unauthenticated delete");
                AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.PostAsync("/api/v1/usage-accounts/automated-login/refresh", null).ConfigureAwait(false)).StatusCode, "unauthenticated refresh");
            }).ConfigureAwait(false);

            // Typed-decision modes and the provider key are administrator settings.
            await RunTest("TypedDecisions_FromTenantAdminOrUser_Returns403", async () =>
            {
                string sampleKey = "automated-typed-SECRET-value";
                foreach (HttpClient client in new[] { _ClientA!, _ClientA3! })
                {
                    AssertEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/typed-decisions").ConfigureAwait(false)).StatusCode, "get");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.PutAsync("/api/v1/typed-decisions",
                        JsonHelper.ToJsonContent(new { mode = "Off" })).ConfigureAwait(false)).StatusCode, "put modes");
                    HttpResponseMessage key = await client.PutAsync("/api/v1/typed-decisions/key",
                        JsonHelper.ToJsonContent(new { apiKey = sampleKey })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, key.StatusCode, "put key");
                    AssertFalse((await key.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("SECRET"), "the key is never echoed");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/v1/typed-decisions/key").ConfigureAwait(false)).StatusCode, "delete key");
                }
                AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.GetAsync("/api/v1/typed-decisions").ConfigureAwait(false)).StatusCode, "unauthenticated");
            }).ConfigureAwait(false);

            await RunTest("UsageAccountLogin_Home_FromGlobalAdmin_DerivesFolderAndRejectsUnsafeIds", async () =>
            {
                HttpResponseMessage created = await _AdminClient.PostAsync("/api/v1/usage-accounts/automated-login/login/home", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, created.StatusCode, "a global administrator creates the account folder");
                string body = await created.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertContains(Path.Combine("accounts", "automated-login"), body.Replace("\\\\", "\\"), "the folder is derived under the data directory");
                AssertEqual(HttpStatusCode.BadRequest, (await _AdminClient.PostAsync("/api/v1/usage-accounts/bad.id/login/home", null).ConfigureAwait(false)).StatusCode, "a dotted ID is refused");
                AssertEqual(HttpStatusCode.OK, (await _AdminClient.GetAsync("/api/v1/usage-accounts/automated-login/login/status").ConfigureAwait(false)).StatusCode, "status");
            }).ConfigureAwait(false);

            await RunTest("UsageAccount_DeleteOrRefreshUnknownAccount_FromGlobalAdmin_Returns404", async () =>
            {
                HttpResponseMessage deleted = await _AdminClient.DeleteAsync("/api/v1/usage-accounts/automated-missing-account").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, deleted.StatusCode, "delete of an unknown account");
                AssertContains("account_not_found", await deleted.Content.ReadAsStringAsync().ConfigureAwait(false), "named reason");
                HttpResponseMessage refreshed = await _AdminClient.PostAsync("/api/v1/usage-accounts/automated-missing-account/refresh", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, refreshed.StatusCode, "refresh of an unknown account");
                AssertContains("account_not_found", await refreshed.Content.ReadAsStringAsync().ConfigureAwait(false), "named reason");
            }).ConfigureAwait(false);

            await RunTest("UsageAccountLogin_KeyForUnsavedAccount_Returns404WithoutEchoingKey", async () =>
            {
                HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/usage-accounts/automated-login/login/key",
                    JsonHelper.ToJsonContent(new { ApiKey = "automated-SECRET-key-value" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "a key needs a saved account");
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertFalse(body.Contains("automated-SECRET"), "the key is never echoed");
            }).ConfigureAwait(false);

            #endregion

            #region Cleanup

            await RunTest("Cleanup_DeleteTenantResources", async () =>
            {
                // Dispose tenant-scoped clients
                _ClientA?.Dispose();
                _ClientB?.Dispose();
                _ClientA2?.Dispose();
                _ClientA3?.Dispose();

                // Delete credentials
                if (_CredentialAId != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _CredentialAId).ConfigureAwait(false);
                if (_CredentialBId != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _CredentialBId).ConfigureAwait(false);
                if (_CredentialA2Id != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _CredentialA2Id).ConfigureAwait(false);
                if (_CredentialA3Id != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _CredentialA3Id).ConfigureAwait(false);

                // Delete users
                if (_UserAId != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _UserAId).ConfigureAwait(false);
                if (_UserBId != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _UserBId).ConfigureAwait(false);
                if (_UserA2Id != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _UserA2Id).ConfigureAwait(false);
                if (_UserA3Id != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _UserA3Id).ConfigureAwait(false);

                // Delete tenants
                if (_TenantAId != null)
                {
                    HttpResponseMessage resp = await _AdminClient.DeleteAsync("/api/v1/tenants/" + _TenantAId).ConfigureAwait(false);
                    Assert(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
                        "Expected OK or NotFound when deleting tenant-A, got " + resp.StatusCode);
                }
                if (_TenantBId != null)
                {
                    HttpResponseMessage resp = await _AdminClient.DeleteAsync("/api/v1/tenants/" + _TenantBId).ConfigureAwait(false);
                    Assert(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.NotFound,
                        "Expected OK or NotFound when deleting tenant-B, got " + resp.StatusCode);
                }
            }).ConfigureAwait(false);

            #endregion
        }

        #endregion
    }
}
