namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// An extra prompt template seeded from settings. Used for deployment-specific
    /// specialist reviewers that must not be baked into the generic product defaults.
    /// When <see cref="Content"/> is set, it is used as the template body. Otherwise
    /// the specialist-reviewer wrapper is built from <see cref="RoleName"/>,
    /// <see cref="Focus"/>, and <see cref="Checklist"/>.
    /// </summary>
    public class AdditionalPromptTemplateSettings
    {
        #region Public-Members

        /// <summary>
        /// Template name, for example <c>persona.diagnostic_protocol_reviewer</c>.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => _Name = value ?? String.Empty;
        }

        /// <summary>
        /// Human-readable description stored on the template record.
        /// </summary>
        public string Description
        {
            get => _Description;
            set => _Description = value ?? String.Empty;
        }

        /// <summary>
        /// Template category. Defaults to <c>persona</c>.
        /// </summary>
        public string Category
        {
            get => _Category;
            set => _Category = String.IsNullOrWhiteSpace(value) ? "persona" : value.Trim();
        }

        /// <summary>
        /// Role name interpolated into the specialist-reviewer wrapper when
        /// <see cref="Content"/> is empty.
        /// </summary>
        public string RoleName
        {
            get => _RoleName;
            set => _RoleName = value ?? String.Empty;
        }

        /// <summary>
        /// Specialist-focus paragraph used when <see cref="Content"/> is empty.
        /// </summary>
        public string Focus
        {
            get => _Focus;
            set => _Focus = value ?? String.Empty;
        }

        /// <summary>
        /// Review-checklist markdown used when <see cref="Content"/> is empty.
        /// </summary>
        public string Checklist
        {
            get => _Checklist;
            set => _Checklist = value ?? String.Empty;
        }

        /// <summary>
        /// Optional full template body. When set, it replaces the specialist wrapper.
        /// </summary>
        public string? Content { get; set; }

        #endregion

        #region Private-Members

        private string _Name = String.Empty;
        private string _Description = String.Empty;
        private string _Category = "persona";
        private string _RoleName = String.Empty;
        private string _Focus = String.Empty;
        private string _Checklist = String.Empty;

        #endregion
    }
}
