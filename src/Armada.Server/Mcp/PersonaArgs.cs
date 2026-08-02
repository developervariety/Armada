namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using Armada.Core.Models;

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
        /// Default playbooks for this persona. Null leaves the current value unchanged;
        /// an empty list clears it.
        /// </summary>
        public List<SelectedPlaybook>? DefaultPlaybooks { get; set; }
    }
}
