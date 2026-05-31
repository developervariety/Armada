namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the shared <see cref="CodeContextTimeouts"/> resolver introduced in M3.
    /// Pins the env-var name and default constants, the clamp range [100, 300000] ms,
    /// the invalid-input fallbacks, and the per-request override precedence used by
    /// armada_context_pack. The dispatch path and explicit-tool path both funnel through
    /// this resolver, so these unit tests guard the behavior both callers rely on.
    /// </summary>
    public class CodeContextTimeoutsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Code Context Timeouts";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constants_MatchPublishedContract", () =>
            {
                AssertEqual("ARMADA_CODE_CONTEXT_TIMEOUT_MS", CodeContextTimeouts.TimeoutEnvVar);
                AssertEqual(75_000, CodeContextTimeouts.DefaultDispatchTimeoutMs,
                    "Dispatch default must be 75 seconds");
                AssertEqual(120_000, CodeContextTimeouts.DefaultExplicitTimeoutMs,
                    "Explicit-tool default must be 120 seconds");
            });

            await RunTest("Resolve_EnvVarAbsent_ReturnsDefaultUnclamped", () =>
            {
                WithEnvVar(null, () =>
                {
                    AssertEqual(75_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Absent env var must return the supplied default");
                    // The default path does NOT clamp -- callers own their default values.
                    AssertEqual(50, (int)CodeContextTimeouts.Resolve(50).TotalMilliseconds,
                        "Default path returns the raw default even below the env-var clamp floor");
                });
            });

            await RunTest("Resolve_EnvVarWithinRange_UsesEnvValue", () =>
            {
                WithEnvVar("5000", () =>
                {
                    AssertEqual(5000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Valid env var must override the default");
                });
            });

            await RunTest("Resolve_EnvVarBelowFloor_ClampsTo100", () =>
            {
                WithEnvVar("50", () =>
                {
                    AssertEqual(100, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Env var below 100 must clamp up to 100");
                });
            });

            await RunTest("Resolve_EnvVarAboveCeiling_ClampsTo300000", () =>
            {
                WithEnvVar("999999", () =>
                {
                    AssertEqual(300_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Env var above 300000 must clamp down to 300000");
                });
            });

            await RunTest("Resolve_EnvVarExactBoundaries_AcceptedAsIs", () =>
            {
                WithEnvVar("100", () =>
                    AssertEqual(100, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Lower clamp boundary 100 is accepted exactly"));
                WithEnvVar("300000", () =>
                    AssertEqual(300_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Upper clamp boundary 300000 is accepted exactly"));
            });

            await RunTest("Resolve_EnvVarZero_FallsBackToDefault", () =>
            {
                WithEnvVar("0", () =>
                    AssertEqual(75_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Zero is rejected (must be > 0) and falls back to default"));
            });

            await RunTest("Resolve_EnvVarNegative_FallsBackToDefault", () =>
            {
                WithEnvVar("-100", () =>
                    AssertEqual(75_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Negative is rejected and falls back to default"));
            });

            await RunTest("Resolve_EnvVarNonNumeric_FallsBackToDefault", () =>
            {
                WithEnvVar("not-a-number", () =>
                    AssertEqual(75_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Non-numeric env var falls back to default"));
            });

            await RunTest("Resolve_EnvVarEmptyString_FallsBackToDefault", () =>
            {
                WithEnvVar(String.Empty, () =>
                    AssertEqual(75_000, (int)CodeContextTimeouts.Resolve(75_000).TotalMilliseconds,
                        "Empty env var falls back to default"));
            });

            await RunTest("ResolveForExplicitTool_RequestTimeout_WinsOverEnvVar", () =>
            {
                WithEnvVar("999999", () =>
                {
                    JsonElement args = ArgsWith(new { timeoutMs = 10_000 });
                    AssertEqual(10_000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Per-request timeoutMs takes precedence over the env var");
                });
            });

            await RunTest("ResolveForExplicitTool_RequestTimeoutBelowFloor_ClampsTo100", () =>
            {
                WithEnvVar(null, () =>
                {
                    JsonElement args = ArgsWith(new { timeoutMs = 5 });
                    AssertEqual(100, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Per-request timeoutMs below 100 clamps to 100");
                });
            });

            await RunTest("ResolveForExplicitTool_RequestTimeoutAboveCeiling_ClampsTo300000", () =>
            {
                WithEnvVar(null, () =>
                {
                    JsonElement args = ArgsWith(new { timeoutMs = 10_000_000 });
                    AssertEqual(300_000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Per-request timeoutMs above 300000 clamps to 300000");
                });
            });

            await RunTest("ResolveForExplicitTool_RequestTimeoutZero_FallsThroughToEnvVar", () =>
            {
                WithEnvVar("7000", () =>
                {
                    JsonElement args = ArgsWith(new { timeoutMs = 0 });
                    AssertEqual(7000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "timeoutMs=0 is ignored (must be > 0); env var is consulted next");
                });
            });

            await RunTest("ResolveForExplicitTool_RequestTimeoutNegative_FallsThroughToEnvVar", () =>
            {
                WithEnvVar("7000", () =>
                {
                    JsonElement args = ArgsWith(new { timeoutMs = -5 });
                    AssertEqual(7000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Negative timeoutMs is ignored; env var is consulted next");
                });
            });

            await RunTest("ResolveForExplicitTool_TimeoutMsWrongType_FallsThroughToDefault", () =>
            {
                WithEnvVar(null, () =>
                {
                    // A string-typed timeoutMs has ValueKind String, not Number -- it must be ignored.
                    JsonElement args = ArgsWith(new { timeoutMs = "5000" });
                    AssertEqual(120_000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Non-number timeoutMs is ignored and falls back to the explicit default");
                });
            });

            await RunTest("ResolveForExplicitTool_NoTimeoutMsNoEnvVar_UsesExplicitDefault", () =>
            {
                WithEnvVar(null, () =>
                {
                    JsonElement args = ArgsWith(new { goal = "no timeout supplied" });
                    AssertEqual(120_000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "Absent timeoutMs and env var must use the 120s explicit default");
                });
            });

            await RunTest("ResolveForExplicitTool_NoTimeoutMsWithEnvVar_UsesEnvVar", () =>
            {
                WithEnvVar("8000", () =>
                {
                    JsonElement args = ArgsWith(new { goal = "env only" });
                    AssertEqual(8000, (int)CodeContextTimeouts.ResolveForExplicitTool(args).TotalMilliseconds,
                        "With no per-request timeoutMs, the env var overrides the explicit default");
                });
            });
        }

        #region Private-Methods

        /// <summary>
        /// Serializes an anonymous object into a <see cref="JsonElement"/> for resolver input.
        /// </summary>
        private static JsonElement ArgsWith(object value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        /// <summary>
        /// Runs <paramref name="body"/> with the timeout env var set to <paramref name="value"/>,
        /// restoring the prior value afterward so tests do not leak process-global state.
        /// </summary>
        private static void WithEnvVar(string? value, Action body)
        {
            string? prior = Environment.GetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar);
            Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, value);
            try
            {
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CodeContextTimeouts.TimeoutEnvVar, prior);
            }
        }

        #endregion
    }
}
