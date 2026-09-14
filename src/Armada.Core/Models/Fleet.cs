namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A named collection of repositories under management.
    /// </summary>
    public class Fleet
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
        /// Fleet name.
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
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Fleet description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Default pipeline to use for dispatches to this fleet.
        /// Vessel setting overrides fleet setting. Null uses WorkerOnly pipeline.
        /// </summary>
        public string? DefaultPipelineId { get; set; } = null;

        /// <summary>
        /// JSON-serialized list of <see cref="SelectedPlaybook"/> entries automatically merged
        /// into every mission whose vessel belongs to this fleet. Layered
        /// FIRST in the four-way merge (least specific): fleet -&gt; vessel -&gt; persona -&gt; captain.
        /// Use <see cref="GetDefaultPlaybooks"/> to obtain a parsed list.
        /// </summary>
        public string? DefaultPlaybooks { get; set; } = null;

        /// <summary>
        /// Whether the fleet is active.
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

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.FleetIdPrefix, 24);
        private string _Name = "My Fleet";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Fleet()
        {
        }

        /// <summary>
        /// Instantiate with name.
        /// </summary>
        /// <param name="name">Fleet name.</param>
        public Fleet(string name)
        {
            Name = name;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Lazy-parses the <see cref="DefaultPlaybooks"/> JSON string. Returns an empty list
        /// when unset or malformed. Mirrors the helper on <see cref="Vessel"/>,
        /// <see cref="Persona"/>, and <see cref="Captain"/>.
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

        #endregion
    }
}
