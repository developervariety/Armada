namespace Armada.Test.Runtimes.Suites
{
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pinning tests for OpenCode reasoning-effort validation and runtime enum acceptance.
    /// </summary>
    public class OpenCodeRuntimeTests : TestSuite
    {
        /// <summary>Suite display name.</summary>
        public override string Name => "OpenCode Runtime Tests";

        /// <summary>Run all OpenCode runtime pinning tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AgentRuntimeEnum_OpenCode_ParseSucceeds", () =>
            {
                bool parsed = Enum.TryParse("OpenCode", ignoreCase: true, out AgentRuntimeEnum runtime);
                AssertTrue(parsed, "Enum.TryParse must accept OpenCode for armada_create_captain runtime");
                AssertEqual(AgentRuntimeEnum.OpenCode, runtime);
            });

            await RunTest("ValidateReasoningEffort_Null_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, null);
                AssertNull(error, "Null reasoningEffort must be accepted (use OpenCode default)");
            });

            await RunTest("ValidateReasoningEffort_Low_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "low");
                AssertNull(error, "low must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Medium_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "medium");
                AssertNull(error, "medium must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "high");
                AssertNull(error, "high must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "xhigh");
                AssertNull(error, "xhigh must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "max");
                AssertNull(error, "max must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_InvalidValue_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "ultra");
                AssertNotNull(error, "Unrecognised value must be rejected for OpenCode");
                AssertContains("Accepted values: low, medium, high, xhigh, max.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_CaseInsensitive_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "HIGH");
                AssertNull(error, "Validation must be case-insensitive");
            });

            await RunTest("ValidateReasoningEffort_Whitespace_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "   ");
                AssertNull(error, "Whitespace-only reasoningEffort must be treated as unset");
            });
        }
    }
}
