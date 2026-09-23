namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// A named agent persona that defines a captain's role during a mission.
    /// Personas reference prompt templates that provide role-specific instructions.
    /// </summary>
    public class Persona : IOwnedRecord
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
        /// Owning user. The server records the creating caller.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Who may see the persona inside its tenant. Records that predate ownership are tenant-wide.
        /// </summary>
        public OwnershipScopeEnum OwnershipScope { get; set; } = OwnershipScopeEnum.TenantWide;

        /// <summary>
        /// Persona name (e.g. "Worker", "Architect", "Judge", "Test Engineer").
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
        /// When set, missions assigned to this persona default to this captain unless a voyage-level
        /// override or an explicit RequestedCaptainId takes precedence.
        /// </summary>
        public string? DefaultCaptainId { get; set; } = null;

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
        /// into every mission whose stage runs this persona. Layered between
        /// vessel.DefaultPlaybooks and captain.DefaultPlaybooks during brief assembly.
        /// Use <see cref="GetDefaultPlaybooks"/> to obtain a parsed list.
        /// </summary>
        public string? DefaultPlaybooks { get; set; } = null;

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
        /// Minimum capability tier for missions of this persona. Null leaves the mission request and normal
        /// Legacy Routing order in control. This is an eligibility floor, not a preference for stronger models.
        /// </summary>
        public CaptainTierEnum? MinimumTier { get; set; } = null;

        /// <summary>Compatibility input field. It does not affect routing; databases use it for one-time migration.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Specialist { get; set; } = false;

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
