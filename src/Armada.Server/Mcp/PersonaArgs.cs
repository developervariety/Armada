namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
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
        /// Legacy compatibility input. Persona routing now uses MinimumTier.
        /// </summary>
        public bool? Specialist { get; set; }

        /// <summary>Minimum capability tier. An explicit null can clear it on update.</summary>
        public CaptainTierEnum? MinimumTier
        {
            get => _MinimumTier;
            set { _MinimumTier = value; MinimumTierSupplied = true; }
        }

        [JsonIgnore]
        public bool MinimumTierSupplied { get; private set; }

        /// <summary>
        /// Default playbooks for this persona. Null leaves the current value unchanged;
        /// an empty list clears it.
        /// </summary>
        public List<SelectedPlaybook>? DefaultPlaybooks { get; set; }

        private CaptainTierEnum? _MinimumTier;
    }
}
