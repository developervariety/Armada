namespace Armada.Core.Models
{
    /// <summary>
    /// A before/after view of a persona's prompt for a project profile: the base (built-in) prompt and
    /// the effective prompt after the profile's persona override is applied. Feeds the dashboard's live
    /// persona diff editor.
    /// </summary>
    public class PersonaPromptPreview
    {
        /// <summary>
        /// The persona name previewed (e.g. "Architect").
        /// </summary>
        public string PersonaName { get; set; } = string.Empty;

        /// <summary>
        /// The base prompt-template name for the persona.
        /// </summary>
        public string BaseTemplateName { get; set; } = string.Empty;

        /// <summary>
        /// The prompt-template name in effect after the override (equals the base when not overridden).
        /// </summary>
        public string EffectiveTemplateName { get; set; } = string.Empty;

        /// <summary>
        /// The rendered base persona prompt.
        /// </summary>
        public string BasePrompt { get; set; } = string.Empty;

        /// <summary>
        /// The rendered effective persona prompt (with override template and appended instructions).
        /// </summary>
        public string EffectivePrompt { get; set; } = string.Empty;

        /// <summary>
        /// Additional per-project instructions appended by the override, if any.
        /// </summary>
        public string? AdditionalInstructions { get; set; } = null;

        /// <summary>
        /// Whether an enabled override was applied for this persona.
        /// </summary>
        public bool IsOverridden { get; set; } = false;
    }
}
