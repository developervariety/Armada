namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for persona operations.
    /// </summary>
    public class PersonaArgs
    {
        /// <summary>
        /// Persona name.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Persona description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Prompt template name for this persona.
        /// </summary>
        public string? PromptTemplateName { get; set; }

        /// <summary>
        /// Optional default (preferred) captain id (cpt_ prefix) for this persona. Pre-fills the per-step
        /// captain at dispatch and seeds the preferred captain for missions of this persona.
        /// </summary>
        public string? DefaultCaptainId { get; set; }
    }
}
