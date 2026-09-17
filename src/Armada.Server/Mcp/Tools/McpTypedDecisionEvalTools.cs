namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers the operator tool that runs the synthetic typed-decision evaluation set against the live
    /// provider. It is an operator tool: it spends provider calls and is not in the caller-scoped set.
    /// </summary>
    public static class McpTypedDecisionEvalTools
    {
        #region Public-Members

        /// <summary>Registered name of the evaluation tool.</summary>
        public const string ToolName = "armada_typed_decision_eval";

        /// <summary>Reason returned when a caller other than a global administrator calls the tool.</summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the evaluation tool.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="evalService">The evaluation service.</param>
        public static void Register(RegisterToolDelegate register, TypedDecisionEvalService evalService)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (evalService == null) throw new ArgumentNullException(nameof(evalService));

            register(
                ToolName,
                "Run the synthetic typed-decision evaluation set against the live TypeSafe provider and return the report: "
                + "the model version the provider reported, and for each case whether its reference answers held (Reference) or "
                + "its two variants agreed (Consistency), with the answers received. Each case builds its request through the "
                + "decision's own adapter, so it tests the questions production sends. The run also records one "
                + "typed_decision.eval event. It spends roughly two provider calls per case. Use it after changing typed-decision "
                + "questions or thresholds, or to check a new model version; it also runs by itself when the provider reports "
                + "a model version that has not been evaluated. Optional 'decision' limits the run to one decision point. "
                + "Returns status 'busy' when a run is already in progress.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        decision = new { type = "string", description = "Run only this decision point's cases (for example failure_cause)." }
                    }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin)
                        return (object)new { Error = "Running the typed-decision evaluation requires a global administrator.", Reason = GlobalAdministratorRequiredReason };

                    string? decision = null;
                    if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object)
                    {
                        EvalArgs? parsed = JsonSerializer.Deserialize<EvalArgs>(args.Value.GetRawText());
                        decision = String.IsNullOrWhiteSpace(parsed?.Decision) ? null : parsed!.Decision!.Trim();
                    }

                    TypedDecisionEvalReport? report = await evalService.RunAsync(decision, "operator", CancellationToken.None).ConfigureAwait(false);
                    if (report == null) return (object)new { status = "busy" };
                    return (object)new { status = "complete", report };
                });
        }

        #endregion

        #region Private-Types

        private sealed class EvalArgs
        {
            [System.Text.Json.Serialization.JsonPropertyName("decision")]
            public string? Decision { get; set; }
        }

        #endregion
    }
}
