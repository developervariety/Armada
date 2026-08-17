namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A per-project customization of a pipeline persona. Points a persona (by name) at a different
    /// prompt template and/or layers additional instructions and context onto it, without mutating the
    /// shared built-in persona definitions.
    /// </summary>
    public class PersonaOverride
    {
        /// <summary>
        /// The persona name this override applies to (e.g. "Architect", "Worker", "Test Engineer").
        /// Matches <see cref="Persona.Name"/>.
        /// </summary>
        public string PersonaName
        {
            get => _PersonaName;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(PersonaName));
                _PersonaName = value.Trim();
            }
        }

        /// <summary>
        /// Optional prompt-template name to use for this persona instead of its default. Matches
        /// <see cref="PromptTemplate.Name"/>. Null leaves the persona's default template in place.
        /// </summary>
        public string? PromptTemplateName { get; set; } = null;

        /// <summary>
        /// Optional additional instructions appended to the persona's resolved prompt for this project.
        /// </summary>
        public string? AdditionalInstructions { get; set; } = null;

        /// <summary>
        /// Whether this override is enabled. Disabled overrides are retained but not applied.
        /// </summary>
        public bool Enabled { get; set; } = true;

        private string _PersonaName = String.Empty;
    }
}
