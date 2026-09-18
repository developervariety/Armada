namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// What a captain's runtime can actually do, as opposed to what its record permits. Permission and
    /// capability are different questions: a captain's allow-list says which personas its operator WANTS
    /// it to fill, while this says which ones its runtime CAN fill at all.
    /// </summary>
    /// <remarks>
    /// This is the one definition. Both the assignment-time eligibility check and the launch-time refusal
    /// ask it, so the two cannot drift into disagreeing — and a disagreement here is expensive in a
    /// specific way: eligibility that is more permissive than the launch guard assigns a mission to a
    /// captain that then refuses it, which turns "pick another captain" into a failed mission, an incident
    /// and a rescue.
    /// </remarks>
    public static class AgentRuntimeCapability
    {
        #region Public-Methods

        /// <summary>
        /// Whether a runtime can run commands in a dock for a dispatched mission: a shell, git, and the
        /// vessel's test command.
        /// </summary>
        /// <remarks>
        /// Every CLI runtime shells out to a harness that carries its own command tool. The API-endpoint
        /// runtime drives a model through an in-process tool loop, and a mission launch registers the
        /// built-in <c>run_command</c> tool for it, so it qualifies too. The question is asked about MISSIONS
        /// because that is the only place persona eligibility applies; the dashboard chat path builds the same
        /// runtime with the command tool deliberately left off.
        ///
        /// Every runtime answers true today. The predicate stays as the one seam that eligibility and the
        /// launch refusal both ask, so a runtime added later without a command tool is excluded from those
        /// personas by changing this one answer, in both places at once.
        /// </remarks>
        /// <param name="runtime">Runtime to classify.</param>
        /// <returns>True when a mission on the runtime can execute commands.</returns>
        public static bool ExecutesCommands(AgentRuntimeEnum runtime)
        {
            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode:
                case AgentRuntimeEnum.Codex:
                case AgentRuntimeEnum.Gemini:
                case AgentRuntimeEnum.Cursor:
                case AgentRuntimeEnum.OpenCode:
                case AgentRuntimeEnum.Mux:
                case AgentRuntimeEnum.ApiEndpoint:
                case AgentRuntimeEnum.Custom:
                    return true;
                default:
                    // An unknown runtime has proven nothing, so it is not trusted with an execution persona.
                    return false;
            }
        }

        /// <summary>
        /// Whether a runtime can serve a persona. A persona that must run commands needs a runtime that
        /// can; every other persona is served by any runtime.
        /// </summary>
        /// <param name="runtime">Runtime to classify.</param>
        /// <param name="persona">Persona the mission runs as; null or blank imposes no requirement.</param>
        /// <returns>True when the runtime can serve the persona.</returns>
        public static bool CanServePersona(AgentRuntimeEnum runtime, string? persona)
        {
            if (!PersonaCatalog.RequiresCommandExecution(persona)) return true;
            return ExecutesCommands(runtime);
        }

        #endregion
    }
}
