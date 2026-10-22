namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RuntimeProviderErrorParser"/>. Positive cases confirm the structured channels
    /// (Claude Code API Error and usage-limit protocol lines, the stream-json error result, HTTP statuses) yield typed
    /// provider errors; negative cases confirm ordinary output that merely mentions statuses, limits, or billing does
    /// not.
    /// </summary>
    public sealed class RuntimeProviderErrorParserSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("claude_api_error_line_with_body", "A Claude API Error line yields its status and deserialized error type", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseClaudeTextLine(
                    "API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Number of request tokens has exceeded your per-minute rate limit\"}}");
                AssertNotNull(error);
                AssertEqual(429, error!.HttpStatusCode);
                AssertEqual("rate_limit_error", error.ErrorType);
                AssertContains("per-minute rate limit", error.Message ?? String.Empty);
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, error));
            }));

            cases.Add(Case("claude_api_error_line_with_trailing_hint", "A Claude API Error line with a trailing hint still deserializes", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseClaudeTextLine(
                    "API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}} \u00b7 Please run /login");
                AssertNotNull(error);
                AssertEqual(401, error!.HttpStatusCode);
                AssertEqual("authentication_error", error.ErrorType);
                AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, error));
            }));

            cases.Add(Case("claude_api_error_status_only", "A Claude API Error line without a JSON body still carries its status", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseClaudeTextLine("API Error: 529 Overloaded");
                AssertNotNull(error);
                AssertEqual(529, error!.HttpStatusCode);
                AssertNull(error.ErrorType);
            }));

            cases.Add(Case("claude_usage_limit_line", "The Claude usage-limit protocol line yields a reset time", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseClaudeTextLine("Claude AI usage limit reached|1768478400");
                AssertNotNull(error);
                AssertEqual("usage_limit_reached", error!.ErrorType);
                AssertEqual(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), error.ResetUtc!.Value);
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, error));
            }));

            cases.Add(Case("ordinary_output_is_not_an_error", "Ordinary output that mentions limits, statuses, or billing is not a provider error", TestTags.Negative, () =>
            {
                string[] lines = new string[]
                {
                    "src/Services/BillingService.cs(42,7): error CS0403: Cannot convert null to type parameter",
                    "HTTP 429 Too Many Requests",
                    "permission denied: /var/run/docker.sock",
                    "You've hit your limit and must wait for reset.",
                    "The test asserts on 'API Error: 429' in the client",
                    "  > API Error: 429 {\"type\":\"error\"}",
                    "API Error: abc",
                    "Claude AI usage limit reached",
                    "Claude AI usage limit reached|soon",
                    ""
                };
                foreach (string line in lines)
                {
                    AssertNull(RuntimeProviderErrorParser.TryParseClaudeTextLine(line), "not an error: " + line);
                }
                AssertNull(RuntimeProviderErrorParser.TryParseClaudeTextLine(null));
            }));

            cases.Add(Case("stream_json_error_result", "A stream-json error result event yields the embedded API error", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseClaudeStreamJsonLine(
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"API Error: 403 {\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"type\\\":\\\"permission_error\\\",\\\"message\\\":\\\"no access\\\"}}\"}");
                AssertNotNull(error);
                AssertEqual(403, error!.HttpStatusCode);
                AssertEqual("permission_error", error.ErrorType);
            }));

            cases.Add(Case("stream_json_non_error_events_ignored", "Stream-json events that are not error results yield nothing", TestTags.Negative, () =>
            {
                AssertNull(RuntimeProviderErrorParser.TryParseClaudeStreamJsonLine("{\"type\":\"result\",\"is_error\":false,\"result\":\"API Error: 429\"}"));
                AssertNull(RuntimeProviderErrorParser.TryParseClaudeStreamJsonLine("{\"type\":\"assistant\",\"is_error\":true,\"result\":\"API Error: 429\"}"));
                AssertNull(RuntimeProviderErrorParser.TryParseClaudeStreamJsonLine("API Error: 429"));
                AssertNull(RuntimeProviderErrorParser.TryParseClaudeStreamJsonLine("{not json"));
            }));

            cases.Add(Case("http_status_errors", "HTTP error statuses build provider errors; success statuses do not", TestTags.Positive, () =>
            {
                RuntimeProviderError? error = RuntimeProviderErrorParser.FromHttpStatus(429, "slow down", 30);
                AssertNotNull(error);
                AssertEqual(429, error!.HttpStatusCode);
                AssertEqual(30, error.RetryAfterSeconds);
                AssertNull(RuntimeProviderErrorParser.FromHttpStatus(200, "ok"));
                AssertNull(RuntimeProviderErrorParser.FromHttpStatus(0, null));
            }));

            cases.Add(Case("cli_stderr_statuses_classified", "Gemini, Cursor and OpenCode stderr lines with an explicit HTTP status, a Google RPC status or a network code are classified instead of crashing", TestTags.Positive, () =>
            {
                Dictionary<string, RuntimeFailureKindEnum> lines = new Dictionary<string, RuntimeFailureKindEnum>
                {
                    ["[API Error: got status: 429 Too Many Requests. Please wait before retrying.]"] = RuntimeFailureKindEnum.UsageLimit,
                    ["Error: Request failed with status code 503"] = RuntimeFailureKindEnum.Transient,
                    ["AI_APICallError: Unauthorized (HTTP 401)"] = RuntimeFailureKindEnum.AuthFailure,
                    ["upstream returned HTTP/1.1 502 Bad Gateway"] = RuntimeFailureKindEnum.Transient,
                    ["{\"error\":{\"code\":429,\"message\":\"Quota exceeded\",\"status\":\"RESOURCE_EXHAUSTED\"}}"] = RuntimeFailureKindEnum.UsageLimit,
                    ["Error: [UNAUTHENTICATED] API key not valid"] = RuntimeFailureKindEnum.AuthFailure,
                    ["{\"error\":{\"message\":\"The model is overloaded\",\"status\":\"UNAVAILABLE\"}}"] = RuntimeFailureKindEnum.Transient,
                    ["TypeError: fetch failed (cause: connect ECONNREFUSED 127.0.0.1:11434)"] = RuntimeFailureKindEnum.Transient,
                };
                foreach (KeyValuePair<string, RuntimeFailureKindEnum> entry in lines)
                {
                    RuntimeProviderError? error = RuntimeProviderErrorParser.TryParseCliStderrLine(entry.Key, "Gemini");
                    AssertNotNull(error, entry.Key);
                    AssertEqual(entry.Value, RuntimeFailureClassifier.Classify(1, error), entry.Key);
                    AssertContains("Gemini reported a provider error", error!.Reason ?? String.Empty);
                }
            }));

            cases.Add(Case("cli_stderr_ordinary_lines_ignored", "Ordinary stderr (bare numbers, log lines, words like unavailable) is not a provider error", TestTags.Negative, () =>
            {
                foreach (string line in new[] { "", "   ", "Loaded 429 files", "Processed 503 items in 2s", "warning: deprecated flag --foo",
                    "The service is unavailable on Windows", "status: ok", "HTTP 200 OK", "Retrying in 3 seconds" })
                    AssertNull(RuntimeProviderErrorParser.TryParseCliStderrLine(line, "Cursor"), line);
            }));

            cases.Add(Case("cli_runtimes_report_stderr_provider_errors", "A Gemini, Cursor or OpenCode process that prints a rate limit to stderr and exits 1 raises a typed provider error; the same text on stdout raises none", TestTags.Positive, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                foreach (Func<LoggingModule, string, BaseAgentRuntime> factory in new Func<LoggingModule, string, BaseAgentRuntime>[] {
                    (log, stub) => new GeminiRuntime(log) { ExecutablePath = stub },
                    (log, stub) => new CursorRuntime(log) { ExecutablePath = stub },
                    (log, stub) => new OpenCodeRuntime(log) { ExecutablePath = stub } })
                {
                    AssertEqual(RuntimeFailureKindEnum.UsageLimit, RunStubClassification(factory, ">&2"));
                    AssertEqual(RuntimeFailureKindEnum.Crash, RunStubClassification(factory, String.Empty));
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.RuntimeProviderErrorParser",
                displayName: "Runtime Provider Error Parser",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static RuntimeFailureKindEnum RunStubClassification(Func<LoggingModule, string, BaseAgentRuntime> factory, string redirect)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The stub CLI is a POSIX shell script.");
            string root = Path.Combine(Path.GetTempPath(), "armada-stderr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string stub = Path.Combine(root, "stub.sh");
                File.WriteAllText(stub, "#!/bin/sh\ncat > /dev/null\necho 'Error: Request failed with status code 429 Too Many Requests' " + redirect + "\nexit 1\n");
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                BaseAgentRuntime runtime = factory(logging, stub);
                RuntimeProviderError? error = null;
                int? exitCode = null;
                using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                runtime.OnProviderError += (pid, e) => error = RuntimeFailureClassifier.KeepMoreSignificant(error, e);
                runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                runtime.StartAsync(root, "say hi").GetAwaiter().GetResult();
                AssertTrue(exited.Wait(TimeSpan.FromSeconds(15)), "stub did not exit");
                return RuntimeFailureClassifier.Classify(exitCode, error);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.RuntimeProviderErrorParser",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
