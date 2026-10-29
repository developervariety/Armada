namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>HTTP contracts for saved Ask threads and the preserved legacy Ask route.</summary>
    public sealed class AskThreadApiSuite : IArmadaTestSuite
    {
        private const string SuiteId = "E2E.AskThreadApi";

        /// <summary>Builds the saved Ask route cases.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("thread_owner_auth_and_legacy_ask", "Thread owner access, refusals and legacy Ask", TestTags.Positive, VerifyOwnerAndLegacyAsync)
            };
            return new TestSuiteDescriptor(SuiteId, "Ask Thread API", cases);
        }

        private static async Task VerifyOwnerAndLegacyAsync()
        {
            E2EServerFixture fx = await E2EServerFixture.AcquireAsync(typeof(AskThreadApiSuite)).ConfigureAwait(false);
            using (HttpResponseMessage unauth = await fx.UnauthClient.GetAsync("/api/v1/ask/threads").ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

            using (HttpResponseMessage createdResponse = await fx.AuthClient.PostAsync("/api/v1/ask/threads", JsonHelper.ToJsonContent(new { title = "Owner thread" })).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, createdResponse.StatusCode);
                AskThread thread = await JsonHelper.DeserializeAsync<AskThread>(createdResponse).ConfigureAwait(false);
                AssertStartsWith(Constants.AskThreadIdPrefix, thread.Id);
                await VerifyOtherOwnersCannotReadAsync(fx, thread.Id).ConfigureAwait(false);
                await VerifyUnreadRoutesAsync(fx, thread.Id).ConfigureAwait(false);
                await VerifyTurnAndLegacyRoutesAsync(fx, thread.Id).ConfigureAwait(false);
                await VerifyMalformedBodiesAsync(fx, thread.Id).ConfigureAwait(false);
                await VerifyDeleteRoutesAsync(fx, thread.Id).ConfigureAwait(false);
            }
        }

        private static async Task<AskThread> CreateThreadAsync(E2EServerFixture fx, string title)
        {
            using (HttpResponseMessage created = await fx.AuthClient.PostAsync("/api/v1/ask/threads", JsonHelper.ToJsonContent(new { title })).ConfigureAwait(false))
                return await JsonHelper.DeserializeAsync<AskThread>(created).ConfigureAwait(false);
        }

        private static async Task VerifyDeleteRoutesAsync(E2EServerFixture fx, string activeThreadId)
        {
            AskThread archived = await CreateThreadAsync(fx, "Delete me").ConfigureAwait(false);
            AskThread kept = await CreateThreadAsync(fx, "Keep me").ConfigureAwait(false);
            using (HttpResponseMessage archive = await fx.AuthClient.PutAsync("/api/v1/ask/threads/" + archived.Id, JsonHelper.ToJsonContent(new { setArchived = true, archived = true })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.OK, archive.StatusCode);

            using (HttpResponseMessage notArchived = await fx.AuthClient.DeleteAsync("/api/v1/ask/threads/" + kept.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Conflict, notArchived.StatusCode);
            using (HttpClient otherAdmin = await AskThreadApiTestClients.CreateOtherAdminAsync(fx).ConfigureAwait(false))
            using (HttpResponseMessage foreign = await otherAdmin.DeleteAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            using (HttpClient regular = await AskThreadApiTestClients.CreateBearerUserAsync(fx, false).ConfigureAwait(false))
            {
                using (HttpResponseMessage refused = await regular.DeleteAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Forbidden, refused.StatusCode);
                using (HttpResponseMessage refusedBatch = await regular.PostAsync("/api/v1/ask/threads/delete/multiple", JsonHelper.ToJsonContent(new { ids = new[] { archived.Id } })).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Forbidden, refusedBatch.StatusCode);
            }
            using (HttpResponseMessage unauth = await fx.UnauthClient.DeleteAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

            using (HttpResponseMessage deleted = await fx.AuthClient.DeleteAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            using (HttpResponseMessage gone = await fx.AuthClient.GetAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, gone.StatusCode);
            using (HttpResponseMessage again = await fx.AuthClient.DeleteAsync("/api/v1/ask/threads/" + archived.Id).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, again.StatusCode);

            using (HttpResponseMessage batch = await fx.AuthClient.PostAsync("/api/v1/ask/threads/delete/multiple", JsonHelper.ToJsonContent(new { ids = new[] { kept.Id, activeThreadId, "ask_missing" } })).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, batch.StatusCode);
                DeleteMultipleResult result = await JsonHelper.DeserializeAsync<DeleteMultipleResult>(batch).ConfigureAwait(false);
                AssertEqual(0, result.Deleted);
                AssertEqual(3, result.Skipped.Count);
            }
            using (HttpResponseMessage empty = await fx.AuthClient.PostAsync("/api/v1/ask/threads/delete/multiple", JsonHelper.ToJsonContent(new { ids = new string[0] })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, empty.StatusCode);
            string[] tooMany = new string[AskThreadService.MaxDeleteBatch + 1];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = "ask_x" + i;
            using (HttpResponseMessage capped = await fx.AuthClient.PostAsync("/api/v1/ask/threads/delete/multiple", JsonHelper.ToJsonContent(new { ids = tooMany })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, capped.StatusCode);
        }

        private static async Task VerifyOtherOwnersCannotReadAsync(E2EServerFixture fx, string threadId)
        {
            using (HttpClient otherAdmin = await AskThreadApiTestClients.CreateOtherAdminAsync(fx).ConfigureAwait(false))
            using (HttpResponseMessage otherRead = await otherAdmin.GetAsync("/api/v1/ask/threads/" + threadId).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, otherRead.StatusCode);

            using (HttpClient tenantAdmin = await AskThreadApiTestClients.CreateBearerUserAsync(fx, true).ConfigureAwait(false))
            using (HttpResponseMessage tenantRead = await tenantAdmin.GetAsync("/api/v1/ask/threads/" + threadId).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Forbidden, tenantRead.StatusCode);

            using (HttpClient regular = await AskThreadApiTestClients.CreateBearerUserAsync(fx, false).ConfigureAwait(false))
            using (HttpResponseMessage regularRead = await regular.GetAsync("/api/v1/ask/threads/" + threadId).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Forbidden, regularRead.StatusCode);

            SessionTokenService tokens = new SessionTokenService(fx.Settings.SessionTokenEncryptionKey);
            string missionToken = tokens.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, "mis_ask_scope").Token!;
            using (HttpClient missionCaller = AskThreadApiTestClients.CreateBearerClient(fx.BaseUrl, missionToken))
            using (HttpResponseMessage missionRead = await missionCaller.GetAsync("/api/v1/ask/threads/" + threadId).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Forbidden, missionRead.StatusCode);
        }

        private static async Task VerifyUnreadRoutesAsync(E2EServerFixture fx, string threadId)
        {
            using (HttpResponseMessage unread = await fx.AuthClient.PostAsync("/api/v1/ask/threads/" + threadId + "/unread", null).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, unread.StatusCode);
                AssertEqual(1, (await JsonHelper.DeserializeAsync<AskThread>(unread).ConfigureAwait(false)).UnreadCount);
            }
            using (HttpClient otherAdmin = await AskThreadApiTestClients.CreateOtherAdminAsync(fx).ConfigureAwait(false))
            using (HttpResponseMessage foreign = await otherAdmin.PostAsync("/api/v1/ask/threads/" + threadId + "/read", null).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            using (HttpResponseMessage stillUnread = await fx.AuthClient.GetAsync("/api/v1/ask/threads/" + threadId).ConfigureAwait(false))
                AssertEqual(1, (await JsonHelper.DeserializeAsync<AskThread>(stillUnread).ConfigureAwait(false)).UnreadCount);
            using (HttpResponseMessage read = await fx.AuthClient.PostAsync("/api/v1/ask/threads/" + threadId + "/read", null).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, read.StatusCode);
                AssertEqual(0, (await JsonHelper.DeserializeAsync<AskThread>(read).ConfigureAwait(false)).UnreadCount);
            }
            using (HttpResponseMessage missing = await fx.AuthClient.PostAsync("/api/v1/ask/threads/ask_missing/read", null).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using (HttpClient regular = await AskThreadApiTestClients.CreateBearerUserAsync(fx, false).ConfigureAwait(false))
            using (HttpResponseMessage refused = await regular.PostAsync("/api/v1/ask/threads/" + threadId + "/unread", null).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        private static async Task VerifyTurnAndLegacyRoutesAsync(E2EServerFixture fx, string threadId)
        {
            using (HttpResponseMessage turn = await fx.AuthClient.PostAsync("/api/v1/ask/threads/" + threadId + "/turns",
                JsonHelper.ToJsonContent(new AskTurnSendRequest { RequestId = "ask_req_no_captain", Content = "Check fleet status", ShowThinking = false })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, turn.StatusCode);

            using (HttpResponseMessage oldAsk = await fx.AuthClient.PostAsync("/api/v1/ask", JsonHelper.ToJsonContent(new AskRequest { Message = "help" })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.OK, oldAsk.StatusCode);
            using (HttpResponseMessage oldAskDenied = await fx.UnauthClient.PostAsync("/api/v1/ask", JsonHelper.ToJsonContent(new AskRequest { Message = "help" })).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Unauthorized, oldAskDenied.StatusCode);
        }

        private static async Task VerifyMalformedBodiesAsync(E2EServerFixture fx, string threadId)
        {
            using (HttpResponseMessage create = await fx.AuthClient.PostAsync("/api/v1/ask/threads", new StringContent("{", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, create.StatusCode);
            using (HttpResponseMessage update = await fx.AuthClient.PutAsync("/api/v1/ask/threads/" + threadId, new StringContent("{", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, update.StatusCode);
            using (HttpResponseMessage send = await fx.AuthClient.PostAsync("/api/v1/ask/threads/" + threadId + "/turns", new StringContent("{", System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.BadRequest, send.StatusCode);
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> run)
        {
            return new TestCaseDescriptor(SuiteId, id, name, (CancellationToken _) => run(), new List<string> { tag });
        }
    }
}
