namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One executable or checklist step within a runbook.
    /// </summary>
    public class RunbookStep
    {
        /// <summary>
        /// Stable step identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value.Trim();
            }
        }

        /// <summary>
        /// Short step title.
        /// </summary>
        public string Title
        {
            get => _Title;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Title));
                _Title = value.Trim();
            }
        }

        /// <summary>
        /// Step instructions.
        /// </summary>
        public string Instructions { get; set; } = String.Empty;

        private string _Id = Constants.IdGenerator.GenerateKSortable("rbs_", 24);
        private string _Title = "Step";
    }
}
