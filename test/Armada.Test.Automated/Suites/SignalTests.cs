namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// Integration tests for the Signal REST API endpoints.
    /// </summary>
    public class SignalTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Signals";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;
        private List<string> _CreatedSignalIds = new List<string>();
        private List<string> _CreatedCaptainIds = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with authenticated and unauthenticated HTTP clients.
        /// </summary>
        /// <param name="authClient">Authenticated HTTP client.</param>
        /// <param name="unauthClient">Unauthenticated HTTP client.</param>
        public SignalTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run all signal tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            #region Signal-Create-Tests

            await RunTest("Signal_Create", async () =>
            {
                string recipientId = await CreateCaptainAsync("signal-create-recipient");

                StringContent content = new StringContent(
                    JsonSerializer.Serialize(new { Type = "Nudge", Payload = "test-create", ToCaptainId = recipientId }),
                    Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/signals", content);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();
                JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                string signalId = root.GetProperty("Id").GetString()!;
                _CreatedSignalIds.Add(signalId);

                AssertStartsWith("sig_", signalId);
                AssertEqual("Nudge", root.GetProperty("Type").GetString()!);
                AssertEqual("test-create", root.GetProperty("Payload").GetString()!);
                AssertEqual(recipientId, root.GetProperty("ToCaptainId").GetString()!);
                AssertFalse(root.GetProperty("Read").GetBoolean(), "New signal should be unread");
                AssertTrue(root.TryGetProperty("CreatedUtc", out _), "CreatedUtc should be present");
            });

            #endregion

            #region Signal-Read-Tests

            await RunTest("Signal_Read", async () =>
            {
                string recipientId = await CreateCaptainAsync("signal-read-recipient");
                JsonElement created = await CreateSignalAsync("Mail", "read-test-payload", toCaptainId: recipientId);
                string signalId = created.GetProperty("Id").GetString()!;

                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/signals/" + signalId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();
                JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                AssertEqual(signalId, root.GetProperty("Id").GetString()!);
                AssertEqual("Mail", root.GetProperty("Type").GetString()!);
                AssertEqual("read-test-payload", root.GetProperty("Payload").GetString()!);
                AssertEqual(recipientId, root.GetProperty("ToCaptainId").GetString()!);
                AssertFalse(root.GetProperty("Read").GetBoolean());
            });

            #endregion

            #region Signal-EnumerateByRecipient-Tests

            await RunTest("Signal_EnumerateByRecipient_UnreadOnly", async () =>
            {
                string recipientId = await CreateCaptainAsync("signal-unread-recipient");

                // Create two unread signals
                JsonElement unread1 = await CreateSignalAsync("Nudge", "unread-1", toCaptainId: recipientId);
                JsonElement unread2 = await CreateSignalAsync("Mail", "unread-2", toCaptainId: recipientId);

                // Create one signal and mark it as read
                JsonElement readSignal = await CreateSignalAsync("Heartbeat", "will-be-read", toCaptainId: recipientId);
                string readSignalId = readSignal.GetProperty("Id").GetString()!;
                HttpResponseMessage markResponse = await _AuthClient.PutAsync(
                    "/api/v1/signals/" + readSignalId + "/read",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                AssertEqual(HttpStatusCode.OK, markResponse.StatusCode);

                // Enumerate with unreadOnly=true (default)
                HttpResponseMessage response = await _AuthClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=true");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();
                JsonDocument doc = JsonDocument.Parse(body);
                JsonElement signals = doc.RootElement;

                AssertEqual(2, signals.GetArrayLength(), "Should return only 2 unread signals");

                // Verify all returned signals are unread
                foreach (JsonElement sig in signals.EnumerateArray())
                {
                    AssertFalse(sig.GetProperty("Read").GetBoolean(), "All returned signals should be unread");
                    AssertEqual(recipientId, sig.GetProperty("ToCaptainId").GetString()!, "All signals should be to the recipient");
                }
            });

            await RunTest("Signal_EnumerateByRecipient_All", async () =>
            {
                string recipientId = await CreateCaptainAsync("signal-all-recipient");

                // Create two unread signals
                await CreateSignalAsync("Nudge", "all-unread-1", toCaptainId: recipientId);
                await CreateSignalAsync("Mail", "all-unread-2", toCaptainId: recipientId);

                // Create one signal and mark it as read
                JsonElement readSignal = await CreateSignalAsync("Heartbeat", "all-will-be-read", toCaptainId: recipientId);
                string readSignalId = readSignal.GetProperty("Id").GetString()!;
                await _AuthClient.PutAsync(
                    "/api/v1/signals/" + readSignalId + "/read",
                    new StringContent("{}", Encoding.UTF8, "application/json"));

                // Enumerate with unreadOnly=false to get all signals
                HttpResponseMessage response = await _AuthClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=false");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();
                JsonDocument doc = JsonDocument.Parse(body);
                JsonElement signals = doc.RootElement;

                AssertEqual(3, signals.GetArrayLength(), "Should return all 3 signals (read and unread)");

                // Verify all signals belong to the recipient
                foreach (JsonElement sig in signals.EnumerateArray())
                {
                    AssertEqual(recipientId, sig.GetProperty("ToCaptainId").GetString()!, "All signals should be to the recipient");
                }
            });

            #endregion

            #region Signal-EnumerateRecent-Tests

            await RunTest("Signal_EnumerateRecent", async () =>
            {
                // Create several signals with small delays for ordering
                await CreateSignalAsync("Nudge", "recent-1");
                await Task.Delay(50);
                await CreateSignalAsync("Mail", "recent-2");
                await Task.Delay(50);
                await CreateSignalAsync("Heartbeat", "recent-3");
                await Task.Delay(50);
                await CreateSignalAsync("Nudge", "recent-4");
                await Task.Delay(50);
                await CreateSignalAsync("Mail", "recent-5");

                // Request only 3 most recent
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/signals/recent?count=3");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();
                JsonDocument doc = JsonDocument.Parse(body);
                JsonElement signals = doc.RootElement;

                AssertEqual(3, signals.GetArrayLength(), "Should return exactly 3 signals");

                // Verify descending order by CreatedUtc
                for (int i = 0; i < signals.GetArrayLength() - 1; i++)
                {
                    DateTime current = DateTime.Parse(signals[i].GetProperty("CreatedUtc").GetString()!);
                    DateTime next = DateTime.Parse(signals[i + 1].GetProperty("CreatedUtc").GetString()!);
                    Assert(current >= next, "Recent signals should be in descending order by CreatedUtc");
                }
            });

            #endregion

            #region Signal-MarkRead-Tests

            await RunTest("Signal_MarkRead", async () =>
            {
                string recipientId = await CreateCaptainAsync("signal-markread-recipient");

                // Create an unread signal
                JsonElement created = await CreateSignalAsync("Nudge", "markread-test", toCaptainId: recipientId);
                string signalId = created.GetProperty("Id").GetString()!;
                AssertFalse(created.GetProperty("Read").GetBoolean(), "New signal should be unread");

                // Mark it as read
                HttpResponseMessage markResponse = await _AuthClient.PutAsync(
                    "/api/v1/signals/" + signalId + "/read",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                AssertEqual(HttpStatusCode.OK, markResponse.StatusCode);

                // Verify the signal is now read via direct read
                HttpResponseMessage readResponse = await _AuthClient.GetAsync("/api/v1/signals/" + signalId);
                AssertEqual(HttpStatusCode.OK, readResponse.StatusCode);

                string readBody = await readResponse.Content.ReadAsStringAsync();
                JsonDocument readDoc = JsonDocument.Parse(readBody);
                AssertTrue(readDoc.RootElement.GetProperty("Read").GetBoolean(), "Signal should be marked as read");

                // Verify EnumerateByRecipient with unreadOnly=true no longer returns this signal
                HttpResponseMessage recipientResponse = await _AuthClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=true");
                AssertEqual(HttpStatusCode.OK, recipientResponse.StatusCode);

                string recipientBody = await recipientResponse.Content.ReadAsStringAsync();
                JsonDocument recipientDoc = JsonDocument.Parse(recipientBody);
                JsonElement recipientSignals = recipientDoc.RootElement;

                // The marked-read signal should not appear in unread results
                foreach (JsonElement sig in recipientSignals.EnumerateArray())
                {
                    AssertNotEqual(signalId, sig.GetProperty("Id").GetString()!, "Marked-read signal should not appear in unread results");
                }
            });

            #endregion

            #region Signal-EnumeratePaginated-Tests

            await RunTest("Signal_EnumeratePaginated", async () =>
            {
                // Create 12 signals for pagination testing
                for (int i = 0; i < 12; i++)
                {
                    await CreateSignalAsync("Nudge", "paginated-" + i);
                }

                // Page 1 with size 5
                StringContent page1Content = new StringContent(
                    JsonSerializer.Serialize(new { PageSize = 5, PageNumber = 1 }),
                    Encoding.UTF8, "application/json");

                HttpResponseMessage page1Response = await _AuthClient.PostAsync("/api/v1/signals/enumerate", page1Content);
                AssertEqual(HttpStatusCode.OK, page1Response.StatusCode);

                string page1Body = await page1Response.Content.ReadAsStringAsync();
                JsonDocument page1Doc = JsonDocument.Parse(page1Body);
                JsonElement page1Root = page1Doc.RootElement;

                AssertEqual(5, page1Root.GetProperty("Objects").GetArrayLength(), "Page 1 should have 5 items");
                AssertEqual(1, page1Root.GetProperty("PageNumber").GetInt32());
                AssertEqual(5, page1Root.GetProperty("PageSize").GetInt32());
                AssertTrue(page1Root.GetProperty("TotalRecords").GetInt32() >= 12, "TotalRecords should be at least 12");
                AssertTrue(page1Root.GetProperty("TotalPages").GetInt32() >= 3, "TotalPages should be at least 3");
                AssertTrue(page1Root.GetProperty("Success").GetBoolean());

                // Page 2 with size 5
                StringContent page2Content = new StringContent(
                    JsonSerializer.Serialize(new { PageSize = 5, PageNumber = 2 }),
                    Encoding.UTF8, "application/json");

                HttpResponseMessage page2Response = await _AuthClient.PostAsync("/api/v1/signals/enumerate", page2Content);
                string page2Body = await page2Response.Content.ReadAsStringAsync();
                JsonDocument page2Doc = JsonDocument.Parse(page2Body);
                JsonElement page2Root = page2Doc.RootElement;

                AssertEqual(5, page2Root.GetProperty("Objects").GetArrayLength(), "Page 2 should have 5 items");
                AssertEqual(2, page2Root.GetProperty("PageNumber").GetInt32());

                // Verify page 1 and page 2 have different signal IDs
                string firstIdPage1 = page1Root.GetProperty("Objects")[0].GetProperty("Id").GetString()!;
                string firstIdPage2 = page2Root.GetProperty("Objects")[0].GetProperty("Id").GetString()!;
                AssertNotEqual(firstIdPage1, firstIdPage2, "Page 1 and Page 2 should have different first items");
            });

            #endregion

            #region Cleanup

            await CleanupAsync();

            #endregion
        }

        #endregion

        #region Private-Methods

        private async Task<string> CreateCaptainAsync(string name)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = new StringContent(
                JsonSerializer.Serialize(new { Name = uniqueName }),
                Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/captains", content);
            string body = await resp.Content.ReadAsStringAsync();
            string captainId = JsonDocument.Parse(body).RootElement.GetProperty("Id").GetString()!;
            _CreatedCaptainIds.Add(captainId);
            return captainId;
        }

        private async Task<JsonElement> CreateSignalAsync(
            string type = "Nudge",
            string? payload = null,
            string? toCaptainId = null,
            string? fromCaptainId = null)
        {
            object body;
            if (toCaptainId != null && fromCaptainId != null)
                body = new { Type = type, Payload = payload, ToCaptainId = toCaptainId, FromCaptainId = fromCaptainId };
            else if (toCaptainId != null)
                body = new { Type = type, Payload = payload, ToCaptainId = toCaptainId };
            else if (fromCaptainId != null)
                body = new { Type = type, Payload = payload, FromCaptainId = fromCaptainId };
            else if (payload != null)
                body = new { Type = type, Payload = payload };
            else
                body = new { Type = type };

            StringContent content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/signals", content);
            string responseBody = await response.Content.ReadAsStringAsync();
            JsonElement root = JsonDocument.Parse(responseBody).RootElement;
            _CreatedSignalIds.Add(root.GetProperty("Id").GetString()!);
            return root;
        }

        private async Task CleanupAsync()
        {
            foreach (string signalId in _CreatedSignalIds)
            {
                try { await _AuthClient.DeleteAsync("/api/v1/signals/" + signalId); } catch { }
            }

            foreach (string captainId in _CreatedCaptainIds)
            {
                try { await _AuthClient.DeleteAsync("/api/v1/captains/" + captainId); } catch { }
            }
        }

        #endregion
    }
}
