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
    using Armada.Core.Enums;
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

        private sealed class DoctorCheck
        {
            public string Name { get; set; } = String.Empty;

            public string Status { get; set; } = String.Empty;

            public string Message { get; set; } = String.Empty;
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

        // The value a caller that may not read a bearer token receives in its place.
        private const string RedactedToken = "********";

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

        /// <summary>
        /// Create a vessel as <paramref name="owner"/>, then name its server paths as the global administrator. A
        /// local repository URL and LocalPath are server paths only a global administrator may set.
        /// </summary>
        private async Task<Vessel> CreateVesselWithServerPathsAsync(
            HttpClient owner,
            string name,
            string? fleetId,
            string defaultBranch,
            string repoUrl,
            string? localPath)
        {
            HttpResponseMessage create = await owner.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
            {
                Name = name,
                FleetId = fleetId,
                RepoUrl = "https://example.invalid/" + name + ".git",
                DefaultBranch = defaultBranch
            })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, create.StatusCode, "Owner creates vessel " + name);
            Vessel created = await JsonHelper.DeserializeAsync<Vessel>(create).ConfigureAwait(false);

            HttpResponseMessage update = await _AdminClient.PutAsync("/api/v1/vessels/" + created.Id, JsonHelper.ToJsonContent(new
            {
                Name = name,
                FleetId = fleetId,
                RepoUrl = repoUrl,
                LocalPath = localPath,
                DefaultBranch = defaultBranch
            })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, update.StatusCode, "Global administrator sets the server paths of " + name);
            return await JsonHelper.DeserializeAsync<Vessel>(update).ConfigureAwait(false);
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
                    JsonHelper.ToJsonContent(new { DefaultCaptainId = captainAId, MinimumTier = "Premium" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, set.StatusCode);
                Persona read = await JsonHelper.DeserializeAsync<Persona>(await _ClientA!.GetAsync("/api/v1/personas/" + defaultedPersonaName).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(captainAId, read.DefaultCaptainId, "The default captain is stored");
                AssertEqual(CaptainTierEnum.Premium, read.MinimumTier, "The minimum tier in the same update is stored");

                HttpResponseMessage retired = await _ClientA!.PutAsync("/api/v1/personas/" + defaultedPersonaName,
                    JsonHelper.ToJsonContent(new { Specialist = true })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, retired.StatusCode, "The retired specialist flag is refused, not ignored");
                AssertContains("specialist_retired", await retired.Content.ReadAsStringAsync().ConfigureAwait(false), "The refusal names the retired flag");

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
                Vessel vessel = await CreateVesselWithServerPathsAsync(
                    _ClientA!,
                    "xt-vessel-A-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    fleetAId,
                    "main",
                    TestRepoHelper.GetLocalBareRepoUrl(),
                    null).ConfigureAwait(false);
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

                    Vessel workingVessel = await CreateVesselWithServerPathsAsync(
                        _ClientA2!, "xt-branch-working-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "file:///branch-working", working).ConfigureAwait(false);
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

                    bareVesselId = (await CreateVesselWithServerPathsAsync(
                        _ClientA, "xt-branch-bare-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "file:///branch-bare", bare).ConfigureAwait(false)).Id;
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
                    Vessel detachedVessel = await CreateVesselWithServerPathsAsync(
                        _ClientA, "xt-branch-detached-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "missing-default", "file:///branch-detached", detached).ConfigureAwait(false);
                    BranchListResponse detachedListing = await JsonHelper.DeserializeAsync<BranchListResponse>(await _ClientA.GetAsync("/api/v1/vessels/" + detachedVessel.Id + "/branches").ConfigureAwait(false)).ConfigureAwait(false);
                    AssertEqual("detached", detachedListing.HeadState, "Detached repository HEAD state (error=" + (detachedListing.Error ?? "<null>") + ")");
                    AssertTrue(detachedListing.HeadRef == null, "Detached repository has no symbolic HEAD ref");
                    AssertTrue(detachedListing.Branches[0].DivergenceError != null, "Missing default branch reports unknown divergence");

                    string unrelated = Path.Combine(root, "unrelated.git");
                    await RunGitAsync(root, "clone", "--bare", working, unrelated).ConfigureAwait(false);
                    string missingPath = Path.Combine(root, "missing");
                    HttpResponseMessage traversalCreate = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "../" + Path.GetFileName(unrelated),
                        RepoUrl = "https://example.invalid/branch-missing.git",
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, traversalCreate.StatusCode, "A name that escapes its directory is refused");
                    Vessel missingVessel = await CreateVesselWithServerPathsAsync(
                        _ClientA, "xt-branch-missing-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "file:///branch-missing", missingPath).ConfigureAwait(false);
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
                    Vessel corruptVessel = await CreateVesselWithServerPathsAsync(
                        _ClientA, "xt-branch-corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "file:///branch-corrupt", corrupt).ConfigureAwait(false);
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

                    string vesselId = (await CreateVesselWithServerPathsAsync(
                        _ClientA!, "xt-branch-write-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", origin, landing).ConfigureAwait(false)).Id;
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

            await RunTest("Vessel_ServerPaths_FromTenantAdmin_AreRefusedOnCreateAndKeptOnUpdate", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-vessel-paths-api-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                    HttpResponseMessage withWorkingDirectory = await _ClientA!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-paths-wd-" + suffix,
                        RepoUrl = "https://example.invalid/paths.git",
                        WorkingDirectory = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, withWorkingDirectory.StatusCode, "Tenant admin cannot name a working directory");

                    HttpResponseMessage withLocalPath = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-paths-lp-" + suffix,
                        RepoUrl = "https://example.invalid/paths.git",
                        LocalPath = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, withLocalPath.StatusCode, "Tenant admin cannot name a local path");

                    HttpResponseMessage withLocalRepoUrl = await _ClientA.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-paths-url-" + suffix,
                        RepoUrl = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, withLocalRepoUrl.StatusCode, "Tenant admin cannot use a server path as the repository URL");

                    HttpResponseMessage withFileUrl = await _ClientA3!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "xt-paths-file-" + suffix,
                        RepoUrl = "file://" + root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, withFileUrl.StatusCode, "Ordinary user is refused before the path rule is reached");

                    HttpResponseMessage vessels = await _AdminClient.GetAsync("/api/v1/vessels?pageSize=1000").ConfigureAwait(false);
                    EnumerationResult<Vessel> listed = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(vessels).ConfigureAwait(false);
                    AssertFalse(listed.Objects.Any(v => v.Name.EndsWith(suffix, StringComparison.Ordinal)), "No refused vessel was stored");

                    string serverOwned = Path.Combine(root, "server-owned");
                    Vessel vessel = await CreateVesselWithServerPathsAsync(
                        _ClientA, "xt-paths-kept-" + suffix, null, "main", "https://example.invalid/kept.git", serverOwned).ConfigureAwait(false);
                    HttpResponseMessage update = await _ClientA.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(new
                    {
                        Name = vessel.Name,
                        RepoUrl = vessel.RepoUrl,
                        DefaultBranch = "main",
                        LocalPath = root,
                        WorkingDirectory = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, update.StatusCode, "Tenant admin update succeeds");
                    Vessel stored = await JsonHelper.DeserializeAsync<Vessel>(await _AdminClient.GetAsync("/api/v1/vessels/" + vessel.Id).ConfigureAwait(false)).ConfigureAwait(false);
                    AssertEqual(serverOwned, stored.LocalPath, "Tenant admin update keeps the stored local path");
                    AssertTrue(stored.WorkingDirectory == null, "Tenant admin update keeps the stored working directory");

                    HttpResponseMessage localRepoUpdate = await _ClientA.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(new
                    {
                        Name = vessel.Name,
                        RepoUrl = root,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, localRepoUpdate.StatusCode, "Tenant admin cannot repoint the repository URL at a server path");

                    HttpResponseMessage adminUpdate = await _AdminClient.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(new
                    {
                        Name = vessel.Name,
                        RepoUrl = vessel.RepoUrl,
                        DefaultBranch = "main",
                        LocalPath = serverOwned,
                        WorkingDirectory = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, adminUpdate.StatusCode, "Global administrator sets server paths");
                    AssertEqual(root, (await JsonHelper.DeserializeAsync<Vessel>(adminUpdate).ConfigureAwait(false)).WorkingDirectory, "Global administrator's working directory is stored");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("Vessel_NameThatIsNotOneSafePathSegment_IsRefused", async () =>
            {
                foreach (string name in new[] { "../x", "a/b", "a\\b", ".hidden", "..", "x..y" })
                {
                    HttpResponseMessage tenantAdmin = await _ClientA!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = name,
                        RepoUrl = "https://example.invalid/unsafe.git"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, tenantAdmin.StatusCode, "Tenant admin create with name '" + name + "' is refused");

                    HttpResponseMessage admin = await _AdminClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = name,
                        RepoUrl = "https://example.invalid/unsafe.git"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, admin.StatusCode, "Global administrator create with name '" + name + "' is refused");
                }

                HttpResponseMessage rename = await _ClientA!.PutAsync("/api/v1/vessels/" + vesselAId, JsonHelper.ToJsonContent(new
                {
                    Name = "../escaped",
                    FleetId = fleetAId,
                    RepoUrl = "https://example.invalid/unsafe.git",
                    DefaultBranch = "main"
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, rename.StatusCode, "Renaming to an unsafe name is refused");
                Vessel stored = await JsonHelper.DeserializeAsync<Vessel>(await _ClientA.GetAsync("/api/v1/vessels/" + vesselAId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(stored.Name.Contains("..", StringComparison.Ordinal), "The stored name is unchanged");
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

            await RunTest("Workspace_Exec_FromTenantAdmin_Returns403AndRunsNothing", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-exec-api-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    Vessel vessel = await CreateVesselWithServerPathsAsync(
                        _ClientA!, "xt-exec-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "https://example.invalid/exec.git", null).ConfigureAwait(false);
                    HttpResponseMessage setWorkingDirectory = await _AdminClient.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(new
                    {
                        Name = vessel.Name,
                        RepoUrl = vessel.RepoUrl,
                        DefaultBranch = "main",
                        WorkingDirectory = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, setWorkingDirectory.StatusCode, "Global administrator sets the working directory");

                    string marker = Path.Combine(root, "exec-ran.txt");
                    object exec = new { Command = "echo ran > exec-ran.txt", TimeoutSeconds = 30 };
                    string execPath = "/api/v1/workspace/vessels/" + vessel.Id + "/exec";

                    HttpResponseMessage tenantAdmin = await _ClientA.PostAsync(execPath, JsonHelper.ToJsonContent(exec)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, tenantAdmin.StatusCode, "Tenant admin cannot run a workspace command");
                    string body = await tenantAdmin.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertTrue(body.Contains("global administrators", StringComparison.OrdinalIgnoreCase), "The refusal names the rule: " + body);
                    AssertEqual(HttpStatusCode.Forbidden, (await _ClientA3!.PostAsync(execPath, JsonHelper.ToJsonContent(exec)).ConfigureAwait(false)).StatusCode, "Ordinary user cannot run a workspace command");
                    AssertEqual(HttpStatusCode.Unauthorized, (await _UnauthClient.PostAsync(execPath, JsonHelper.ToJsonContent(exec)).ConfigureAwait(false)).StatusCode, "Anonymous cannot run a workspace command");
                    AssertFalse(File.Exists(marker), "No refused command ran");

                    HttpResponseMessage admin = await _AdminClient.PostAsync(execPath, JsonHelper.ToJsonContent(exec)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, admin.StatusCode, "Global administrator runs a workspace command");
                    AssertTrue(File.Exists(marker), "The global administrator's command ran");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("CheckRun_CommandOverride_FromNonGlobalAdmin_IsRefusedAndRunsNothing", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-check-override-api-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    Vessel vessel = await CreateVesselWithServerPathsAsync(
                        _ClientA!, "xt-override-" + Guid.NewGuid().ToString("N").Substring(0, 8), null, "main", "https://example.invalid/override.git", null).ConfigureAwait(false);
                    HttpResponseMessage setWorkingDirectory = await _AdminClient.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(new
                    {
                        Name = vessel.Name,
                        RepoUrl = vessel.RepoUrl,
                        DefaultBranch = "main",
                        WorkingDirectory = root
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, setWorkingDirectory.StatusCode, "Global administrator sets the working directory");

                    string marker = Path.Combine(root, "override-ran.txt");
                    object run = new { VesselId = vessel.Id, Type = "Build", CommandOverride = "echo ran > override-ran.txt" };

                    HttpResponseMessage tenantAdmin = await _ClientA.PostAsync("/api/v1/check-runs", JsonHelper.ToJsonContent(run)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Forbidden, tenantAdmin.StatusCode, "Tenant admin cannot send a command override");
                    string body = await tenantAdmin.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertTrue(body.Contains("global administrator", StringComparison.OrdinalIgnoreCase), "The refusal names the rule: " + body);
                    AssertEqual(HttpStatusCode.Forbidden, (await _ClientA3!.PostAsync("/api/v1/check-runs", JsonHelper.ToJsonContent(run)).ConfigureAwait(false)).StatusCode, "Ordinary user cannot run a check");
                    AssertFalse(File.Exists(marker), "No refused override ran");

                    HttpResponseMessage checks = await _AdminClient.GetAsync("/api/v1/check-runs?vesselId=" + vessel.Id).ConfigureAwait(false);
                    AssertEqual(0L, (await JsonHelper.DeserializeAsync<EnumerationResult<CheckRun>>(checks).ConfigureAwait(false)).TotalRecords, "A refused override stores no check run");

                    HttpResponseMessage deploy = await _ClientA.PostAsync("/api/v1/check-runs", JsonHelper.ToJsonContent(new { VesselId = vessel.Id, Type = "Deploy", EnvironmentName = "staging" })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, deploy.StatusCode, "A Deploy check outside the deployment workflow is refused");
                    AssertTrue((await deploy.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("linked to a deployment", StringComparison.Ordinal), "The Deploy refusal names the rule");
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("CheckRun_ImportNamingAnotherTenantsVoyage_IsRefused", async () =>
            {
                HttpResponseMessage vesselB = await _ClientB!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "xt-import-B-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    RepoUrl = "https://example.invalid/import-b.git"
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, vesselB.StatusCode, "Tenant B creates its own vessel");
                string vesselBId = (await JsonHelper.DeserializeAsync<Vessel>(vesselB).ConfigureAwait(false)).Id;

                HttpResponseMessage forged = await _ClientB.PostAsync("/api/v1/check-runs/import", JsonHelper.ToJsonContent(new
                {
                    VesselId = vesselBId,
                    VoyageId = voyageAId,
                    Type = "Build",
                    Status = "Failed",
                    ExitCode = 1
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, forged.StatusCode, "Tenant B cannot link a check to tenant A's voyage");

                HttpResponseMessage forgedMission = await _ClientB.PostAsync("/api/v1/check-runs/import", JsonHelper.ToJsonContent(new
                {
                    VesselId = vesselBId,
                    MissionId = missionAId,
                    Type = "Build",
                    Status = "Passed",
                    ExitCode = 0
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, forgedMission.StatusCode, "Tenant B cannot link a check to tenant A's mission");

                HttpResponseMessage attached = await _AdminClient.GetAsync("/api/v1/check-runs?voyageId=" + voyageAId).ConfigureAwait(false);
                AssertEqual(0L, (await JsonHelper.DeserializeAsync<EnumerationResult<CheckRun>>(attached).ConfigureAwait(false)).TotalRecords, "No check is linked to tenant A's voyage");
                HttpResponseMessage attachedMission = await _AdminClient.GetAsync("/api/v1/check-runs?missionId=" + missionAId).ConfigureAwait(false);
                AssertEqual(0L, (await JsonHelper.DeserializeAsync<EnumerationResult<CheckRun>>(attachedMission).ConfigureAwait(false)).TotalRecords, "No check is linked to tenant A's mission");

                await _AdminClient.DeleteAsync("/api/v1/vessels/" + vesselBId).ConfigureAwait(false);
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
                    new AuthRefusalProbe("CheckRunRoutes", "POST", "/api/v1/check-runs", true),
                    new AuthRefusalProbe("CheckRunRoutes", "POST", "/api/v1/check-runs/import", true),
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
                    new AuthRefusalProbe("JobRoutes", "GET", "/api/v1/jobs", true),
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
                    new AuthRefusalProbe("RuntimeRoutes", "GET", "/api/v1/runtimes/mux/endpoints", true),
                    new AuthRefusalProbe("SignalRoutes", "GET", "/api/v1/signals", false),
                    new AuthRefusalProbe("SignalRoutes", "POST", "/api/v1/signals", true),
                    new AuthRefusalProbe("SkillRoutes", "GET", "/api/v1/skills", false),
                    new AuthRefusalProbe("StatusRoutes", "GET", "/api/v1/status", false),
                    new AuthRefusalProbe("StatusRoutes", "GET", "/api/v1/status", true),
                    new AuthRefusalProbe("StatusRoutes", "POST", "/api/v1/settings/usage-preview", true),
                    new AuthRefusalProbe("TenantRoutes", "GET", "/api/v1/tenants", false),
                    new AuthRefusalProbe("TenantRoutes", "GET", "/api/v1/tenants", true),
                    new AuthRefusalProbe("TokenUsageRoutes", "GET", "/api/v1/token-usage/summary", false),
                    new AuthRefusalProbe("TokenUsageRoutes", "POST", "/api/v1/token-usage/delete/by-filter", true),
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
                    new AuthRefusalProbe("WorkspaceRoutes", "POST", "/api/v1/workspace/vessels/vsl_missing/exec", true),
                    new AuthRefusalProbe("WorkspaceRoutes", "PUT", "/api/v1/workspace/vessels/vsl_missing/file", true),
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

            #region Body-Reference-Scope

            // An id in a request body is read in the caller's scope exactly like an id in the path.
            // Tenant B's administrator names tenant A's records by id and is refused as if they did not
            // exist; nothing is created, dispatched or attached.
            string vesselBId = null!;

            await RunTest("BodyReference_Setup_CreateTenantBVessel", async () =>
            {
                Vessel vessel = await CreateVesselWithServerPathsAsync(
                    _ClientB!,
                    "xt-vessel-B-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    null,
                    "main",
                    TestRepoHelper.GetLocalBareRepoUrl(),
                    null).ConfigureAwait(false);
                vesselBId = vessel.Id;
            }).ConfigureAwait(false);

            await RunTest("BodyReference_VoyageCreateWithOtherTenantVessel_Returns404AndDispatchesNothing", async () =>
            {
                string title = "xt-cross-voyage-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/voyages",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = title,
                        VesselId = vesselAId,
                        Missions = new[] { new { Title = title + "-m", Description = "cross-tenant dispatch" } }
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Another tenant's vessel must not be reachable by body id");

                // A voyage dispatched into a vessel takes the vessel's tenant, so tenant A would see it.
                EnumerationResult<Voyage> voyages = await JsonHelper.DeserializeAsync<EnumerationResult<Voyage>>(
                    await _ClientA!.GetAsync("/api/v1/voyages").ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(voyages.Objects.Any(v => v.Title == title), "No voyage is dispatched into tenant A's vessel");
            }).ConfigureAwait(false);

            await RunTest("BodyReference_MissionCreateWithOtherTenantReferences_Returns404", async () =>
            {
                object[] bodies = new object[]
                {
                    new { Title = "xt-cross-vessel", VesselId = vesselAId },
                    new { Title = "xt-cross-voyage", VoyageId = voyageAId },
                    new { Title = "xt-cross-dependency", DependsOnMissionId = missionAId },
                    new { Title = "xt-cross-captain", CaptainId = captainAId },
                    new { Title = "xt-cross-requested-captain", RequestedCaptainId = captainAId }
                };
                foreach (object body in bodies)
                {
                    HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/missions", JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Refused: " + JsonHelper.ToJsonContent(body).ReadAsStringAsync().Result);
                }

                EnumerationResult<Mission> missions = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(
                    await _ClientB!.GetAsync("/api/v1/missions").ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(missions.Objects.Any(m => m.Title.StartsWith("xt-cross-", StringComparison.Ordinal)), "No refused mission is created");
            }).ConfigureAwait(false);

            await RunTest("BodyReference_MissionUpdateToOtherTenantDependency_Returns404", async () =>
            {
                HttpResponseMessage created = await _ClientB!.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new { Title = "xt-own-mission-B" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);
                string body = await created.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                Mission own = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);

                HttpResponseMessage update = await _ClientB!.PutAsync("/api/v1/missions/" + own.Id,
                    JsonHelper.ToJsonContent(new { Title = own.Title, DependsOnMissionId = missionAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, update.StatusCode, "A dependency on another tenant's mission is refused");

                Mission stored = await JsonHelper.DeserializeAsync<Mission>(
                    await _ClientB!.GetAsync("/api/v1/missions/" + own.Id).ConfigureAwait(false)).ConfigureAwait(false);
                AssertNull(stored.DependsOnMissionId, "The refused dependency is not stored");
                await _ClientB!.DeleteAsync("/api/v1/missions/" + own.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BodyReference_BuildContextWithOtherTenantCaptain_Returns404", async () =>
            {
                // A runtime that cannot launch keeps the pre-fix path from starting a real agent.
                HttpResponseMessage captainResponse = await _ClientA!.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new { Name = "xt-context-captain-A-" + Guid.NewGuid().ToString("N").Substring(0, 8), Runtime = "Custom" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, captainResponse.StatusCode);
                Captain captain = await JsonHelper.DeserializeAsync<Captain>(captainResponse).ConfigureAwait(false);

                HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/vessels/" + vesselBId + "/build-context",
                    JsonHelper.ToJsonContent(new { CaptainId = captain.Id })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Another tenant's captain must not run against this vessel");
                await _ClientA!.DeleteAsync("/api/v1/captains/" + captain.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BodyReference_MergeEnqueueWithOtherTenantMission_Returns404", async () =>
            {
                string branch = "feature/xt-cross-merge-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/merge-queue",
                    JsonHelper.ToJsonContent(new { MissionId = missionAId, VesselId = vesselBId, BranchName = branch, TargetBranch = "main" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "Another tenant's mission must not be attached to a merge entry");

                HttpResponseMessage vesselResponse = await _ClientB!.PostAsync("/api/v1/merge-queue",
                    JsonHelper.ToJsonContent(new { VesselId = vesselAId, BranchName = branch, TargetBranch = "main" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, vesselResponse.StatusCode, "Another tenant's vessel must not be a merge target");

                EnumerationResult<MergeEntry> entries = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(
                    await _ClientB!.GetAsync("/api/v1/merge-queue").ConfigureAwait(false)).ConfigureAwait(false);
                AssertFalse(entries.Objects.Any(e => e.BranchName == branch), "No refused entry is enqueued");
            }).ConfigureAwait(false);

            await RunTest("BodyReference_IncidentWithOtherTenantLinks_IsRefused", async () =>
            {
                HttpResponseMessage created = await _ClientB!.PostAsync("/api/v1/incidents",
                    JsonHelper.ToJsonContent(new { Title = "xt-cross-incident", MissionId = missionAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, created.StatusCode, "An incident linking another tenant's mission is refused");

                created = await _ClientB!.PostAsync("/api/v1/incidents",
                    JsonHelper.ToJsonContent(new { Title = "xt-cross-incident", VesselId = vesselAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, created.StatusCode, "An incident linking another tenant's vessel is refused");

                HttpResponseMessage own = await _ClientB!.PostAsync("/api/v1/incidents",
                    JsonHelper.ToJsonContent(new { Title = "xt-own-incident", VesselId = vesselBId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, own.StatusCode, "An incident linking the caller's own vessel is created");
                Incident incident = await JsonHelper.DeserializeAsync<Incident>(own).ConfigureAwait(false);

                HttpResponseMessage update = await _ClientB!.PutAsync("/api/v1/incidents/" + incident.Id,
                    JsonHelper.ToJsonContent(new { VoyageId = voyageAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, update.StatusCode, "An update linking another tenant's voyage is refused");
                await _ClientB!.DeleteAsync("/api/v1/incidents/" + incident.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BodyReference_VesselWithOtherTenantFleet_Returns404", async () =>
            {
                string name = "xt-cross-fleet-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage created = await _ClientB!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = name,
                    FleetId = fleetAId,
                    RepoUrl = "https://example.invalid/" + name + ".git",
                    DefaultBranch = "main"
                })).ConfigureAwait(false);
                if (created.StatusCode == HttpStatusCode.Created)
                    await _AdminClient.DeleteAsync("/api/v1/vessels/" + (await JsonHelper.DeserializeAsync<Vessel>(created).ConfigureAwait(false)).Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, created.StatusCode, "A vessel cannot join another tenant's fleet on create");

                Vessel own = await JsonHelper.DeserializeAsync<Vessel>(await _ClientB!.GetAsync("/api/v1/vessels/" + vesselBId).ConfigureAwait(false)).ConfigureAwait(false);
                HttpResponseMessage moved = await _ClientB!.PutAsync("/api/v1/vessels/" + vesselBId, JsonHelper.ToJsonContent(new
                {
                    Name = own.Name,
                    FleetId = fleetAId,
                    RepoUrl = own.RepoUrl,
                    DefaultBranch = own.DefaultBranch
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, moved.StatusCode, "A vessel cannot move into another tenant's fleet");
                Vessel stored = await JsonHelper.DeserializeAsync<Vessel>(await _ClientB!.GetAsync("/api/v1/vessels/" + vesselBId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertNotEqual(fleetAId, stored.FleetId, "The refused fleet is not stored");
            }).ConfigureAwait(false);

            await RunTest("BodyReference_MissionCreateWithUnreadableDock_Returns404", async () =>
            {
                string title = "xt-cross-dock-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage response = await _ClientB!.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new { Title = title, DockId = "dck_" + Guid.NewGuid().ToString("N").Substring(0, 12) })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode, "A dock the caller cannot read is refused like every other mission reference");
                EnumerationResult<Mission> missions = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(
                    await _ClientB!.GetAsync("/api/v1/missions?pageSize=1000").ConfigureAwait(false)).ConfigureAwait(false);
                foreach (Mission leftover in missions.Objects.Where(m => m.Title == title))
                    await _AdminClient.DeleteAsync("/api/v1/missions/" + leftover.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BodyReference_IncidentWithOtherTenantRegressionObjective_IsRefused", async () =>
            {
                HttpResponseMessage objectiveResponse = await _ClientA!.PostAsync("/api/v1/objectives",
                    JsonHelper.ToJsonContent(new { Title = "xt-regression-objective-A" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, objectiveResponse.StatusCode);
                Objective objectiveA = await JsonHelper.DeserializeAsync<Objective>(objectiveResponse).ConfigureAwait(false);
                try
                {
                    HttpResponseMessage created = await _ClientB!.PostAsync("/api/v1/incidents", JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-cross-regression",
                        RegressionPurpose = "Consumer",
                        RegressionObjectiveId = objectiveA.Id
                    })).ConfigureAwait(false);
                    if (created.StatusCode == HttpStatusCode.Created)
                        await _ClientB!.DeleteAsync("/api/v1/incidents/" + (await JsonHelper.DeserializeAsync<Incident>(created).ConfigureAwait(false)).Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, created.StatusCode, "An incident cannot name another tenant's objective as its regression");

                    HttpResponseMessage own = await _ClientA!.PostAsync("/api/v1/incidents", JsonHelper.ToJsonContent(new
                    {
                        Title = "xt-own-regression",
                        RegressionPurpose = "Consumer",
                        RegressionObjectiveId = objectiveA.Id
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, own.StatusCode, "An incident names its own tenant's objective");
                    await _ClientA!.DeleteAsync("/api/v1/incidents/" + (await JsonHelper.DeserializeAsync<Incident>(own).ConfigureAwait(false)).Id).ConfigureAwait(false);
                }
                finally
                {
                    await _ClientA!.DeleteAsync("/api/v1/objectives/" + objectiveA.Id).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("BodyReference_GlobalAdminStillCreatesMissionOnAnyTenantVessel", async () =>
            {
                HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new { Title = "xt-admin-cross-" + Guid.NewGuid().ToString("N").Substring(0, 8), VoyageId = voyageAId })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode, "A global administrator names any tenant's records");
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                Mission mission = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);
                await _AdminClient.DeleteAsync("/api/v1/missions/" + mission.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BodyReference_Cleanup_DeleteTenantBVessel", async () =>
            {
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/vessels/" + vesselBId).ConfigureAwait(false);
                Assert(response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK,
                    "Expected tenant B to delete its vessel, got " + response.StatusCode);
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
            await RunTest("Persona_DeleteBuiltInFromOtherTenant_IsRefusedAndKept", async () =>
            {
                // A built-in persona is readable by every tenant, so the refusal names it as built-in
                // rather than hiding it; what matters is that it is never deleted.
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/personas/Worker").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, "A built-in persona cannot be deleted");
                HttpResponseMessage kept = await _AdminClient.GetAsync("/api/v1/personas/Worker").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, kept.StatusCode, "The built-in persona is still stored");
            }).ConfigureAwait(false);

            await RunTest("Pipeline_DeleteBuiltInFromOtherTenant_IsRefusedAndKept", async () =>
            {
                // A built-in pipeline is readable by every tenant, so the refusal names it as built-in
                // rather than hiding it; what matters is that it is never deleted.
                HttpResponseMessage response = await _ClientB!.DeleteAsync("/api/v1/pipelines/FullPipeline").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, "A built-in pipeline cannot be deleted");
                HttpResponseMessage kept = await _AdminClient.GetAsync("/api/v1/pipelines/FullPipeline").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, kept.StatusCode, "The built-in pipeline is still stored");
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

            await RunTest("BuiltInPipelineAndPersona_UpdateFromDefaultTenantAdmin_Returns403AndGlobalAdminSucceeds", async () =>
            {
                // Built-ins are stored in the default tenant and used by every tenant, so a tenant
                // administrator of the default tenant must not change them.
                TenantUserCredentialResult defaultAdmin = await CreateUserCredentialAsync("default", "default-tenant-admin", true).ConfigureAwait(false);
                using (HttpClient defaultAdminClient = CreateBearerClient(defaultAdmin.BearerToken))
                {
                    try
                    {
                        Pipeline builtIn = await JsonHelper.DeserializeAsync<Pipeline>(
                            await _AdminClient.GetAsync("/api/v1/pipelines/FullPipeline").ConfigureAwait(false)).ConfigureAwait(false);
                        AssertTrue(builtIn.IsBuiltIn, "FullPipeline is a built-in");

                        HttpResponseMessage refused = await defaultAdminClient.PutAsync("/api/v1/pipelines/FullPipeline",
                            JsonHelper.ToJsonContent(new { Description = "changed by a tenant admin" })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Forbidden, refused.StatusCode, "A tenant administrator may not change a built-in pipeline");

                        HttpResponseMessage personaRefused = await defaultAdminClient.PutAsync("/api/v1/personas/Worker",
                            JsonHelper.ToJsonContent(new { Description = "changed by a tenant admin" })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Forbidden, personaRefused.StatusCode, "A tenant administrator may not change a built-in persona");

                        Pipeline unchanged = await JsonHelper.DeserializeAsync<Pipeline>(
                            await _AdminClient.GetAsync("/api/v1/pipelines/FullPipeline").ConfigureAwait(false)).ConfigureAwait(false);
                        AssertEqual(builtIn.Description, unchanged.Description, "The refused change is not stored");

                        HttpResponseMessage allowed = await _AdminClient.PutAsync("/api/v1/pipelines/FullPipeline",
                            JsonHelper.ToJsonContent(new { Description = builtIn.Description })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, allowed.StatusCode, "A global administrator changes a built-in pipeline");
                    }
                    finally
                    {
                        await _AdminClient.DeleteAsync("/api/v1/credentials/" + defaultAdmin.CredentialId).ConfigureAwait(false);
                        await _AdminClient.DeleteAsync("/api/v1/users/" + defaultAdmin.UserId).ConfigureAwait(false);
                    }
                }
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

            // Background jobs carry no tenant or user, so only a global administrator reads them, as with
            // the armada_job_status tool.
            await RunTest("Jobs_FromTenantAdminOrUser_Returns403", async () =>
            {
                foreach (HttpClient client in new[] { _ClientA!, _ClientA3! })
                {
                    AssertEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/jobs").ConfigureAwait(false)).StatusCode, "job list");
                    AssertEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/jobs/job_missing").ConfigureAwait(false)).StatusCode, "job read");
                }
            }).ConfigureAwait(false);

            await RunTest("Jobs_FromGlobalAdmin_ListEnvelopeAndUnknownJob404", async () =>
            {
                HttpResponseMessage list = await _AdminClient.GetAsync("/api/v1/jobs").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, list.StatusCode, "A global administrator reads the job list");
                string body = await list.Content.ReadAsStringAsync().ConfigureAwait(false);
                foreach (string field in new[] { "\"Success\":true", "\"Objects\":", "\"TotalRecords\":" })
                    AssertTrue(body.Contains(field, StringComparison.OrdinalIgnoreCase), "The list keeps its envelope field " + field + ": " + body);

                HttpResponseMessage missing = await _AdminClient.GetAsync("/api/v1/jobs/job_missing").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, missing.StatusCode, "An unknown job reads 404");
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

            #region User-Privilege-Boundary

            // A tenant administrator may manage the users of its own tenant, but never a global administrator
            // or a protected user in that tenant, and never read another user's bearer token.
            string privilegedUserId = null!;
            string privilegedEmail = "xt-global-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada";
            string privilegedCredentialId = null!;
            string privilegedToken = null!;

            await RunTest("UserPrivilege_Setup_GlobalAdminInTenantA", async () =>
            {
                HttpResponseMessage userResponse = await _AdminClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(new
                {
                    TenantId = _TenantAId,
                    Email = privilegedEmail,
                    PasswordSha256 = UserMaster.ComputePasswordHash("privileged-fixture"),
                    IsAdmin = true
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);
                UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);
                AssertTrue(user.IsAdmin, "Fixture user is a global administrator");
                privilegedUserId = user.Id;

                HttpResponseMessage credentialResponse = await _AdminClient.PostAsync("/api/v1/credentials",
                    JsonHelper.ToJsonContent(new { TenantId = _TenantAId, UserId = privilegedUserId, Name = "xt-global-cred" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, credentialResponse.StatusCode);
                Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
                privilegedCredentialId = credential.Id;
                privilegedToken = credential.BearerToken;
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_CannotChangeGlobalAdminInOwnTenant", async () =>
            {
                HttpResponseMessage response = await _ClientA!.PutAsync("/api/v1/users/" + privilegedUserId, JsonHelper.ToJsonContent(new
                {
                    Email = privilegedEmail,
                    Password = "taken-over",
                    Active = false
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "A tenant administrator cannot reset or deactivate a global administrator");

                UserMaster stored = await JsonHelper.DeserializeAsync<UserMaster>(
                    await _AdminClient.GetAsync("/api/v1/users/" + privilegedUserId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertTrue(stored.Active, "The refused change leaves the global administrator active");
                using (HttpClient privileged = CreateBearerClient(privilegedToken))
                {
                    HttpResponseMessage whoami = await privileged.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, whoami.StatusCode, "The global administrator still signs in");
                }
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_CannotMintOrManageGlobalAdminCredentials", async () =>
            {
                HttpResponseMessage minted = await _ClientA!.PostAsync("/api/v1/credentials",
                    JsonHelper.ToJsonContent(new { UserId = privilegedUserId, Name = "xt-minted" })).ConfigureAwait(false);
                if (minted.StatusCode == HttpStatusCode.Created)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + (await JsonHelper.DeserializeAsync<Credential>(minted).ConfigureAwait(false)).Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, minted.StatusCode, "A tenant administrator cannot mint a credential for a global administrator");

                HttpResponseMessage update = await _ClientA!.PutAsync("/api/v1/credentials/" + privilegedCredentialId,
                    JsonHelper.ToJsonContent(new { Name = "xt-renamed", Active = false })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, update.StatusCode, "A tenant administrator cannot change a global administrator's credential");
                HttpResponseMessage delete = await _ClientA!.DeleteAsync("/api/v1/credentials/" + privilegedCredentialId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, delete.StatusCode, "A tenant administrator cannot delete a global administrator's credential");
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_ReadsNoOtherUsersBearerToken", async () =>
            {
                HttpResponseMessage list = await _ClientA!.GetAsync("/api/v1/credentials?pageSize=1000").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, list.StatusCode);
                EnumerationResult<Credential> credentials = await JsonHelper.DeserializeAsync<EnumerationResult<Credential>>(list).ConfigureAwait(false);
                AssertFalse(credentials.Objects.Any(c => c.BearerToken == privilegedToken), "The global administrator's token is never listed to a tenant administrator");
                AssertTrue(credentials.Objects.Where(c => c.UserId != _UserAId).All(c => c.BearerToken == RedactedToken),
                    "Every other user's token is redacted");
                AssertTrue(credentials.Objects.Any(c => c.Id == _CredentialAId && c.BearerToken == _BearerTokenA),
                    "The caller still reads its own token");

                HttpResponseMessage read = await _ClientA!.GetAsync("/api/v1/credentials/" + privilegedCredentialId).ConfigureAwait(false);
                if (read.StatusCode == HttpStatusCode.OK)
                {
                    Credential single = await JsonHelper.DeserializeAsync<Credential>(read).ConfigureAwait(false);
                    AssertEqual(RedactedToken, single.BearerToken, "A single read redacts another user's token");
                }
                else
                {
                    AssertEqual(HttpStatusCode.NotFound, read.StatusCode, "A single read either redacts or hides the credential");
                }
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_CannotChangeProtectedTenantUser", async () =>
            {
                EnumerationResult<UserMaster> users = await JsonHelper.DeserializeAsync<EnumerationResult<UserMaster>>(
                    await _ClientA!.GetAsync("/api/v1/users?pageSize=1000").ConfigureAwait(false)).ConfigureAwait(false);
                UserMaster seeded = users.Objects.Single(u => u.IsProtected && u.TenantId == _TenantAId);
                HttpResponseMessage response = await _ClientA!.PutAsync("/api/v1/users/" + seeded.Id, JsonHelper.ToJsonContent(new
                {
                    Email = seeded.Email,
                    Password = "taken-over",
                    Active = false
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "A tenant administrator cannot take over the tenant's protected user");
                HttpResponseMessage minted = await _ClientA!.PostAsync("/api/v1/credentials",
                    JsonHelper.ToJsonContent(new { UserId = seeded.Id, Name = "xt-minted-protected" })).ConfigureAwait(false);
                if (minted.StatusCode == HttpStatusCode.Created)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + (await JsonHelper.DeserializeAsync<Credential>(minted).ConfigureAwait(false)).Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, minted.StatusCode, "A tenant administrator cannot mint a credential for the protected user");
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_StillManagesOrdinaryUsersAndItself", async () =>
            {
                UserMaster ordinary = await JsonHelper.DeserializeAsync<UserMaster>(
                    await _AdminClient.GetAsync("/api/v1/users/" + _UserA3Id).ConfigureAwait(false)).ConfigureAwait(false);
                HttpResponseMessage renamed = await _ClientA!.PutAsync("/api/v1/users/" + _UserA3Id, JsonHelper.ToJsonContent(new
                {
                    Email = ordinary.Email,
                    FirstName = "Renamed",
                    Active = true
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, renamed.StatusCode, "A tenant administrator still changes an ordinary user");

                HttpResponseMessage minted = await _ClientA!.PostAsync("/api/v1/credentials",
                    JsonHelper.ToJsonContent(new { UserId = _UserA3Id, Name = "xt-minted-ordinary" })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, minted.StatusCode, "A tenant administrator still mints a credential for an ordinary user");
                Credential mintedCredential = await JsonHelper.DeserializeAsync<Credential>(minted).ConfigureAwait(false);
                AssertNotEqual(RedactedToken, mintedCredential.BearerToken, "The creation response returns the new token once");

                HttpResponseMessage saved = await _ClientA!.PutAsync("/api/v1/credentials/" + mintedCredential.Id, JsonHelper.ToJsonContent(new
                {
                    Name = "xt-minted-renamed",
                    Active = true,
                    BearerToken = RedactedToken
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, saved.StatusCode, "A redacted credential can be saved back");
                using (HttpClient minted2 = CreateBearerClient(mintedCredential.BearerToken))
                {
                    HttpResponseMessage whoami = await minted2.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, whoami.StatusCode, "Saving a redacted credential keeps its token");
                }
                await _ClientA!.DeleteAsync("/api/v1/credentials/" + mintedCredential.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_GlobalAdmin_Unaffected", async () =>
            {
                HttpResponseMessage renamed = await _AdminClient.PutAsync("/api/v1/users/" + privilegedUserId, JsonHelper.ToJsonContent(new
                {
                    Email = privilegedEmail,
                    FirstName = "Global",
                    IsAdmin = true,
                    Active = true
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, renamed.StatusCode, "A global administrator still changes any user");
                Credential read = await JsonHelper.DeserializeAsync<Credential>(
                    await _AdminClient.GetAsync("/api/v1/credentials/" + privilegedCredentialId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(privilegedToken, read.BearerToken, "A global administrator still reads every token");
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_TenantAdmin_CannotDeleteGlobalAdminInOwnTenant", async () =>
            {
                HttpResponseMessage response = await _ClientA!.DeleteAsync("/api/v1/users/" + privilegedUserId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, response.StatusCode, "A tenant administrator cannot delete a global administrator");
                HttpResponseMessage stillThere = await _AdminClient.GetAsync("/api/v1/users/" + privilegedUserId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, stillThere.StatusCode, "The refused delete leaves the global administrator in place");
            }).ConfigureAwait(false);

            await RunTest("UserPrivilege_Cleanup", async () =>
            {
                if (privilegedCredentialId != null) await _AdminClient.DeleteAsync("/api/v1/credentials/" + privilegedCredentialId).ConfigureAwait(false);
                if (privilegedUserId != null) await _AdminClient.DeleteAsync("/api/v1/users/" + privilegedUserId).ConfigureAwait(false);
            }).ConfigureAwait(false);

            #endregion

            #region Ordinary-User-Scope

            await RunTest("OrdinaryUser_ReadsOnlyOwnRecordsInsideSharedParents", async () =>
            {
                // An ordinary user reads only its own records. A demoted tenant administrator keeps the fleet and
                // voyage it created; another user's vessel, mission and signal inside them stay invisible to it.
                string? userId = null;
                string? credentialId = null;
                string? fleetId = null;
                string? voyageId = null;
                string? otherVesselId = null;
                string? otherMissionId = null;
                string? otherSignalId = null;
                List<Exception> failures = new List<Exception>();
                try
                {
                    UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(await _AdminClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(new
                    {
                        TenantId = _TenantAId,
                        Email = "xt-demoted-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada",
                        PasswordSha256 = UserMaster.ComputePasswordHash("demoted-fixture"),
                        IsTenantAdmin = true
                    })).ConfigureAwait(false)).ConfigureAwait(false);
                    userId = user.Id;
                    Credential credential = await JsonHelper.DeserializeAsync<Credential>(await _AdminClient.PostAsync("/api/v1/credentials",
                        JsonHelper.ToJsonContent(new { TenantId = _TenantAId, UserId = userId, Name = "xt-demoted" })).ConfigureAwait(false)).ConfigureAwait(false);
                    credentialId = credential.Id;

                    using (HttpClient demoted = CreateBearerClient(credential.BearerToken))
                    {
                        HttpResponseMessage fleetResponse = await demoted.PostAsync("/api/v1/fleets",
                            JsonHelper.ToJsonContent(new { Name = "xt-demoted-fleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, fleetResponse.StatusCode);
                        fleetId = (await JsonHelper.DeserializeAsync<Fleet>(fleetResponse).ConfigureAwait(false)).Id;
                        HttpResponseMessage voyageResponse = await demoted.PostAsync("/api/v1/voyages",
                            JsonHelper.ToJsonContent(new { Title = "xt-demoted-voyage" })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, voyageResponse.StatusCode);
                        voyageId = (await JsonHelper.DeserializeAsync<Voyage>(voyageResponse).ConfigureAwait(false)).Id;

                        string vesselName = "xt-other-in-fleet-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        HttpResponseMessage vesselResponse = await _ClientA!.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                        {
                            Name = vesselName,
                            FleetId = fleetId,
                            RepoUrl = "https://example.invalid/" + vesselName + ".git",
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode, "The tenant administrator adds its vessel to the fleet");
                        otherVesselId = (await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false)).Id;

                        HttpResponseMessage missionResponse = await _ClientA!.PostAsync("/api/v1/missions",
                            JsonHelper.ToJsonContent(new { Title = "xt-other-in-voyage", VoyageId = voyageId })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, missionResponse.StatusCode, "The tenant administrator adds its mission to the voyage");
                        string missionBody = await missionResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(missionBody);
                        otherMissionId = (wrapper.Mission ?? JsonHelper.Deserialize<Mission>(missionBody)).Id;

                        HttpResponseMessage signalResponse = await _ClientA!.PostAsync("/api/v1/signals",
                            JsonHelper.ToJsonContent(new { Type = "Mail", Payload = "xt-other-user-signal" })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, signalResponse.StatusCode);
                        otherSignalId = (await JsonHelper.DeserializeAsync<Signal>(signalResponse).ConfigureAwait(false)).Id;

                        user.IsTenantAdmin = false;
                        AssertEqual(HttpStatusCode.OK, (await _AdminClient.PutAsync("/api/v1/users/" + userId, JsonHelper.ToJsonContent(user)).ConfigureAwait(false)).StatusCode);
                        AssertFalse((await JsonHelper.DeserializeAsync<WhoAmIResult>(await demoted.GetAsync("/api/v1/whoami").ConfigureAwait(false)).ConfigureAwait(false)).User!.IsTenantAdmin,
                            "The fixture user is now an ordinary user");

                        HttpResponseMessage fleetDetail = await demoted.GetAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, fleetDetail.StatusCode, "The ordinary user reads its own fleet");
                        string fleetRaw = await fleetDetail.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertFalse(fleetRaw.Contains(otherVesselId!, StringComparison.Ordinal), "The fleet detail lists no other user's vessel");

                        HttpResponseMessage voyageDetail = await demoted.GetAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, voyageDetail.StatusCode, "The ordinary user reads its own voyage");
                        string voyageRaw = await voyageDetail.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertFalse(voyageRaw.Contains(otherMissionId!, StringComparison.Ordinal), "The voyage detail lists no other user's mission");

                        string recentRaw = await (await demoted.GetAsync("/api/v1/signals/recent?count=1000").ConfigureAwait(false)).Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertFalse(recentRaw.Contains(otherSignalId!, StringComparison.Ordinal), "Recent signals list no other user's signal");

                        HttpResponseMessage gitStatus = await demoted.GetAsync("/api/v1/vessels/" + otherVesselId + "/git-status").ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.NotFound, gitStatus.StatusCode, "Git status reads the vessel in the ordinary user's scope");

                        HttpResponseMessage enumerate = await demoted.PostAsync("/api/v1/playbooks/enumerate", JsonHelper.ToJsonContent(new { PageSize = 10 })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, enumerate.StatusCode, "An ordinary user enumerates what it may list");
                        foreach (string collection in new[] { "workflow-profiles", "environments", "objectives", "backlog" })
                        {
                            HttpResponseMessage collectionEnumerate = await demoted.PostAsync("/api/v1/" + collection + "/enumerate", JsonHelper.ToJsonContent(new { PageSize = 10 })).ConfigureAwait(false);
                            AssertEqual(HttpStatusCode.OK, collectionEnumerate.StatusCode, "An ordinary user enumerates " + collection);
                        }
                    }
                }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    foreach (string path in new[]
                    {
                        otherSignalId == null ? "" : "/api/v1/signals/" + otherSignalId,
                        otherMissionId == null ? "" : "/api/v1/missions/" + otherMissionId,
                        otherVesselId == null ? "" : "/api/v1/vessels/" + otherVesselId,
                        voyageId == null ? "" : "/api/v1/voyages/" + voyageId,
                        fleetId == null ? "" : "/api/v1/fleets/" + fleetId,
                        credentialId == null ? "" : "/api/v1/credentials/" + credentialId,
                        userId == null ? "" : "/api/v1/users/" + userId
                    })
                    {
                        if (path.Length == 0) continue;
                        try { await _AdminClient.DeleteAsync(path).ConfigureAwait(false); }
                        catch (Exception exception) { failures.Add(exception); }
                    }
                }
                if (failures.Count > 0) throw new AggregateException("Ordinary-user scope test failed", failures);
            }).ConfigureAwait(false);

            #endregion

            #region Host-Detail-Authorization

            await RunTest("Doctor_FromTenantAdmin_ShowsNoHostPaths", async () =>
            {
                HttpResponseMessage tenantResponse = await _ClientA!.GetAsync("/api/v1/doctor").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, tenantResponse.StatusCode);
                List<DoctorCheck> tenantChecks = await JsonHelper.DeserializeAsync<List<DoctorCheck>>(tenantResponse).ConfigureAwait(false);
                AssertTrue(tenantChecks.Count > 0, "The tenant administrator still receives the checks");
                foreach (DoctorCheck check in tenantChecks)
                    AssertFalse(check.Message.Contains('/') || check.Message.Contains('\\'), "No server path reaches a tenant administrator: " + check.Name + ": " + check.Message);

                List<DoctorCheck> adminChecks = await JsonHelper.DeserializeAsync<List<DoctorCheck>>(
                    await _AdminClient.GetAsync("/api/v1/doctor").ConfigureAwait(false)).ConfigureAwait(false);
                DoctorCheck settings = adminChecks.Single(check => check.Name == "Settings");
                AssertTrue(settings.Message.Contains('/') || settings.Message.Contains('\\'), "A global administrator still sees the settings path: " + settings.Message);
            }).ConfigureAwait(false);

            await RunTest("MuxRuntimeRoutes_FromTenantAdmin_Return403", async () =>
            {
                HttpResponseMessage list = await _ClientA!.GetAsync("/api/v1/runtimes/mux/endpoints?configDirectory=" + Uri.EscapeDataString(Path.GetTempPath())).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, list.StatusCode, "A tenant administrator cannot read the server's Mux configuration");
                HttpResponseMessage show = await _ClientA!.GetAsync("/api/v1/runtimes/mux/endpoints/example").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, show.StatusCode, "A tenant administrator cannot probe a Mux endpoint");
                HttpResponseMessage admin = await _AdminClient.GetAsync("/api/v1/runtimes/mux/endpoints").ConfigureAwait(false);
                AssertNotEqual(HttpStatusCode.Forbidden, admin.StatusCode, "A global administrator still reaches the Mux routes");
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
