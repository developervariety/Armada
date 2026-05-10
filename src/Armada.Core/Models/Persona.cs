namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A named agent persona that defines a captain's role during a mission.
    /// Personas reference prompt templates that provide role-specific instructions.
    /// </summary>
    public class Persona
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Persona name (e.g. "Worker", "Architect", "Judge", "TestEngineer").
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Name));
                _Name = value;
            }
        }

        /// <summary>
        /// Human-readable description of what this persona does.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Name of the prompt template used by this persona (references PromptTemplate.Name).
        /// </summary>
        public string PromptTemplateName
        {
            get => _PromptTemplateName;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(PromptTemplateName));
                _PromptTemplateName = value;
            }
        }

        /// <summary>
        /// Whether this is a built-in system persona. Built-in personas cannot be deleted.
        /// </summary>
        public bool IsBuiltIn { get; set; } = false;

        /// <summary>
        /// JSON-serialized list of <see cref="SelectedPlaybook"/> entries automatically merged
        /// into every mission whose stage runs this persona (Reflections v2-F2). Layered between
        /// vessel.DefaultPlaybooks and captain.DefaultPlaybooks during brief assembly.
        /// Use <see cref="GetDefaultPlaybooks"/> to obtain a parsed list.
        /// </summary>
        public string? DefaultPlaybooks { get; set; } = null;

        /// <summary>
        /// Per-persona persona-curate trigger threshold (mission-count window since last
        /// accepted persona-curate). Null disables the audit-drain auto-trigger for this
        /// persona (Reflections v2-F2).
        /// </summary>
        public int? CurateThreshold { get; set; } = null;

        /// <summary>
        /// FK reference to the persona-&lt;name&gt;-learned playbook. Set at v2-F2 install bootstrap
        /// (or at first <c>PersonaSeedService</c> seed for personas added after install).
        /// Null means the persona has no learned playbook yet.
        /// </summary>
        public string? LearnedPlaybookId { get; set; } = null;

        /// <summary>
        /// Lazy-parses the <see cref="DefaultPlaybooks"/> JSON string. Returns an empty list when unset or malformed.
        /// </summary>
        /// <returns>List of <see cref="SelectedPlaybook"/> entries.</returns>
        public List<SelectedPlaybook> GetDefaultPlaybooks()
        {
            if (String.IsNullOrWhiteSpace(DefaultPlaybooks)) return new List<SelectedPlaybook>();
            try
            {
                List<SelectedPlaybook>? list = System.Text.Json.JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                    DefaultPlaybooks,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return list ?? new List<SelectedPlaybook>();
            }
            catch
            {
                return new List<SelectedPlaybook>();
            }
        }

        /// <summary>
        /// Whether the persona is active.
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable("prs_", 24);
        private string _Name = "Worker";
        private string _PromptTemplateName = "persona.worker";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Persona()
        {
        }

        /// <summary>
        /// Instantiate with name and prompt template.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <param name="promptTemplateName">Prompt template name.</param>
        public Persona(string name, string promptTemplateName)
        {
            Name = name;
            PromptTemplateName = promptTemplateName;
        }

        #endregion
    }
}
