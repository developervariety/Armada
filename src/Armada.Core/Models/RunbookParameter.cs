namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Named parameter substituted into a runbook execution.
    /// </summary>
    public class RunbookParameter
    {
        /// <summary>
        /// Parameter name.
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Name));
                _Name = value.Trim();
            }
        }

        /// <summary>
        /// Human-facing label.
        /// </summary>
        public string? Label { get; set; } = null;

        /// <summary>
        /// Optional description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Optional default value.
        /// </summary>
        public string? DefaultValue { get; set; } = null;

        /// <summary>
        /// Whether the parameter is required.
        /// </summary>
        public bool Required { get; set; } = false;

        private string _Name = "parameter";
    }
}
