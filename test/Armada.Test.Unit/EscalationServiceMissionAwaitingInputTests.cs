namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for EscalationService handling the MissionAwaitingInput trigger,
    /// including structured webhook payload and the injectable HttpMessageHandler seam.
    /// </summary>
    public class EscalationServiceMissionAwaitingInputTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Escalation Service MissionAwaitingInput";

        #region Doubles

        /// <summary>
        /// Hand-rolled HTTP message handler that captures the last request body sent to it.
        /// </summary>
        private sealed class CapturingHttpHandler : HttpMessageHandler
        {
            public string? LastRequestBody { get; private set; }
            public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                return Task.FromResult(new HttpResponseMessage(ResponseStatusCode));
            }
        }

        #endregion

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        /// <summary>Run escalation service tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FireAsync_MissionAwaitingInput_SendsWebhook_WithStructuredPayload", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CapturingHttpHandler handler = new CapturingHttpHandler();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.EscalationRules.Add(new EscalationRule(EscalationTriggerEnum.MissionAwaitingInput, EscalationActionEnum.Webhook)
                    {
                        WebhookUrl = "http://localhost:9999/webhook",
                        CooldownMinutes = 0
                    });

                    EscalationService svc = new EscalationService(logging, testDb.Driver, settings, handler);

                    string message = JsonSerializer.Serialize(new
                    {
                        trigger = "MissionAwaitingInput",
                        voyageId = "vyg_test",
                        missionId = "msn_test",
                        mode = "block",
                        questionText = "Which DB engine to use?"
                    });

                    await svc.FireAsync(EscalationTriggerEnum.MissionAwaitingInput, "msn_test", message).ConfigureAwait(false);

                    AssertNotNull(handler.LastRequestBody, "Webhook body should have been sent");
                    AssertTrue(handler.LastRequestBody!.Contains("MissionAwaitingInput", StringComparison.Ordinal), "Payload should include trigger");
                    AssertTrue(handler.LastRequestBody.Contains("msn_test", StringComparison.Ordinal), "Payload should include missionId");
                    AssertTrue(handler.LastRequestBody.Contains("vyg_test", StringComparison.Ordinal), "Payload should include voyageId");
                    AssertTrue(handler.LastRequestBody.Contains("Which DB engine to use?", StringComparison.Ordinal), "Payload should include questionText");

                    // Verify forbidden fields are absent
                    AssertFalse(handler.LastRequestBody.Contains("token", StringComparison.OrdinalIgnoreCase), "Payload must not include token");
                    AssertFalse(handler.LastRequestBody.Contains("bearer", StringComparison.OrdinalIgnoreCase), "Payload must not include bearer");
                    AssertFalse(handler.LastRequestBody.Contains("apiKey", StringComparison.OrdinalIgnoreCase), "Payload must not include apiKey");
                    AssertFalse(handler.LastRequestBody.Contains("password", StringComparison.OrdinalIgnoreCase), "Payload must not include password");
                    AssertFalse(handler.LastRequestBody.Contains("environment", StringComparison.OrdinalIgnoreCase), "Payload must not include environment");
                }
            });

            await RunTest("FireAsync_MissionAwaitingInput_WebhookPayload_IsValidJson", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CapturingHttpHandler handler = new CapturingHttpHandler();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.EscalationRules.Add(new EscalationRule(EscalationTriggerEnum.MissionAwaitingInput, EscalationActionEnum.Webhook)
                    {
                        WebhookUrl = "http://localhost:9999/webhook",
                        CooldownMinutes = 0
                    });

                    EscalationService svc = new EscalationService(logging, testDb.Driver, settings, handler);
                    string message = JsonSerializer.Serialize(new
                    {
                        trigger = "MissionAwaitingInput",
                        voyageId = "vyg_json",
                        missionId = "msn_json",
                        mode = "block",
                        questionText = "Which strategy?"
                    });

                    await svc.FireAsync(EscalationTriggerEnum.MissionAwaitingInput, "msn_json", message).ConfigureAwait(false);

                    AssertNotNull(handler.LastRequestBody, "Request body should not be null");
                    JsonDocument parsed = JsonDocument.Parse(handler.LastRequestBody!);
                    AssertTrue(parsed.RootElement.ValueKind == JsonValueKind.Object, "Payload should be a JSON object");
                }
            });

            await RunTest("FireAsync_MissionAwaitingInput_NonWebhookRule_LogsOnly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CapturingHttpHandler handler = new CapturingHttpHandler();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.EscalationRules.Add(new EscalationRule(EscalationTriggerEnum.MissionAwaitingInput, EscalationActionEnum.Log)
                    {
                        CooldownMinutes = 0
                    });

                    EscalationService svc = new EscalationService(logging, testDb.Driver, settings, handler);
                    string message = JsonSerializer.Serialize(new
                    {
                        trigger = "MissionAwaitingInput",
                        voyageId = "vyg_log",
                        missionId = "msn_log",
                        mode = "block",
                        questionText = "Log only test"
                    });

                    await svc.FireAsync(EscalationTriggerEnum.MissionAwaitingInput, "msn_log", message).ConfigureAwait(false);
                    AssertNull(handler.LastRequestBody, "Log-only rule should not send HTTP request");
                }
            });

            await RunTest("FireAsync_NoMatchingRule_DoesNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CapturingHttpHandler handler = new CapturingHttpHandler();
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
                    // No rules configured for MissionAwaitingInput

                    EscalationService svc = new EscalationService(logging, testDb.Driver, settings, handler);
                    await svc.FireAsync(EscalationTriggerEnum.MissionAwaitingInput, "msn_none", "{}").ConfigureAwait(false);
                    AssertNull(handler.LastRequestBody, "No rule should produce no HTTP request");
                }
            });
        }
    }
}
