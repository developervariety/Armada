namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// An extra persona seeded from settings on startup. Built-in product personas
    /// stay in code; deployment-specific reviewers belong here.
    /// </summary>
    public class AdditionalPersonaSettings
    {
        #region Public-Members

        /// <summary>
        /// Persona display name, for example <c>DiagnosticProtocolReviewer</c>.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => _Name = value ?? String.Empty;
        }

        /// <summary>
        /// Persona description stored on the record.
        /// </summary>
        public string Description
        {
            get => _Description;
            set => _Description = value ?? String.Empty;
        }

        /// <summary>
        /// Prompt template name this persona resolves, for example
        /// <c>persona.diagnostic_protocol_reviewer</c>.
        /// </summary>
        public string PromptTemplateName
        {
            get => _PromptTemplateName;
            set => _PromptTemplateName = value ?? String.Empty;
        }

        #endregion

        #region Private-Members

        private string _Name = String.Empty;
        private string _Description = String.Empty;
        private string _PromptTemplateName = String.Empty;

        #endregion
    }
}
