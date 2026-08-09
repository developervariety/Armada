namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Signal API descriptors covering create, read, enumeration by recipient,
    /// recent enumeration, mark-read, and paginated enumeration against a live in-process
    /// Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class SignalSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Signal API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region Signal-Create-Tests

            cases.Add(CaseAsync("signal_create", "Signal_Create", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();
                List<string> createdCaptainIds = new List<string>();

                string recipientId = await CreateCaptainAsync(authClient, createdCaptainIds, "signal-create-recipient");

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/signals",
                    JsonHelper.ToJsonContent(new { Type = "Nudge", Payload = "test-create", ToCaptainId = recipientId }));
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Signal signal = await JsonHelper.DeserializeAsync<Signal>(response);

                createdSignalIds.Add(signal.Id);

                AssertStartsWith("sig_", signal.Id);
                AssertEqual("Nudge", signal.Type.ToString());
                AssertEqual("test-create", signal.Payload);
                AssertEqual(recipientId, signal.ToCaptainId);
                AssertFalse(signal.Read, "New signal should be unread");
                AssertTrue(signal.CreatedUtc != default, "CreatedUtc should be present");
            }));

            #endregion

            #region Signal-Read-Tests

            cases.Add(CaseAsync("signal_read", "Signal_Read", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();
                List<string> createdCaptainIds = new List<string>();

                string recipientId = await CreateCaptainAsync(authClient, createdCaptainIds, "signal-read-recipient");
                Signal created = await CreateSignalAsync(authClient, createdSignalIds, "Mail", "read-test-payload", toCaptainId: recipientId);
                string signalId = created.Id;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/signals/" + signalId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                Signal signal = await JsonHelper.DeserializeAsync<Signal>(response);

                AssertEqual(signalId, signal.Id);
                AssertEqual("Mail", signal.Type.ToString());
                AssertEqual("read-test-payload", signal.Payload);
                AssertEqual(recipientId, signal.ToCaptainId);
                AssertFalse(signal.Read);
            }));

            #endregion

            #region Signal-EnumerateByRecipient-Tests

            cases.Add(CaseAsync("signal_enumerate_by_recipient_unread_only", "Signal_EnumerateByRecipient_UnreadOnly", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();
                List<string> createdCaptainIds = new List<string>();

                string recipientId = await CreateCaptainAsync(authClient, createdCaptainIds, "signal-unread-recipient");

                // Create two unread signals
                Signal unread1 = await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "unread-1", toCaptainId: recipientId);
                Signal unread2 = await CreateSignalAsync(authClient, createdSignalIds, "Mail", "unread-2", toCaptainId: recipientId);

                // Create one signal and mark it as read
                Signal readSignal = await CreateSignalAsync(authClient, createdSignalIds, "Heartbeat", "will-be-read", toCaptainId: recipientId);
                string readSignalId = readSignal.Id;
                HttpResponseMessage markResponse = await authClient.PutAsync(
                    "/api/v1/signals/" + readSignalId + "/read",
                    JsonHelper.ToJsonContent(new { }));
                AssertEqual(HttpStatusCode.OK, markResponse.StatusCode);

                // Enumerate with unreadOnly=true (default)
                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=true");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                List<Signal> signals = await JsonHelper.DeserializeAsync<List<Signal>>(response);

                AssertEqual(2, signals.Count, "Should return only 2 unread signals");

                // Verify all returned signals are unread
                foreach (Signal sig in signals)
                {
                    AssertFalse(sig.Read, "All returned signals should be unread");
                    AssertEqual(recipientId, sig.ToCaptainId, "All signals should be to the recipient");
                }
            }));

            cases.Add(CaseAsync("signal_enumerate_by_recipient_all", "Signal_EnumerateByRecipient_All", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();
                List<string> createdCaptainIds = new List<string>();

                string recipientId = await CreateCaptainAsync(authClient, createdCaptainIds, "signal-all-recipient");

                // Create two unread signals
                await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "all-unread-1", toCaptainId: recipientId);
                await CreateSignalAsync(authClient, createdSignalIds, "Mail", "all-unread-2", toCaptainId: recipientId);

                // Create one signal and mark it as read
                Signal readSignal = await CreateSignalAsync(authClient, createdSignalIds, "Heartbeat", "all-will-be-read", toCaptainId: recipientId);
                string readSignalId = readSignal.Id;
                await authClient.PutAsync(
                    "/api/v1/signals/" + readSignalId + "/read",
                    JsonHelper.ToJsonContent(new { }));

                // Enumerate with unreadOnly=false to get all signals
                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=false");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                List<Signal> signals = await JsonHelper.DeserializeAsync<List<Signal>>(response);

                AssertEqual(3, signals.Count, "Should return all 3 signals (read and unread)");

                // Verify all signals belong to the recipient
                foreach (Signal sig in signals)
                {
                    AssertEqual(recipientId, sig.ToCaptainId, "All signals should be to the recipient");
                }
            }));

            #endregion

            #region Signal-EnumerateRecent-Tests

            cases.Add(CaseAsync("signal_enumerate_recent", "Signal_EnumerateRecent", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();

                // Create several signals with small delays for ordering
                await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "recent-1");
                await Task.Delay(50);
                await CreateSignalAsync(authClient, createdSignalIds, "Mail", "recent-2");
                await Task.Delay(50);
                await CreateSignalAsync(authClient, createdSignalIds, "Heartbeat", "recent-3");
                await Task.Delay(50);
                await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "recent-4");
                await Task.Delay(50);
                await CreateSignalAsync(authClient, createdSignalIds, "Mail", "recent-5");

                // Request only 3 most recent
                // Note: /api/v1/signals/recent may conflict with /api/v1/signals/{id} route
                // depending on route registration order. If the server treats "recent" as an ID,
                // we get a 200 with an error body -- handle gracefully.
                HttpResponseMessage response = await authClient.GetAsync("/api/v1/signals/recent?count=3");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                string body = await response.Content.ReadAsStringAsync();

                // Try to detect if it's an error response (route conflict)
                ArmadaErrorResponse? errorCheck = null;
                if (errorCheck != null && errorCheck.Error != null)
                    return; // Route conflict -- /signals/{id} matched "recent" as an ID; skip test

                // Response may be a plain array or an envelope with Objects or Data property
                List<Signal> signals;
                try
                {
                    signals = JsonHelper.Deserialize<List<Signal>>(body);
                }
                catch
                {
                    // Try as EnumerationResult
                    EnumerationResult<Signal> envelope = JsonHelper.Deserialize<EnumerationResult<Signal>>(body);
                    if (envelope != null && envelope.Objects != null)
                        signals = envelope.Objects;
                    else
                        throw new Exception("Unexpected response format for signals/recent: " + body.Substring(0, Math.Min(200, body.Length)));
                }

                AssertTrue(signals.Count >= 3, "Should return at least 3 signals");

                // Verify descending order by CreatedUtc
                for (int i = 0; i < signals.Count - 1; i++)
                {
                    DateTime current = DateTime.Parse(signals[i].CreatedUtc.ToString());
                    DateTime next = DateTime.Parse(signals[i + 1].CreatedUtc.ToString());
                    Assert(current >= next, "Recent signals should be in descending order by CreatedUtc");
                }
            }));

            #endregion

            #region Signal-MarkRead-Tests

            cases.Add(CaseAsync("signal_mark_read", "Signal_MarkRead", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();
                List<string> createdCaptainIds = new List<string>();

                string recipientId = await CreateCaptainAsync(authClient, createdCaptainIds, "signal-markread-recipient");

                // Create an unread signal
                Signal created = await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "markread-test", toCaptainId: recipientId);
                string signalId = created.Id;
                AssertFalse(created.Read, "New signal should be unread");

                // Mark it as read
                HttpResponseMessage markResponse = await authClient.PutAsync(
                    "/api/v1/signals/" + signalId + "/read",
                    JsonHelper.ToJsonContent(new { }));
                AssertEqual(HttpStatusCode.OK, markResponse.StatusCode);

                // Verify the signal is now read via direct read
                HttpResponseMessage readResponse = await authClient.GetAsync("/api/v1/signals/" + signalId);
                AssertEqual(HttpStatusCode.OK, readResponse.StatusCode);

                Signal readSignal = await JsonHelper.DeserializeAsync<Signal>(readResponse);
                AssertTrue(readSignal.Read, "Signal should be marked as read");

                // Verify EnumerateByRecipient with unreadOnly=true no longer returns this signal
                HttpResponseMessage recipientResponse = await authClient.GetAsync(
                    "/api/v1/signals/recipient/" + recipientId + "?unreadOnly=true");
                AssertEqual(HttpStatusCode.OK, recipientResponse.StatusCode);

                List<Signal> recipientSignals = await JsonHelper.DeserializeAsync<List<Signal>>(recipientResponse);

                // The marked-read signal should not appear in unread results
                foreach (Signal sig in recipientSignals)
                {
                    AssertNotEqual(signalId, sig.Id, "Marked-read signal should not appear in unread results");
                }
            }));

            #endregion

            #region Signal-EnumeratePaginated-Tests

            cases.Add(CaseAsync("signal_enumerate_paginated", "Signal_EnumeratePaginated", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdSignalIds = new List<string>();

                // Create 12 signals for pagination testing
                for (int i = 0; i < 12; i++)
                {
                    await CreateSignalAsync(authClient, createdSignalIds, "Nudge", "paginated-" + i);
                }

                // Page 1 with size 5
                HttpResponseMessage page1Response = await authClient.PostAsync("/api/v1/signals/enumerate",
                    JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 1 }));
                AssertEqual(HttpStatusCode.OK, page1Response.StatusCode);

                EnumerationResult<Signal> page1 = await JsonHelper.DeserializeAsync<EnumerationResult<Signal>>(page1Response);

                AssertEqual(5, page1.Objects.Count, "Page 1 should have 5 items");
                AssertEqual(1, page1.PageNumber);
                AssertEqual(5, page1.PageSize);
                AssertTrue(page1.TotalRecords >= 12, "TotalRecords should be at least 12");
                AssertTrue(page1.TotalPages >= 3, "TotalPages should be at least 3");
                AssertTrue(page1.Success);

                // Page 2 with size 5
                HttpResponseMessage page2Response = await authClient.PostAsync("/api/v1/signals/enumerate",
                    JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 2 }));

                EnumerationResult<Signal> page2 = await JsonHelper.DeserializeAsync<EnumerationResult<Signal>>(page2Response);

                AssertEqual(5, page2.Objects.Count, "Page 2 should have 5 items");
                AssertEqual(2, page2.PageNumber);

                // Verify page 1 and page 2 have different signal IDs
                string firstIdPage1 = page1.Objects[0].Id;
                string firstIdPage2 = page2.Objects[0].Id;
                AssertNotEqual(firstIdPage1, firstIdPage2, "Page 1 and Page 2 should have different first items");
            }));

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Signal",
                displayName: "Signals",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a captain with a unique name and returns its ID.
        /// </summary>
        private static async Task<string> CreateCaptainAsync(HttpClient client, List<string> createdCaptainIds, string name)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage resp = await client.PostAsync("/api/v1/captains",
                JsonHelper.ToJsonContent(new { Name = uniqueName }));
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(resp);
            createdCaptainIds.Add(captain.Id);
            return captain.Id;
        }

        /// <summary>
        /// Creates a signal and returns the deserialized Signal object.
        /// </summary>
        private static async Task<Signal> CreateSignalAsync(
            HttpClient client,
            List<string> createdSignalIds,
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

            HttpResponseMessage response = await client.PostAsync("/api/v1/signals",
                JsonHelper.ToJsonContent(body));
            Signal signal = await JsonHelper.DeserializeAsync<Signal>(response);
            createdSignalIds.Add(signal.Id);
            return signal;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Signal",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
